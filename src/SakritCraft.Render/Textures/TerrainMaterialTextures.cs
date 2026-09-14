using System.Diagnostics;
using System.Numerics;
using SakritCraft.Content.Textures;
using SakritCraft.Render.Descriptors;
using SakritCraft.Render.Memory;
using SakritCraft.Render.Upload;
using SakritCraft.Render.Vulkan;
using Silk.NET.Vulkan;

namespace SakritCraft.Render.Textures;

/// <summary>
/// The terrain material textures on the GPU: three layered images holding base colour,
/// tangent-space normal, and packed occlusion, roughness and metalness, one array layer
/// per material.
/// <para>
/// Layered images rather than eighteen separate textures, because the shader picks a
/// material per fragment and indexing an array layer costs nothing while switching
/// between bindless handles in a loop does not vectorise. The array view is registered
/// as an ordinary sampled image; the shader declares a <c>texture2DArray</c> alias on
/// the same binding, which Vulkan permits since the descriptor type is unchanged.
/// </para>
/// </summary>
public sealed unsafe class TerrainMaterialTextures : IDisposable
{
    private const string Tag = "textures";

    /// <summary>Bumped whenever a recipe changes, so stale cached bakes are discarded.</summary>
    private const int CacheVersion = 3;

    private readonly VulkanDevice _device;
    private readonly DescriptorHeap _heap;

    private readonly GpuImage _albedo;
    private readonly GpuImage _normal;
    private readonly GpuImage _orm;
    private readonly Sampler _sampler;

    public BindlessHandle AlbedoHandle { get; }
    public BindlessHandle NormalHandle { get; }
    public BindlessHandle OrmHandle { get; }
    public BindlessHandle SamplerHandle { get; }

    public uint LayerCount { get; }
    public uint MipLevels { get; }
    public int Resolution { get; }
    public ulong VramBytes => _albedo.AllocatedBytes + _normal.AllocatedBytes + _orm.AllocatedBytes;

    public TerrainMaterialTextures(VulkanDevice device, GpuAllocator allocator, DescriptorHeap heap,
                                   TransferUploader uploader, int resolution, string? cacheDirectory)
    {
        _device = device;
        _heap = heap;
        Resolution = resolution;
        LayerCount = (uint)TerrainTextureBaker.Layers.Length;
        MipLevels = (uint)(int)(Math.Log2(resolution) + 1);

        MaterialTextureSet[] sets = BakeAll(resolution, cacheDirectory);

        _albedo = CreateArray(device, allocator, resolution, Format.R8G8B8A8Srgb, "Terrain.Albedo");
        _normal = CreateArray(device, allocator, resolution, Format.R8G8B8A8Unorm, "Terrain.Normal");
        _orm = CreateArray(device, allocator, resolution, Format.R8G8B8A8Unorm, "Terrain.Orm");

        Upload(uploader, _albedo, sets, MapKind.Albedo);
        Upload(uploader, _normal, sets, MapKind.Normal);
        Upload(uploader, _orm, sets, MapKind.Orm);

        _sampler = CreateSampler(device, MipLevels);

        AlbedoHandle = heap.RegisterSampledImage(_albedo.View, ImageLayout.ShaderReadOnlyOptimal);
        NormalHandle = heap.RegisterSampledImage(_normal.View, ImageLayout.ShaderReadOnlyOptimal);
        OrmHandle = heap.RegisterSampledImage(_orm.View, ImageLayout.ShaderReadOnlyOptimal);
        SamplerHandle = heap.RegisterSampler(_sampler);

        RenderLog.Info(Tag,
            $"Terrain materials: {LayerCount} layers at {resolution}^2, {MipLevels} mips, " +
            $"{VramBytes / (1024 * 1024)} MiB VRAM");
    }

    private enum MapKind { Albedo, Normal, Orm }

    // ── Baking, with a disk cache ────────────────────────────────────────────────

    private static MaterialTextureSet[] BakeAll(int resolution, string? cacheDirectory)
    {
        var sw = Stopwatch.StartNew();
        var sets = new MaterialTextureSet[TerrainTextureBaker.Layers.Length];

        string? cachePath = cacheDirectory is null
            ? null
            : Path.Combine(cacheDirectory, $"terrain-materials-v{CacheVersion}-{resolution}.bin");

        if (cachePath is not null && TryLoadCache(cachePath, resolution, sets))
        {
            RenderLog.Info(Tag, $"Loaded baked materials from cache in {sw.ElapsedMilliseconds} ms");
            return sets;
        }

        Parallel.For(0, sets.Length, i =>
        {
            sets[i] = TerrainTextureBaker.Bake(TerrainTextureBaker.Layers[i], resolution);
        });

        RenderLog.Info(Tag, $"Baked {sets.Length} materials at {resolution}^2 in {sw.ElapsedMilliseconds} ms");

        if (cachePath is not null)
        {
            try
            {
                Directory.CreateDirectory(Path.GetDirectoryName(cachePath)!);
                using var fs = File.Create(cachePath);
                foreach (MaterialTextureSet set in sets)
                {
                    fs.Write(set.Albedo);
                    fs.Write(set.Normal);
                    fs.Write(set.Orm);
                }
            }
            catch (IOException e)
            {
                RenderLog.Warn(Tag, $"Could not write the material cache: {e.Message}");
            }
        }

        return sets;
    }

    private static bool TryLoadCache(string path, int resolution, MaterialTextureSet[] sets)
    {
        try
        {
            if (!File.Exists(path)) return false;

            long perMap = (long)resolution * resolution * 4;
            long expected = perMap * 3 * sets.Length;
            using var fs = File.OpenRead(path);
            if (fs.Length != expected) return false;

            for (int i = 0; i < sets.Length; i++)
            {
                var set = new MaterialTextureSet(resolution);
                fs.ReadExactly(set.Albedo);
                fs.ReadExactly(set.Normal);
                fs.ReadExactly(set.Orm);
                sets[i] = set;
            }
            return true;
        }
        catch (IOException)
        {
            return false;
        }
    }

    // ── GPU resources ────────────────────────────────────────────────────────────

    private GpuImage CreateArray(VulkanDevice device, GpuAllocator allocator, int resolution,
                                 Format format, string name) =>
        new(device, allocator, new GpuImageDesc
        {
            Name = name,
            Width = (uint)resolution,
            Height = (uint)resolution,
            Format = format,
            Usage = ImageUsageFlags.SampledBit | ImageUsageFlags.TransferDstBit,
            MipLevels = MipLevels,
            ArrayLayers = LayerCount,
            ViewType = ImageViewType.Type2DArray,
        });

    private void Upload(TransferUploader uploader, GpuImage image, MaterialTextureSet[] sets, MapKind kind)
    {
        // Build the whole mip chain on the CPU. Generating it on the GPU would mean
        // blits on the graphics queue, which defeats the point of uploading terrain data
        // on the dedicated transfer queue in the first place.
        byte[][] layerChains = new byte[sets.Length][];
        Parallel.For(0, sets.Length, i =>
        {
            byte[] level0 = kind switch
            {
                MapKind.Albedo => sets[i].Albedo,
                MapKind.Normal => sets[i].Normal,
                _ => sets[i].Orm,
            };
            layerChains[i] = BuildMipChain(level0, Resolution, kind);
        });

        ulong total = 0;
        for (uint m = 0; m < MipLevels; m++)
        {
            uint size = MipByteSize(m);
            total += size * LayerCount;
        }

        uploader.BeginBatch();
        if (!uploader.TryStage(total, 16, out StagingSlice slice))
        {
            throw new InvalidOperationException(
                $"Staging ring cannot hold {total / (1024 * 1024)} MiB of terrain material data. " +
                "Either raise StagingRingBytes or lower the material texture resolution.");
        }

        // Layout: for each mip level, all six layers contiguously. One copy region per
        // level then covers every layer, which is what layerCount in the region means.
        var regions = new BufferImageCopy[MipLevels];
        Span<byte> dst = slice.Span;
        ulong cursor = 0;

        for (uint m = 0; m < MipLevels; m++)
        {
            uint levelSize = MipByteSize(m);
            uint dim = Math.Max(1u, (uint)Resolution >> (int)m);

            regions[m] = new BufferImageCopy
            {
                BufferOffset = cursor,
                BufferRowLength = 0,
                BufferImageHeight = 0,
                ImageSubresource = new ImageSubresourceLayers(image.Aspect, m, 0, LayerCount),
                ImageOffset = new Offset3D(0, 0, 0),
                ImageExtent = new Extent3D(dim, dim, 1),
            };

            for (int layer = 0; layer < sets.Length; layer++)
            {
                int sourceOffset = MipChainOffset(m);
                layerChains[layer].AsSpan(sourceOffset, (int)levelSize)
                                  .CopyTo(dst.Slice((int)cursor, (int)levelSize));
                cursor += levelSize;
            }
        }

        uploader.CopyToImageRegions(slice, image, regions, ImageLayout.ShaderReadOnlyOptimal,
            PipelineStageFlags2.FragmentShaderBit, AccessFlags2.ShaderSampledReadBit);
        uploader.EndBatch();
    }

    private uint MipByteSize(uint level)
    {
        uint dim = Math.Max(1u, (uint)Resolution >> (int)level);
        return dim * dim * 4;
    }

    private int MipChainOffset(uint level)
    {
        int offset = 0;
        for (uint m = 0; m < level; m++) offset += (int)MipByteSize(m);
        return offset;
    }

    /// <summary>
    /// Box-filters a texture down to a full mip chain, packed head to tail.
    /// <para>
    /// The filter is kind-aware, which matters. Base colour is averaged in linear space
    /// and re-encoded, because averaging sRGB bytes directly darkens every distant
    /// surface. Normals are averaged then renormalised, since the mean of unit vectors
    /// is not a unit vector and an unnormalised normal map dims the lighting with
    /// distance.
    /// </para>
    /// </summary>
    private static byte[] BuildMipChain(byte[] level0, int resolution, MapKind kind)
    {
        int total = 0;
        for (int dim = resolution; dim >= 1; dim >>= 1) total += dim * dim * 4;

        var chain = new byte[total];
        level0.AsSpan(0, resolution * resolution * 4).CopyTo(chain);

        int srcOffset = 0;
        int srcDim = resolution;
        int dstOffset = resolution * resolution * 4;

        while (srcDim > 1)
        {
            int dstDim = srcDim >> 1;
            for (int y = 0; y < dstDim; y++)
            {
                for (int x = 0; x < dstDim; x++)
                {
                    int s00 = srcOffset + ((y * 2) * srcDim + (x * 2)) * 4;
                    int s10 = srcOffset + ((y * 2) * srcDim + (x * 2 + 1)) * 4;
                    int s01 = srcOffset + ((y * 2 + 1) * srcDim + (x * 2)) * 4;
                    int s11 = srcOffset + ((y * 2 + 1) * srcDim + (x * 2 + 1)) * 4;
                    int d = dstOffset + (y * dstDim + x) * 4;

                    switch (kind)
                    {
                        case MapKind.Albedo:
                            for (int c = 0; c < 3; c++)
                            {
                                float sum = SrgbToLinear(chain[s00 + c]) + SrgbToLinear(chain[s10 + c])
                                          + SrgbToLinear(chain[s01 + c]) + SrgbToLinear(chain[s11 + c]);
                                chain[d + c] = LinearToSrgbByte(sum * 0.25f);
                            }
                            chain[d + 3] = 255;
                            break;

                        case MapKind.Normal:
                            {
                                var n = Vector3.Zero;
                                foreach (int s in stackalloc[] { s00, s10, s01, s11 })
                                {
                                    n += new Vector3(
                                        chain[s + 0] / 255.0f * 2.0f - 1.0f,
                                        chain[s + 1] / 255.0f * 2.0f - 1.0f,
                                        chain[s + 2] / 255.0f * 2.0f - 1.0f);
                                }
                                n = n.LengthSquared() > 1e-8f ? Vector3.Normalize(n) : Vector3.UnitZ;
                                chain[d + 0] = (byte)Math.Clamp((n.X * 0.5f + 0.5f) * 255.0f + 0.5f, 0, 255);
                                chain[d + 1] = (byte)Math.Clamp((n.Y * 0.5f + 0.5f) * 255.0f + 0.5f, 0, 255);
                                chain[d + 2] = (byte)Math.Clamp((n.Z * 0.5f + 0.5f) * 255.0f + 0.5f, 0, 255);
                                chain[d + 3] = 255;
                                break;
                            }

                        default:
                            for (int c = 0; c < 4; c++)
                            {
                                chain[d + c] = (byte)((chain[s00 + c] + chain[s10 + c]
                                                     + chain[s01 + c] + chain[s11 + c] + 2) >> 2);
                            }
                            break;
                    }
                }
            }

            srcOffset = dstOffset;
            srcDim = dstDim;
            dstOffset += dstDim * dstDim * 4;
        }

        return chain;
    }

    private static float SrgbToLinear(byte v)
    {
        float c = v / 255.0f;
        return c <= 0.04045f ? c / 12.92f : MathF.Pow((c + 0.055f) / 1.055f, 2.4f);
    }

    private static byte LinearToSrgbByte(float c)
    {
        c = Math.Clamp(c, 0.0f, 1.0f);
        float e = c <= 0.0031308f ? c * 12.92f : 1.055f * MathF.Pow(c, 1.0f / 2.4f) - 0.055f;
        return (byte)Math.Clamp(e * 255.0f + 0.5f, 0.0f, 255.0f);
    }

    private static Sampler CreateSampler(VulkanDevice device, uint mipLevels)
    {
        bool aniso = device.Capabilities.SamplerAnisotropy;
        float maxAniso = Math.Min(16.0f, device.Properties.Limits.MaxSamplerAnisotropy);

        var info = new SamplerCreateInfo
        {
            SType = StructureType.SamplerCreateInfo,
            MagFilter = Filter.Linear,
            MinFilter = Filter.Linear,
            MipmapMode = SamplerMipmapMode.Linear,
            AddressModeU = SamplerAddressMode.Repeat,
            AddressModeV = SamplerAddressMode.Repeat,
            AddressModeW = SamplerAddressMode.Repeat,
            // Anisotropy matters more here than almost anywhere else: terrain is viewed
            // at grazing angles for most of the screen, which is precisely where
            // isotropic filtering blurs the ground into mush.
            AnisotropyEnable = aniso,
            MaxAnisotropy = aniso ? maxAniso : 1.0f,
            MinLod = 0.0f,
            MaxLod = mipLevels,
            BorderColor = BorderColor.FloatOpaqueBlack,
        };

        Sampler sampler;
        device.Vk.CreateSampler(device.Device, &info, null, &sampler).Check("vkCreateSampler(terrain)");
        device.SetObjectName(ObjectType.Sampler, sampler.Handle, "Sampler.TerrainAniso");
        return sampler;
    }

    public void Dispose()
    {
        _heap.Free(AlbedoHandle);
        _heap.Free(NormalHandle);
        _heap.Free(OrmHandle);
        _heap.Free(SamplerHandle);
        _device.Vk.DestroySampler(_device.Device, _sampler, null);
        _albedo.Dispose();
        _normal.Dispose();
        _orm.Dispose();
    }
}
