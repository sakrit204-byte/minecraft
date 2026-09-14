using System.Numerics;
using System.Runtime.InteropServices;
using SakritCraft.Render.Cameras;
using SakritCraft.Render.Descriptors;
using SakritCraft.Render.Memory;
using SakritCraft.Render.Pipelines;
using SakritCraft.Render.Vulkan;
using Silk.NET.Vulkan;

namespace SakritCraft.Render.Shadows;

/// <summary>Push constants for the depth-only cascade pass. 96 bytes, scalar layout.</summary>
[StructLayout(LayoutKind.Sequential, Size = 96)]
public struct ShadowPushConstants
{
    public uint VertexBuffer;
    public uint Pad0;
    public uint Pad1;
    public uint Pad2;
    public Vector3 ChunkOffset;
    public float Pad3;
    public Matrix4x4 LightViewProj;
}

/// <summary>
/// Per-frame shadow data the lighting shader reads. Kept in its own storage buffer rather
/// than in the frame constants, because four matrices would more than double that struct
/// for the benefit of a single pass.
/// </summary>
[StructLayout(LayoutKind.Sequential, Size = 320)]
public struct ShadowConstants
{
    public Matrix4x4 Cascade0;
    public Matrix4x4 Cascade1;
    public Matrix4x4 Cascade2;
    public Matrix4x4 Cascade3;
    /// <summary>View-space distance at which each cascade ends.</summary>
    public Vector4 SplitDistances;
    /// <summary>World size of one shadow texel per cascade, for slope-scaled bias.</summary>
    public Vector4 TexelWorldSize;
    public uint ShadowMap;
    public uint ShadowSampler;
    public float MaxDistance;
    public float Strength;
    /// <summary>One over the shadow map edge length, so the shader sizes its filter without a
    /// samplerless textureSize query (which would need an extra GLSL extension).</summary>
    public float InvResolution;
}

/// <summary>
/// Cascaded shadow maps for the sun.
/// <para>
/// A layered depth image, one layer per cascade, rendered depth-only from the light and
/// then sampled with hardware comparison filtering. The ray-query path in §08 of the
/// design will eventually supersede this for machines that have the extension, but this
/// is the path that must exist regardless: the fallback GPU on this very machine has no
/// ray-tracing cores.
/// </para>
/// </summary>
public sealed unsafe class ShadowRenderer : IDisposable
{
    private const string Tag = "shadows";

    private readonly VulkanDevice _device;
    private readonly DescriptorHeap _heap;
    private readonly GpuImage _depth;
    private readonly ImageView[] _layerViews = new ImageView[ShadowCascades.Count];
    private readonly Sampler _sampler;
    private readonly GraphicsPipeline _pipeline;
    private readonly GpuBuffer[] _constantBuffers;
    private readonly BindlessHandle[] _constantHandles;
    private readonly BindlessHandle _mapHandle;
    private readonly BindlessHandle _samplerHandle;

    private bool _everRendered;

    public ShadowCascades Cascades { get; } = new();
    public uint Resolution { get; }

    /// <summary>How dark a fully shadowed surface becomes. One means the sun is fully blocked.</summary>
    public float Strength { get; set; } = 1.0f;

    /// <summary>Furthest distance cascades cover. Beyond this the sun is treated as unoccluded.</summary>
    public float MaxDistance { get; set; } = 900.0f;

    public ulong VramBytes => _depth.AllocatedBytes;

    public ShadowRenderer(VulkanDevice device, GpuAllocator allocator, DescriptorHeap heap,
                          SakritCraft.Render.Pipelines.PipelineCache pipelines, uint resolution, int framesInFlight)
    {
        _device = device;
        _heap = heap;
        Resolution = resolution;

        Format format = FormatSupport.ChooseShadowFormat(device);

        _depth = new GpuImage(device, allocator, new GpuImageDesc
        {
            Name = "Shadow.Cascades",
            Width = resolution,
            Height = resolution,
            Format = format,
            Usage = ImageUsageFlags.DepthStencilAttachmentBit | ImageUsageFlags.SampledBit,
            ArrayLayers = ShadowCascades.Count,
            ViewType = ImageViewType.Type2DArray,
        });

        // One single-layer view per cascade, because dynamic rendering attaches exactly one layer.
        for (int i = 0; i < ShadowCascades.Count; i++)
        {
            var info = new ImageViewCreateInfo
            {
                SType = StructureType.ImageViewCreateInfo,
                Image = _depth.Handle,
                ViewType = ImageViewType.Type2D,
                Format = format,
                SubresourceRange = new ImageSubresourceRange(ImageAspectFlags.DepthBit, 0, 1, (uint)i, 1),
            };
            ImageView view;
            device.Vk.CreateImageView(device.Device, &info, null, &view).Check("vkCreateImageView(shadow layer)");
            device.SetObjectName(ObjectType.ImageView, view.Handle, $"Shadow.Cascade{i}");
            _layerViews[i] = view;
        }

        _sampler = CreateComparisonSampler(device);

        _pipeline = pipelines.CreateGraphics(new GraphicsPipelineDesc
        {
            Name = "Terrain.Shadow",
            VertexShader = "terrain_shadow.vert",
            FragmentShader = string.Empty,          // depth only
            ColorFormat = Format.Undefined,
            DepthFormat = format,
            DepthTest = true,
            DepthWrite = true,
            DepthCompare = CompareOp.LessOrEqual,   // conventional depth here, unlike the reverse-Z main pass
            CullMode = CullModeFlags.None,
            // Terrain is a closed surface meshed from a field, so front-face culling would be the
            // usual answer to acne. It is avoided here because cascades also cover thin overhangs
            // and arches, where culling a face removes a caster entirely. Bias handles it instead.
            DepthBiasConstant = 1.75f,
            DepthBiasSlope = 3.0f,
        });

        _constantBuffers = new GpuBuffer[framesInFlight];
        _constantHandles = new BindlessHandle[framesInFlight];
        for (int i = 0; i < framesInFlight; i++)
        {
            _constantBuffers[i] = new GpuBuffer(device, allocator, (ulong)sizeof(ShadowConstants),
                BufferUsageFlags.StorageBufferBit, MemoryUsage.Staging, $"Shadow.Constants{i}");
            _constantHandles[i] = heap.RegisterStorageBuffer(_constantBuffers[i].Handle, 0, _constantBuffers[i].Size);
        }

        _mapHandle = heap.RegisterSampledImage(_depth.View, ImageLayout.ShaderReadOnlyOptimal);
        _samplerHandle = heap.RegisterSampler(_sampler);

        RenderLog.Info(Tag, $"Cascaded shadows: {ShadowCascades.Count} x {resolution}^2 {format}, " +
                            $"{VramBytes / (1024 * 1024)} MiB VRAM");
    }

    /// <summary>Bindless handle of this frame's shadow constants, for the lighting pass.</summary>
    public uint ConstantsHandle(int frameIndex) => _constantHandles[frameIndex].Index;

    /// <summary>
    /// Renders every cascade and uploads the constants the lighting pass will read.
    /// </summary>
    public void Render(CommandBuffer cmd, Camera camera, Vector3 sunDirection, int frameIndex,
                       Action<CommandBuffer, Matrix4x4, Pipeline> drawCasters)
    {
        Cascades.Update(camera, sunDirection, Resolution, MaxDistance);

        _device.BeginLabel(cmd, "Shadow cascades");
        var vk = _device.Vk;

        // Whole image to depth-attachment layout in one barrier.
        TransitionAll(cmd,
            _everRendered ? ImageLayout.ShaderReadOnlyOptimal : ImageLayout.Undefined,
            ImageLayout.DepthAttachmentOptimal,
            _everRendered ? PipelineStageFlags2.FragmentShaderBit : PipelineStageFlags2.TopOfPipeBit,
            _everRendered ? AccessFlags2.ShaderSampledReadBit : AccessFlags2.None,
            PipelineStageFlags2.EarlyFragmentTestsBit | PipelineStageFlags2.LateFragmentTestsBit,
            AccessFlags2.DepthStencilAttachmentWriteBit);

        var viewport = new Viewport(0, 0, Resolution, Resolution, 0.0f, 1.0f);
        var scissor = new Rect2D(new Offset2D(0, 0), new Extent2D(Resolution, Resolution));

        for (int i = 0; i < ShadowCascades.Count; i++)
        {
            var attachment = new RenderingAttachmentInfo
            {
                SType = StructureType.RenderingAttachmentInfo,
                ImageView = _layerViews[i],
                ImageLayout = ImageLayout.DepthAttachmentOptimal,
                LoadOp = AttachmentLoadOp.Clear,
                StoreOp = AttachmentStoreOp.Store,
                ClearValue = new ClearValue { DepthStencil = new ClearDepthStencilValue(1.0f, 0) },
            };
            var rendering = new RenderingInfo
            {
                SType = StructureType.RenderingInfo,
                RenderArea = scissor,
                LayerCount = 1,
                ColorAttachmentCount = 0,
                PDepthAttachment = &attachment,
            };

            vk.CmdBeginRendering(cmd, &rendering);
            vk.CmdSetViewport(cmd, 0, 1, &viewport);
            vk.CmdSetScissor(cmd, 0, 1, &scissor);
            drawCasters(cmd, Cascades.Matrices[i], _pipeline.Handle);
            vk.CmdEndRendering(cmd);
        }

        TransitionAll(cmd,
            ImageLayout.DepthAttachmentOptimal, ImageLayout.ShaderReadOnlyOptimal,
            PipelineStageFlags2.LateFragmentTestsBit, AccessFlags2.DepthStencilAttachmentWriteBit,
            PipelineStageFlags2.FragmentShaderBit, AccessFlags2.ShaderSampledReadBit);

        _everRendered = true;
        _device.EndLabel(cmd);

        WriteConstants(frameIndex);
    }

    private void WriteConstants(int frameIndex)
    {
        var c = new ShadowConstants
        {
            Cascade0 = Cascades.Matrices[0],
            Cascade1 = Cascades.Matrices[1],
            Cascade2 = Cascades.Matrices[2],
            Cascade3 = Cascades.Matrices[3],
            SplitDistances = new Vector4(Cascades.Splits[0], Cascades.Splits[1], Cascades.Splits[2], Cascades.Splits[3]),
            TexelWorldSize = new Vector4(Cascades.TexelWorldSize[0], Cascades.TexelWorldSize[1],
                                         Cascades.TexelWorldSize[2], Cascades.TexelWorldSize[3]),
            ShadowMap = _mapHandle.Index,
            ShadowSampler = _samplerHandle.Index,
            MaxDistance = Cascades.MaxDistance,
            Strength = Strength,
            InvResolution = 1.0f / Resolution,
        };
        _constantBuffers[frameIndex].Write(MemoryMarshal.CreateReadOnlySpan(ref c, 1));
    }

    private void TransitionAll(CommandBuffer cmd, ImageLayout from, ImageLayout to,
                               PipelineStageFlags2 srcStage, AccessFlags2 srcAccess,
                               PipelineStageFlags2 dstStage, AccessFlags2 dstAccess)
    {
        var barrier = new ImageMemoryBarrier2
        {
            SType = StructureType.ImageMemoryBarrier2,
            SrcStageMask = srcStage,
            SrcAccessMask = srcAccess,
            DstStageMask = dstStage,
            DstAccessMask = dstAccess,
            OldLayout = from,
            NewLayout = to,
            SrcQueueFamilyIndex = Vk.QueueFamilyIgnored,
            DstQueueFamilyIndex = Vk.QueueFamilyIgnored,
            Image = _depth.Handle,
            SubresourceRange = new ImageSubresourceRange(ImageAspectFlags.DepthBit, 0, 1, 0, ShadowCascades.Count),
        };
        var dep = new DependencyInfo
        {
            SType = StructureType.DependencyInfo,
            ImageMemoryBarrierCount = 1,
            PImageMemoryBarriers = &barrier,
        };
        _device.Vk.CmdPipelineBarrier2(cmd, &dep);
    }

    private static Sampler CreateComparisonSampler(VulkanDevice device)
    {
        // Comparison filtering: the hardware tests depth against the reference and returns the
        // filtered fraction of passing texels, which gives four-tap smoothing for the price of one
        // fetch. Clamping to a white border means anything sampled outside a cascade reads as lit,
        // which is the correct answer for geometry the light frustum never covered.
        var info = new SamplerCreateInfo
        {
            SType = StructureType.SamplerCreateInfo,
            MagFilter = Filter.Linear,
            MinFilter = Filter.Linear,
            MipmapMode = SamplerMipmapMode.Nearest,
            AddressModeU = SamplerAddressMode.ClampToBorder,
            AddressModeV = SamplerAddressMode.ClampToBorder,
            AddressModeW = SamplerAddressMode.ClampToBorder,
            BorderColor = BorderColor.FloatOpaqueWhite,
            CompareEnable = true,
            CompareOp = CompareOp.LessOrEqual,
            MinLod = 0.0f,
            MaxLod = 0.0f,
        };

        Sampler sampler;
        device.Vk.CreateSampler(device.Device, &info, null, &sampler).Check("vkCreateSampler(shadow)");
        device.SetObjectName(ObjectType.Sampler, sampler.Handle, "Sampler.ShadowCompare");
        return sampler;
    }

    public void Dispose()
    {
        _heap.Free(_mapHandle);
        _heap.Free(_samplerHandle);
        foreach (var h in _constantHandles) _heap.Free(h);
        foreach (var b in _constantBuffers) b.Dispose();
        foreach (var v in _layerViews) _device.Vk.DestroyImageView(_device.Device, v, null);
        _device.Vk.DestroySampler(_device.Device, _sampler, null);
        _depth.Dispose();
    }
}
