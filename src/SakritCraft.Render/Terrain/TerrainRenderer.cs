using System.Numerics;
using System.Runtime.CompilerServices;
using System.Runtime.InteropServices;
using SakritCraft.Mesh;
using SakritCraft.Render.Cameras;
using SakritCraft.Render.Descriptors;
using SakritCraft.Render.Frame;
using SakritCraft.Render.Memory;
using SakritCraft.Render.Pipelines;
using SakritCraft.Render.Upload;
using SakritCraft.Render.Vulkan;
using SakritCraft.World.Generation;
using Silk.NET.Maths;
using Silk.NET.Vulkan;
using PipelineCache = SakritCraft.Render.Pipelines.PipelineCache;

namespace SakritCraft.Render.Terrain;

/// <summary>
/// Owns every chunk the renderer knows about and draws the resident ones. A chunk moves through the
/// state machine of docs/MASTER-PLAN.html section 06 (queued, generating, meshing, uploading,
/// resident); the first three live inside <see cref="ChunkGenerator"/>, the last two here.
///
/// <para><b>Per frame</b>, in this order: <see cref="PumpUploads"/> drains finished meshes into staging
/// and records copies on the transfer queue; <see cref="PromoteAcquired"/> flips chunks whose acquire
/// barrier has been recorded to resident; <see cref="Draw"/> culls each resident chunk's bounds against
/// the frustum and issues one indexed draw per survivor. All of it is allocation-free.</para>
///
/// <para><b>GPU layout.</b> Vertices are 16 bytes (position + packed normal/material) in a bindless
/// vertex pool; indices are 32-bit in an index pool. The vertex shader reads the pool through the
/// chunk's pool handle and <c>gl_VertexIndex</c>, which already includes the draw's vertex offset,
/// so a chunk draw is exactly the tuple an indirect command would hold.</para>
///
/// <para><b>Precision.</b> Chunk origins are doubles. Each draw subtracts the camera position in double
/// and pushes the float remainder, so the shader only ever sees positions within a few hundred metres
/// of zero (section 03).</para>
///
/// M2 generates one fixed region at start-up. Dynamic streaming adds a request/release policy on top
/// of <see cref="Request"/> and <see cref="Release"/>; nothing below them needs to change.
/// </summary>
public sealed unsafe class TerrainRenderer : IDisposable
{
    private const string Tag = "terrain";

    /// <summary>GPU vertex: mirrors <c>Vertex</c> in shaders/terrain.vert (scalar layout, 16 bytes).</summary>
    [StructLayout(LayoutKind.Sequential, Size = 16)]
    private struct GpuTerrainVertex
    {
        public Vector3 Position;
        /// <summary>Bytes 0-2: snorm8 normal xyz. Byte 3: material id.</summary>
        public uint NormalMaterial;
    }

    /// <summary>Mirrors the push_constant block in shaders/terrain.vert and terrain.frag (scalar layout, 32 bytes).</summary>
    [StructLayout(LayoutKind.Sequential, Size = 32)]
    private struct TerrainPushConstants
    {
        public uint FrameHandle;
        public uint VertexBuffer;
        public uint Pad0;
        public uint Pad1;
        public Vector3 ChunkOffset;
        public float Pad2;
    }

    private enum ChunkState : byte
    {
        /// <summary>Queued with the generator or waiting for staging space.</summary>
        Pending,
        /// <summary>Copies submitted on the transfer queue; not yet acquired by graphics.</summary>
        Uploading,
        Resident,
        /// <summary>Generated, no triangles. Kept so streaming does not re-request it.</summary>
        Empty,
        Released,
    }

    private sealed class ChunkRecord
    {
        public ChunkCoord Coord;
        public Vector3D<double> Origin;
        public ChunkState State;
        public GeometryRange Vertices;
        public GeometryRange Indices;
        public uint VertexCount;
        public uint IndexCount;
        public Vector3 BoundsMin;
        public Vector3 BoundsMax;
        public ulong UploadTicket;
    }

    private struct DeferredFree
    {
        public GeometryRange Vertices;
        public GeometryRange Indices;
        public ulong RetireAfter;
    }

    private static readonly ulong VertexStride = (ulong)sizeof(GpuTerrainVertex);
    private const ulong IndexStride = sizeof(uint);

    private readonly VulkanDevice _device;
    private readonly DescriptorHeap _heap;
    private readonly TerrainOptions _options;
    private readonly DensityField _field;
    private readonly ChunkGenerator _generator;
    private readonly GeometryPool _vertexPool;
    private readonly GeometryPool _indexPool;
    private readonly GraphicsPipeline _pipeline;
    private readonly GraphicsPipeline? _wireframePipeline;

    private readonly Dictionary<ChunkCoord, ChunkRecord> _chunks = new(4096);
    private readonly List<ChunkRecord> _all = new(4096);
    private readonly Queue<ChunkRecord> _uploading = new(256);
    private readonly Queue<ChunkJobResult> _deferredResults = new(16);
    private readonly List<DeferredFree> _deferredFrees = new(64);
    private readonly List<ChunkCoord> _rebuildScratch = new(64);
    private int _residentCount;
    private int _emptyCount;
    private bool _disposed;

    public DensityField Field => _field;
    public ChunkGenerator Generator => _generator;
    public int ChunkCount => _all.Count;
    public int ResidentCount => _residentCount;
    public int EmptyCount => _emptyCount;
    public int UploadingCount => _uploading.Count;
    /// <summary>Chunks requested but not yet on the GPU: queued, generating, or waiting for staging.</summary>
    public int PendingCount => _all.Count - _residentCount - _emptyCount - _uploading.Count;
    public ulong GeometryBytes => _vertexPool.UsedBytes + _indexPool.UsedBytes;
    public bool SupportsWireframe => _wireframePipeline is not null;

    public TerrainRenderer(VulkanDevice device, GpuAllocator allocator, DescriptorHeap heap, PipelineCache pipelines,
        TerrainOptions options, Format colorFormat, Format depthFormat)
    {
        _device = device;
        _heap = heap;
        _options = options;
        _field = new DensityField(options.Seed);
        _generator = new ChunkGenerator(_field, options.WorkerThreads);
        _vertexPool = new GeometryPool(device, allocator, heap, options.PoolBufferBytes, BufferUsageFlags.None, "Terrain.Vertices");
        _indexPool = new GeometryPool(device, allocator, null, options.PoolBufferBytes, BufferUsageFlags.IndexBufferBit, "Terrain.Indices");

        // The mesher winds front faces counter-clockwise in Y-up world space. The projection negates Y for
        // Vulkan's Y-down framebuffer, but Vulkan's triangle-orientation rule already carries a sign flip
        // for that convention, so counter-clockwise stays counter-clockwise: declare it as such.
        var desc = new GraphicsPipelineDesc
        {
            Name = "Terrain",
            VertexShader = "terrain.vert",
            FragmentShader = "terrain.frag",
            ColorFormat = colorFormat,
            DepthFormat = depthFormat,
            DepthTest = true,
            DepthWrite = true,
            CullMode = CullModeFlags.BackBit,
            FrontFace = FrontFace.CounterClockwise,
        };
        _pipeline = pipelines.CreateGraphics(desc);
        if (device.Capabilities.WireframeFill)
        {
            _wireframePipeline = pipelines.CreateGraphics(desc with { Name = "Terrain.Wireframe", PolygonMode = PolygonMode.Line, CullMode = CullModeFlags.None });
        }
    }

    // ---- Requests -------------------------------------------------------------------------------

    /// <summary>Queues a chunk for generation if it is not already known. Returns false when it was.</summary>
    public bool Request(ChunkCoord coord)
    {
        if (_chunks.ContainsKey(coord))
        {
            return false;
        }

        var record = new ChunkRecord { Coord = coord, Origin = coord.Origin, State = ChunkState.Pending };
        _chunks.Add(coord, record);
        _all.Add(record);
        _generator.Enqueue(coord);
        return true;
    }

    /// <summary>
    /// Forgets a resident chunk. Its geometry ranges are freed once the frame timeline passes
    /// <paramref name="retireAfter"/>, the last submission that may draw it. This is the hook dynamic
    /// streaming will call as chunks leave the octree.
    /// </summary>
    /// <summary>
    /// Rebuilds every resident chunk a sphere touches, at every level of detail.
    /// <para>
    /// A terrain edit changes the density field, and a chunk's mesh is only a cached view of
    /// that field, so the cache has to be dropped. Release then re-request is the whole of it:
    /// generation re-reads the field, which now includes the edit, and the geometry pool
    /// recovers the old allocation once the GPU is finished with it.
    /// </para>
    /// <para>
    /// Coarse levels are rebuilt too, so a tunnel does not close up again as it recedes into
    /// the distance.
    /// </para>
    /// </summary>
    public int RebuildSphere(double x, double y, double z, double radius, ulong retireAfter)
    {
        _rebuildScratch.Clear();

        foreach (var pair in _chunks)
        {
            ChunkCoord coord = pair.Key;
            double size = coord.Size;
            double minX = coord.X * size, minY = coord.Y * size, minZ = coord.Z * size;

            // Nearest point of the chunk box to the sphere centre.
            double nx = Math.Clamp(x, minX, minX + size);
            double ny = Math.Clamp(y, minY, minY + size);
            double nz = Math.Clamp(z, minZ, minZ + size);
            double dx = x - nx, dy = y - ny, dz = z - nz;

            // One extra sample of slack, because the mesher reads a padded volume and an edit
            // just outside the box still moves the vertices on its boundary.
            double reach = radius + coord.Spacing * 2.0;
            if (dx * dx + dy * dy + dz * dz <= reach * reach) _rebuildScratch.Add(coord);
        }

        foreach (ChunkCoord coord in _rebuildScratch)
        {
            if (!_chunks.TryGetValue(coord, out var record)) continue;
            if (record.State != ChunkState.Resident) continue;   // in flight; it will pick the edit up
            Release(coord, retireAfter);
            Request(coord);
        }

        return _rebuildScratch.Count;
    }

    /// <summary>Whether this chunk is already requested, generating, uploading or resident.</summary>
    public bool IsKnown(ChunkCoord coord) => _chunks.ContainsKey(coord);

    /// <summary>Collects every known chunk that is not in the wanted set, for eviction.</summary>
    public void CollectResident(List<ChunkCoord> into, HashSet<ChunkCoord> wanted)
    {
        foreach (var pair in _chunks)
        {
            if (!wanted.Contains(pair.Key)) into.Add(pair.Key);
        }
    }

    public bool Release(ChunkCoord coord, ulong retireAfter)
    {
        if (!_chunks.TryGetValue(coord, out var record) || record.State != ChunkState.Resident)
        {
            return false;
        }

        record.State = ChunkState.Released;
        _residentCount--;
        _deferredFrees.Add(new DeferredFree { Vertices = record.Vertices, Indices = record.Indices, RetireAfter = retireAfter });
        _chunks.Remove(coord);
        _all.Remove(record);
        return true;
    }

    /// <summary>Frees geometry of released chunks whose last frame has completed. Call once per frame with the completed frame-timeline value.</summary>
    public void CollectFrees(ulong completedValue)
    {
        for (int i = _deferredFrees.Count - 1; i >= 0; i--)
        {
            if (_deferredFrees[i].RetireAfter <= completedValue)
            {
                _vertexPool.Free(_deferredFrees[i].Vertices);
                _indexPool.Free(_deferredFrees[i].Indices);
                int last = _deferredFrees.Count - 1;
                _deferredFrees[i] = _deferredFrees[last];
                _deferredFrees.RemoveAt(last);
            }
        }
    }

    /// <summary>
    /// Queues the fixed start-up region described by <see cref="TerrainOptions"/>: a block of chunks
    /// centred on the start column whose vertical span covers the terrain's height range there,
    /// ordered nearest-first from <paramref name="viewpoint"/> so the ground under the camera arrives first.
    /// Returns the number of chunks queued.
    /// </summary>
    public int RequestStartupRegion(Vector3D<double> viewpoint)
    {
        double chunkSize = new ChunkCoord(0, 0, 0).Size;
        int centreX = (int)Math.Floor(_options.CentreX / chunkSize);
        int centreZ = (int)Math.Floor(_options.CentreZ / chunkSize);
        int minX = centreX - _options.RegionChunksX / 2;
        int minZ = centreZ - _options.RegionChunksZ / 2;
        int maxX = minX + _options.RegionChunksX - 1;
        int maxZ = minZ + _options.RegionChunksZ - 1;

        // Height range of the region from the cheap 2D landform, sampled every quarter chunk.
        double minHeight = double.MaxValue, maxHeight = double.MinValue;
        var landform = _field.Landform;
        double step = chunkSize * 0.25;
        for (double z = minZ * chunkSize; z <= (maxZ + 1) * chunkSize; z += step)
        {
            for (double x = minX * chunkSize; x <= (maxX + 1) * chunkSize; x += step)
            {
                double h = landform.Sample(x, z).BaseHeight;
                if (h < minHeight) minHeight = h;
                if (h > maxHeight) maxHeight = h;
            }
        }

        // One chunk of rock below the lowest ground so valleys have floors; the overhang term can lift
        // rock up to 80 m above the height field in young mountains, so leave headroom above.
        int minY = (int)Math.Floor((minHeight - chunkSize) / chunkSize);
        int maxY = (int)Math.Floor((maxHeight + 48.0) / chunkSize);
        if (maxY - minY + 1 > _options.MaxRegionChunksY)
        {
            minY = maxY - _options.MaxRegionChunksY + 1;
        }

        var coords = new List<ChunkCoord>(_options.RegionChunksX * _options.RegionChunksZ * (maxY - minY + 1));
        for (int y = minY; y <= maxY; y++)
        for (int z = minZ; z <= maxZ; z++)
        for (int x = minX; x <= maxX; x++)
        {
            coords.Add(new ChunkCoord(x, y, z));
        }

        coords.Sort((a, b) => DistanceSquared(a, viewpoint).CompareTo(DistanceSquared(b, viewpoint)));
        int queued = 0;
        foreach (var coord in coords)
        {
            if (Request(coord)) queued++;
        }

        RenderLog.Info(Tag, $"Start-up region: chunks x[{minX},{maxX}] y[{minY},{maxY}] z[{minZ},{maxZ}] = {queued} chunks " +
                            $"({_options.RegionChunksX * chunkSize:F0} x {(maxY - minY + 1) * chunkSize:F0} x {_options.RegionChunksZ * chunkSize:F0} m), " +
                            $"terrain height {minHeight:F1}..{maxHeight:F1} m");
        return queued;

        static double DistanceSquared(ChunkCoord c, Vector3D<double> p)
        {
            var centre = c.Centre;
            double dx = centre.X - p.X, dy = centre.Y - p.Y, dz = centre.Z - p.Z;
            return dx * dx + dy * dy + dz * dz;
        }
    }

    // ---- Upload ---------------------------------------------------------------------------------

    /// <summary>
    /// Moves finished meshes onto the GPU: allocates pool ranges, converts vertices straight into the
    /// staging ring and records the copies. Stops when <paramref name="budgetBytes"/> is spent or the
    /// ring is full; leftovers wait for the next frame. Must run between the uploader's BeginBatch and EndBatch.
    /// </summary>
    public void PumpUploads(TransferUploader uploader, ulong budgetBytes)
    {
        ulong spent = 0;
        while (spent < budgetBytes)
        {
            ChunkJobResult result;
            if (_deferredResults.Count > 0)
            {
                result = _deferredResults.Peek();
            }
            else if (!_generator.TryDequeueResult(out result))
            {
                break;
            }

            var record = _chunks[result.Coord];
            if (record.State == ChunkState.Released)
            {
                // Released while still generating: drop the mesh.
                FinishResult(result);
                continue;
            }

            if (result.IsEmpty)
            {
                record.State = ChunkState.Empty;
                _emptyCount++;
                FinishResult(result);
                continue;
            }

            ulong vertexBytes = (ulong)result.Mesh.Vertices.Count * VertexStride;
            ulong indexBytes = (ulong)result.Mesh.Indices.Count * IndexStride;

            if (!uploader.TryStage(vertexBytes, 16, out var vertexSlice))
            {
                Defer(result);
                break;
            }

            if (!uploader.TryStage(indexBytes, 4, out var indexSlice))
            {
                // The vertex slice stays reserved until the ring retires; harmless, it is a few frames of waste.
                Defer(result);
                break;
            }

            record.Vertices = _vertexPool.Allocate(vertexBytes, VertexStride);
            record.Indices = _indexPool.Allocate(indexBytes, IndexStride);
            record.VertexCount = (uint)result.Mesh.Vertices.Count;
            record.IndexCount = (uint)result.Mesh.Indices.Count;
            record.BoundsMin = result.BoundsMin;
            record.BoundsMax = result.BoundsMax;

            ConvertVertices(result.Mesh.Vertices, (GpuTerrainVertex*)vertexSlice.Ptr);
            MemoryMarshal.AsBytes(CollectionsMarshal.AsSpan(result.Mesh.Indices)).CopyTo(indexSlice.Span);

            uploader.CopyToBuffer(vertexSlice, _vertexPool.BufferOf(record.Vertices.PoolIndex), record.Vertices.Offset,
                PipelineStageFlags2.VertexShaderBit, AccessFlags2.ShaderStorageReadBit);
            record.UploadTicket = uploader.CopyToBuffer(indexSlice, _indexPool.BufferOf(record.Indices.PoolIndex), record.Indices.Offset,
                PipelineStageFlags2.IndexInputBit, AccessFlags2.IndexReadBit);

            record.State = ChunkState.Uploading;
            _uploading.Enqueue(record);
            spent += vertexBytes + indexBytes;
            FinishResult(result);
        }
    }

    /// <summary>Marks chunks resident once their upload ticket has been acquired on the graphics queue.</summary>
    public void PromoteAcquired(ulong acquiredThrough)
    {
        while (_uploading.TryPeek(out var record) && record.UploadTicket <= acquiredThrough)
        {
            _uploading.Dequeue();
            if (record.State == ChunkState.Uploading)
            {
                record.State = ChunkState.Resident;
                _residentCount++;
            }
        }
    }

    private void Defer(ChunkJobResult result)
    {
        if (_deferredResults.Count == 0 || !ReferenceEquals(_deferredResults.Peek(), result))
        {
            _deferredResults.Enqueue(result);
        }
    }

    private void FinishResult(ChunkJobResult result)
    {
        if (_deferredResults.Count > 0 && ReferenceEquals(_deferredResults.Peek(), result))
        {
            _deferredResults.Dequeue();
        }

        _generator.Recycle(result);
    }

    private static void ConvertVertices(List<TerrainVertex> source, GpuTerrainVertex* destination)
    {
        var span = CollectionsMarshal.AsSpan(source);
        for (int i = 0; i < span.Length; i++)
        {
            ref readonly var v = ref span[i];
            destination[i] = new GpuTerrainVertex
            {
                Position = v.Position,
                NormalMaterial = PackNormalMaterial(v.Normal, v.Material),
            };
        }
    }

    /// <summary>Three snorm8 components plus the material byte, matching GLSL unpackSnorm4x8 on the low three bytes.</summary>
    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    private static uint PackNormalMaterial(Vector3 n, byte material)
    {
        uint x = (byte)(sbyte)MathF.Round(Math.Clamp(n.X, -1f, 1f) * 127f);
        uint y = (byte)(sbyte)MathF.Round(Math.Clamp(n.Y, -1f, 1f) * 127f);
        uint z = (byte)(sbyte)MathF.Round(Math.Clamp(n.Z, -1f, 1f) * 127f);
        return x | (y << 8) | (z << 16) | ((uint)material << 24);
    }

    // ---- Draw -----------------------------------------------------------------------------------

    /// <summary>Culls and draws every resident chunk. Call inside the main rendering pass with the bindless set bound.</summary>
    public void Draw(CommandBuffer cmd, Camera camera, uint frameHandle, bool wireframe, ref RenderStats stats)
    {
        var vk = _device.Vk;
        var pipeline = wireframe && _wireframePipeline is not null ? _wireframePipeline : _pipeline;
        vk.CmdBindPipeline(cmd, PipelineBindPoint.Graphics, pipeline.Handle);

        var frustum = new Frustum(camera.ViewProjection);
        var push = new TerrainPushConstants { FrameHandle = frameHandle };
        int boundIndexPool = -1;

        for (int i = 0; i < _all.Count; i++)
        {
            var chunk = _all[i];
            if (chunk.State != ChunkState.Resident)
            {
                continue;
            }

            // Camera-relative origin in double, narrowed once; the bounds are chunk-local floats.
            var offset = camera.RelativeTo(chunk.Origin);
            if (!frustum.Intersects(offset + chunk.BoundsMin, offset + chunk.BoundsMax))
            {
                stats.ChunksCulled++;
                continue;
            }

            if (chunk.Indices.PoolIndex != boundIndexPool)
            {
                boundIndexPool = chunk.Indices.PoolIndex;
                vk.CmdBindIndexBuffer(cmd, _indexPool.BufferOf(boundIndexPool), 0, IndexType.Uint32);
            }

            push.VertexBuffer = _vertexPool.HandleOf(chunk.Vertices.PoolIndex).Index;
            push.ChunkOffset = offset;
            vk.CmdPushConstants(cmd, _heap.PipelineLayout, ShaderStageFlags.All, 0, (uint)sizeof(TerrainPushConstants), &push);
            vk.CmdDrawIndexed(cmd, chunk.IndexCount, 1, (uint)(chunk.Indices.Offset / IndexStride), (int)(chunk.Vertices.Offset / VertexStride), 0);

            stats.ChunksDrawn++;
            stats.DrawCalls++;
            stats.TrianglesDrawn += chunk.IndexCount / 3;
        }
    }

    /// <summary>
    /// Records the same chunks into a depth-only pass using a light projection instead of the
    /// camera's. Culling uses the light frustum, which is why a cascade covering ground behind the
    /// player still draws the mountain that shadows it.
    /// </summary>
    public void DrawDepth(CommandBuffer cmd, Camera camera, in Matrix4x4 lightViewProj, Pipeline pipeline)
    {
        var vk = _device.Vk;
        vk.CmdBindPipeline(cmd, PipelineBindPoint.Graphics, pipeline);

        var frustum = new Frustum(lightViewProj);
        var push = new SakritCraft.Render.Shadows.ShadowPushConstants { LightViewProj = lightViewProj };
        int boundIndexPool = -1;

        for (int i = 0; i < _all.Count; i++)
        {
            var chunk = _all[i];
            if (chunk.State != ChunkState.Resident)
            {
                continue;
            }

            var offset = camera.RelativeTo(chunk.Origin);
            if (!frustum.Intersects(offset + chunk.BoundsMin, offset + chunk.BoundsMax))
            {
                continue;
            }

            if (chunk.Indices.PoolIndex != boundIndexPool)
            {
                boundIndexPool = chunk.Indices.PoolIndex;
                vk.CmdBindIndexBuffer(cmd, _indexPool.BufferOf(boundIndexPool), 0, IndexType.Uint32);
            }

            push.VertexBuffer = _vertexPool.HandleOf(chunk.Vertices.PoolIndex).Index;
            push.ChunkOffset = offset;
            vk.CmdPushConstants(cmd, _heap.PipelineLayout, ShaderStageFlags.All, 0,
                (uint)sizeof(SakritCraft.Render.Shadows.ShadowPushConstants), &push);
            vk.CmdDrawIndexed(cmd, chunk.IndexCount, 1, (uint)(chunk.Indices.Offset / IndexStride),
                (int)(chunk.Vertices.Offset / VertexStride), 0);
        }
    }

    public void FillStats(ref RenderStats stats)
    {
        stats.ChunksResident = _residentCount;
        stats.ChunksUploading = _uploading.Count;
        stats.ChunksPending = PendingCount;
        stats.ChunksEmpty = _emptyCount;
        stats.GeometryBytes = GeometryBytes;
    }

    /// <summary>Caller must have made the device idle.</summary>
    public void Dispose()
    {
        if (_disposed)
        {
            return;
        }

        _disposed = true;
        _generator.Dispose();
        while (_generator.TryDequeueResult(out var stray))
        {
            _generator.Recycle(stray);
        }

        foreach (var chunk in _all)
        {
            if (chunk.State is ChunkState.Uploading or ChunkState.Resident)
            {
                _vertexPool.Free(chunk.Vertices);
                _indexPool.Free(chunk.Indices);
            }
        }

        foreach (var free in _deferredFrees)
        {
            _vertexPool.Free(free.Vertices);
            _indexPool.Free(free.Indices);
        }

        _deferredFrees.Clear();
        _all.Clear();
        _chunks.Clear();
        _vertexPool.Dispose();
        _indexPool.Dispose();
    }
}
