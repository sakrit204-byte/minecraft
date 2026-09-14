using System.Collections.Concurrent;
using System.Diagnostics;
using System.Numerics;
using SakritCraft.Mesh;
using SakritCraft.World.Generation;

namespace SakritCraft.Render.Terrain;

/// <summary>Output of one generate-and-mesh job. <see cref="Mesh"/> is pooled: hand the result back through <see cref="ChunkGenerator.Recycle"/>.</summary>
public sealed class ChunkJobResult
{
    public ChunkCoord Coord;
    public ChunkMesh Mesh = new();
    /// <summary>Tight bounds of the mesh in chunk-local metres. Meaningless when <see cref="IsEmpty"/>.</summary>
    public Vector3 BoundsMin;
    public Vector3 BoundsMax;
    /// <summary>True when the chunk is entirely air or entirely rock and produced no triangles.</summary>
    public bool IsEmpty;
    public double GenerateMilliseconds;
    public double MeshMilliseconds;
}

/// <summary>
/// The worker-thread half of chunk streaming (docs/MASTER-PLAN.html section 06: "every stage runs on
/// worker threads except the final GPU upload"). Coordinates go in, meshed chunks come out, and the
/// render thread never samples noise or solves a QEF.
///
/// <para><b>Sampling cost.</b> A chunk is filled with one <see cref="LandformFields.Sample"/> per column
/// and one <see cref="DensityField.SampleColumn"/> per voxel, which is roughly thirty times cheaper
/// than calling <see cref="DensityField.Sample"/> per voxel. Sample positions are computed from integer
/// lattice coordinates times a power-of-two spacing, so two chunks sharing a face evaluate bit-identical
/// world positions and therefore bit-identical densities; that is what makes the seams close without
/// any stitching pass.</para>
///
/// <para><b>Early outs.</b> A chunk whose bottom lies above every column's height field by more than the
/// density field's maximum deformation is air and is never sampled. A filled chunk with no sign change
/// is never meshed.</para>
///
/// <para><b>Memory.</b> Each worker owns one padded <see cref="DensityVolume"/> and reuses it for every
/// chunk by writing through the indexer; the mesher reads only samples, materials and spacing, so the
/// volume's recorded origin is irrelevant. Meshes are pooled and recycled by the render thread.</para>
///
/// Workers are dedicated threads rather than the thread pool so a burst of shader hot-reload builds
/// cannot starve terrain and vice versa.
/// </summary>
public sealed class ChunkGenerator : IDisposable
{
    private const string Tag = "terrain.gen";

    /// <summary>Largest distance the 3D terms in <see cref="DensityField"/> can lift the surface above the height field, in metres. Mirrors its private MaxDeformation.</summary>
    private const double MaxDeformation = 80.0;

    private readonly DensityField _field;
    private readonly ConcurrentQueue<ChunkCoord> _jobs = new();
    private readonly ConcurrentQueue<ChunkJobResult> _results = new();
    private readonly ConcurrentBag<ChunkJobResult> _resultPool = new();
    private readonly SemaphoreSlim _jobSignal = new(0);
    private readonly CancellationTokenSource _cancel = new();
    private readonly Thread[] _workers;
    private int _pending;
    private int _inFlight;
    private long _completed;
    private long _skippedAir;
    private double _totalGenerateMs;
    private double _totalMeshMs;
    private bool _disposed;

    /// <summary>Chunks queued but not yet picked up by a worker.</summary>
    public int PendingCount => Volatile.Read(ref _pending);
    /// <summary>Chunks currently being generated or meshed.</summary>
    public int InFlightCount => Volatile.Read(ref _inFlight);
    public long CompletedCount => Interlocked.Read(ref _completed);
    public int WorkerCount => _workers.Length;

    public ChunkGenerator(DensityField field, int workerThreads)
    {
        _field = field;
        _workers = new Thread[Math.Max(1, workerThreads)];
        for (int i = 0; i < _workers.Length; i++)
        {
            int index = i;
            _workers[i] = new Thread(() => WorkerMain(index))
            {
                Name = $"sakrit.worldgen.{i}",
                IsBackground = true,
                Priority = ThreadPriority.BelowNormal, // the render thread must win contention
            };
            _workers[i].Start();
        }

        RenderLog.Info(Tag, $"{_workers.Length} generation workers started (seed {field.Seed}).");
    }

    /// <summary>Queues a chunk. Safe from any thread. Order of completion is not guaranteed.</summary>
    public void Enqueue(ChunkCoord coord)
    {
        ObjectDisposedException.ThrowIf(_disposed, this);
        Interlocked.Increment(ref _pending);
        _jobs.Enqueue(coord);
        _jobSignal.Release();
    }

    /// <summary>Render-thread pump. Allocation-free.</summary>
    public bool TryDequeueResult(out ChunkJobResult result) => _results.TryDequeue(out result!);

    /// <summary>Returns a result's mesh storage to the pool once its data has been copied to staging.</summary>
    public void Recycle(ChunkJobResult result)
    {
        result.Mesh.Clear();
        _resultPool.Add(result);
    }

    /// <summary>One-line summary for the log: throughput so far.</summary>
    public string Describe()
    {
        long done = CompletedCount;
        long meshed = done - Interlocked.Read(ref _skippedAir);
        double gen = meshed > 0 ? _totalGenerateMs / meshed : 0;
        double mesh = meshed > 0 ? _totalMeshMs / meshed : 0;
        return $"{done} chunks ({Interlocked.Read(ref _skippedAir)} skipped as air), avg fill {gen:F1} ms + mesh {mesh:F1} ms per sampled chunk on {_workers.Length} workers";
    }

    private void WorkerMain(int index)
    {
        var token = _cancel.Token;
        var landform = _field.Landform;
        var columns = new ColumnSample[DensityVolume.Dim * DensityVolume.Dim];
        DensityVolume? volume = null;
        var sw = new Stopwatch();

        try
        {
            while (!token.IsCancellationRequested)
            {
                _jobSignal.Wait(token);
                if (!_jobs.TryDequeue(out var coord))
                {
                    continue;
                }

                Interlocked.Decrement(ref _pending);
                Interlocked.Increment(ref _inFlight);
                try
                {
                    double spacing = coord.Spacing;
                    if (volume is null || volume.Spacing != spacing)
                    {
                        volume = new DensityVolume((0.0, 0.0, 0.0), spacing);
                    }

                    var result = _resultPool.TryTake(out var pooled) ? pooled : new ChunkJobResult();
                    result.Coord = coord;
                    result.IsEmpty = true;
                    result.GenerateMilliseconds = 0;
                    result.MeshMilliseconds = 0;

                    sw.Restart();
                    bool sampled = Fill(coord, volume, columns, landform);
                    result.GenerateMilliseconds = sw.Elapsed.TotalMilliseconds;

                    if (sampled)
                    {
                        sw.Restart();
                        SurfaceNets.Mesh(volume, result.Mesh);
                        result.MeshMilliseconds = sw.Elapsed.TotalMilliseconds;
                        if (!result.Mesh.IsEmpty)
                        {
                            result.IsEmpty = false;
                            ComputeBounds(result);
                        }

                        AddTotals(result.GenerateMilliseconds, result.MeshMilliseconds);
                    }
                    else
                    {
                        Interlocked.Increment(ref _skippedAir);
                    }

                    Interlocked.Increment(ref _completed);
                    _results.Enqueue(result);
                }
                finally
                {
                    Interlocked.Decrement(ref _inFlight);
                }
            }
        }
        catch (OperationCanceledException)
        {
            // Normal shutdown.
        }
        catch (Exception ex)
        {
            RenderLog.Error(Tag, $"Worker {index} died: {ex}");
        }
    }

    /// <summary>
    /// Fills the padded volume for <paramref name="coord"/>. Returns false when the chunk is certainly
    /// all air (or all rock with no sign change) and meshing can be skipped.
    /// </summary>
    private bool Fill(ChunkCoord coord, DensityVolume volume, ColumnSample[] columns, LandformFields landform)
    {
        const int dim = DensityVolume.Dim;
        const int cells = DensityVolume.ChunkCells;
        double spacing = volume.Spacing;

        // Lattice index of padded sample 0 along each axis, in units of spacing.
        long latticeX = (long)coord.X * cells - DensityVolume.Origin;
        long latticeY = (long)coord.Y * cells - DensityVolume.Origin;
        long latticeZ = (long)coord.Z * cells - DensityVolume.Origin;

        double minBase = double.MaxValue, maxBase = double.MinValue;
        for (int z = 0; z < dim; z++)
        {
            double wz = (latticeZ + z) * spacing;
            for (int x = 0; x < dim; x++)
            {
                double wx = (latticeX + x) * spacing;
                var column = landform.Sample(wx, wz);
                columns[z * dim + x] = column;
                if (column.BaseHeight < minBase) minBase = column.BaseHeight;
                if (column.BaseHeight > maxBase) maxBase = column.BaseHeight;
            }
        }

        double chunkBottom = latticeY * spacing;
        if (chunkBottom > maxBase + MaxDeformation + spacing)
        {
            return false; // nothing below can pull the field to solid up here
        }

        bool anySolid = false, anyAir = false;
        for (int z = 0; z < dim; z++)
        {
            double wz = (latticeZ + z) * spacing;
            for (int y = 0; y < dim; y++)
            {
                double wy = (latticeY + y) * spacing;
                for (int x = 0; x < dim; x++)
                {
                    double wx = (latticeX + x) * spacing;
                    ref readonly var column = ref columns[z * dim + x];
                    double d = _field.SampleColumnForSpacing(in column, wx, wy, wz, spacing);
                    volume[x, y, z] = (float)d;
                    if (d < 0.0) anySolid = true; else anyAir = true;
                    volume.SetMaterial(x, y, z, MaterialFor(in column, wy, d, spacing));
                }
            }
        }

        return anySolid && anyAir;
    }

    /// <summary>
    /// Placeholder stratigraphy. Depth below the surface is read from the density itself: the field's
    /// magnitude is an approximate signed distance to the real surface (section 04), which the height
    /// field alone is not once the overhang term has moved that surface by tens of metres. Cave walls
    /// far below the height field stay rock so grass does not grow underground. The mesher takes the
    /// material of the solid corner of each crossing, so only solid samples matter.
    /// </summary>
    private static byte MaterialFor(in ColumnSample column, double y, double density, double spacing)
    {
        // The mesher takes its material from the solid corner of a surface crossing, so every sample
        // this value is ever read from is, by construction, immediately below a surface. There is
        // therefore no need to ask how deep it is: a depth threshold measured in metres only ever
        // reintroduces a dependence on the sample spacing, and neighbouring chunks at different
        // levels of detail then disagree about the same ground and show as blocky patches.
        //
        // What still has to be asked is *which* surface. A crossing deep below the height field is a
        // cave wall, and grass must not grow there. The allowance has to cover the overhang term,
        // which moves the real surface by tens of metres in young mountains.
        double belowHeightField = column.BaseHeight - y;
        double surfaceReach = TerrainMaterial.SurfaceLayerReach
                            + LandformFields.OverhangStrength(column.Erosion)
                            + spacing * 2.0;

        if (belowHeightField >= surfaceReach)
        {
            return TerrainMaterial.Rock;
        }

        if (column.TemperatureC <= TerrainMaterial.SnowTemperatureC) return TerrainMaterial.Snow;
        if (y < LandformFields.SeaLevel + TerrainMaterial.BeachHeight) return TerrainMaterial.Sand;

        // Soil, which the shader resolves into grass on gentle ground and bare earth on steep ground
        // by slope. That test lives in the shader because slope is a property of the shaded fragment,
        // not of the volume sample, and it is level-of-detail invariant.
        return TerrainMaterial.Soil;
    }

    private static void ComputeBounds(ChunkJobResult result)
    {
        var min = new Vector3(float.MaxValue);
        var max = new Vector3(float.MinValue);
        var vertices = result.Mesh.Vertices;
        for (int i = 0; i < vertices.Count; i++)
        {
            var p = vertices[i].Position;
            min = Vector3.Min(min, p);
            max = Vector3.Max(max, p);
        }

        result.BoundsMin = min;
        result.BoundsMax = max;
    }

    private void AddTotals(double generateMs, double meshMs)
    {
        // Rare contention, tiny critical section: a lock is simpler than a CAS loop on doubles.
        lock (_jobs)
        {
            _totalGenerateMs += generateMs;
            _totalMeshMs += meshMs;
        }
    }

    public void Dispose()
    {
        if (_disposed)
        {
            return;
        }

        _disposed = true;
        _cancel.Cancel();
        foreach (var worker in _workers)
        {
            if (!worker.Join(TimeSpan.FromSeconds(5)))
            {
                RenderLog.Warn(Tag, $"Worker {worker.Name} did not stop within 5 s.");
            }
        }

        RenderLog.Info(Tag, "Generation workers stopped: " + Describe());
        _cancel.Dispose();
        _jobSignal.Dispose();
    }
}
