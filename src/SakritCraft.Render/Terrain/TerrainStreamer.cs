using SakritCraft.World.Generation;
using Silk.NET.Maths;

namespace SakritCraft.Render.Terrain;

/// <summary>
/// Decides which chunks should exist, at which level of detail, for the camera's current
/// position, and keeps the terrain renderer's residency matching that decision.
/// <para>
/// The world is unbounded, so residency is a set of nested square rings centred on the
/// camera: the innermost at full resolution, each one outward covering twice the distance
/// with chunks twice the size. Because chunk sizes are powers of two metres and every
/// grid is aligned to the world origin, a coarse chunk's footprint is always an exact
/// union of finer ones, which is what lets the rings partition space with neither gaps
/// nor overlapping geometry.
/// </para>
/// <para>
/// The partition being exact matters more than it sounds. Gaps show as holes through to
/// the sky; overlaps put two approximations of the same surface at the same depth and
/// produce shimmering z-fighting across the whole horizon.
/// </para>
/// </summary>
public sealed class TerrainStreamer
{
    private const string Tag = "terrain.stream";

    private readonly TerrainRenderer _terrain;
    private readonly DensityField _field;

    private readonly HashSet<ChunkCoord> _desired = new(8192);
    private readonly List<ChunkCoord> _pending = new(8192);
    private readonly List<ChunkCoord> _stale = new(1024);

    private Vector3D<double> _lastCentre;
    private bool _hasCentre;

    /// <summary>Half-extent of each ring, in chunks of that level. Must be even so the finer
    /// level's coverage lands on an exact chunk boundary of this one.</summary>
    public int RingHalfExtent { get; init; } = 8;

    /// <summary>Coarsest level generated. Level 6 chunks are 1024 m across.</summary>
    public int MaxLod { get; init; } = 6;

    /// <summary>How far the camera moves before residency is recomputed, in metres.</summary>
    public double RebuildDistance { get; init; } = 8.0;

    /// <summary>Chunks queued per update, so a long move does not stall a frame.</summary>
    public int RequestsPerUpdate { get; init; } = 96;

    /// <summary>Metres of headroom above the height field, covering the overhang term's reach.</summary>
    private const double AboveMargin = 56.0;

    public int DesiredCount => _desired.Count;
    public int PendingCount => _pending.Count;

    /// <summary>Furthest distance any ring reaches, in metres. The fog should end inside this.</summary>
    public double ViewDistance => RingHalfExtent * ChunkCoord.BaseSpacing * Mesh.DensityVolume.ChunkCells * (1 << MaxLod);

    public TerrainStreamer(TerrainRenderer terrain, DensityField field)
    {
        _terrain = terrain;
        _field = field;
    }

    /// <summary>
    /// Brings residency in line with the camera. Cheap when the camera has barely moved:
    /// the desired set is only recomputed once the camera has travelled far enough to
    /// change it.
    /// </summary>
    public void Update(Vector3D<double> camera, ulong retireAfter)
    {
        double moved = _hasCentre
            ? Math.Sqrt(Square(camera.X - _lastCentre.X) + Square(camera.Y - _lastCentre.Y) + Square(camera.Z - _lastCentre.Z))
            : double.MaxValue;

        if (moved >= RebuildDistance)
        {
            Rebuild(camera);
            _lastCentre = camera;
            _hasCentre = true;
        }

        // Feed the generator a bounded number of chunks per update, nearest first.
        int issued = 0;
        while (issued < RequestsPerUpdate && _pending.Count > 0)
        {
            ChunkCoord coord = _pending[^1];
            _pending.RemoveAt(_pending.Count - 1);
            if (_desired.Contains(coord) && _terrain.Request(coord)) issued++;
        }
    }

    private void Rebuild(Vector3D<double> camera)
    {
        _desired.Clear();

        // Coverage of each level, as an inclusive chunk-index square in that level's own grid.
        Span<int> centreX = stackalloc int[MaxLod + 1];
        Span<int> centreZ = stackalloc int[MaxLod + 1];

        for (int lod = 0; lod <= MaxLod; lod++)
        {
            double size = SizeOf(lod);
            centreX[lod] = (int)Math.Floor(camera.X / size);
            centreZ[lod] = (int)Math.Floor(camera.Z / size);
        }

        for (int lod = 0; lod <= MaxLod; lod++)
        {
            double size = SizeOf(lod);
            int cx = centreX[lod], cz = centreZ[lod];
            int h = RingHalfExtent;

            // What the next finer level already covers, expressed in this level's indices. A chunk
            // here is redundant only if it is *entirely* inside that region.
            int innerMinX = int.MaxValue, innerMaxX = int.MinValue;
            int innerMinZ = int.MaxValue, innerMaxZ = int.MinValue;
            if (lod > 0)
            {
                int fx = centreX[lod - 1], fz = centreZ[lod - 1];
                innerMinX = CeilDiv2(fx - h);
                innerMaxX = FloorDiv2(fx + h + 1) - 1;
                innerMinZ = CeilDiv2(fz - h);
                innerMaxZ = FloorDiv2(fz + h + 1) - 1;
            }

            for (int z = cz - h; z <= cz + h; z++)
            {
                for (int x = cx - h; x <= cx + h; x++)
                {
                    bool covered = lod > 0
                                && x >= innerMinX && x <= innerMaxX
                                && z >= innerMinZ && z <= innerMaxZ;
                    if (covered) continue;

                    AddColumn(x, z, lod, size);
                }
            }
        }

        // Anything resident that is no longer wanted goes back to the pool.
        _stale.Clear();
        _terrain.CollectResident(_stale, _desired);

        _pending.Clear();
        foreach (ChunkCoord coord in _desired)
        {
            if (!_terrain.IsKnown(coord)) _pending.Add(coord);
        }

        // Sorted so the nearest are popped from the end first.
        _pending.Sort((a, b) => DistanceSquared(b, camera).CompareTo(DistanceSquared(a, camera)));
    }

    /// <summary>
    /// Adds the vertical run of chunks that can contain terrain for one column, using the cheap
    /// two-dimensional height field rather than probing the volume.
    /// </summary>
    private void AddColumn(int x, int z, int lod, double size)
    {
        double x0 = x * size, z0 = z * size;
        var landform = _field.Landform;

        // Corners and centre give a good enough bound on the column's height range; sampling the
        // whole footprint would cost more than generating the chunks it saves.
        double min = double.MaxValue, max = double.MinValue;
        for (int i = 0; i < 5; i++)
        {
            double sx = i switch { 0 => x0, 1 => x0 + size, 2 => x0, 3 => x0 + size, _ => x0 + size * 0.5 };
            double sz = i switch { 0 => z0, 1 => z0, 2 => z0 + size, 3 => z0 + size, _ => z0 + size * 0.5 };
            double h = landform.Sample(sx, sz).BaseHeight;
            if (h < min) min = h;
            if (h > max) max = h;
        }

        // One chunk of solid rock below the lowest ground so valleys have floors and caves have walls.
        double lowest = Math.Max(DensityField.WorldBottom, min - size);
        double highest = Math.Min(DensityField.WorldTop, max + AboveMargin);

        int yMin = (int)Math.Floor(lowest / size);
        int yMax = (int)Math.Floor(highest / size);

        for (int y = yMin; y <= yMax; y++)
        {
            _desired.Add(new ChunkCoord(x, y, z, lod));
        }
    }

    private static double SizeOf(int lod) => ChunkCoord.BaseSpacing * Mesh.DensityVolume.ChunkCells * (1 << lod);

    // Integer division that rounds toward negative infinity, which ordinary C# division does not.
    private static int FloorDiv2(int v) => v >= 0 ? v >> 1 : -((-v + 1) >> 1);
    private static int CeilDiv2(int v) => -FloorDiv2(-v);

    private static double Square(double v) => v * v;

    private static double DistanceSquared(ChunkCoord coord, Vector3D<double> point)
    {
        var c = coord.Centre;
        double dx = c.X - point.X, dy = (c.Y - point.Y) * 0.5, dz = c.Z - point.Z;
        return dx * dx + dy * dy + dz * dz;
    }

    /// <summary>Chunks that were resident but are no longer wanted. Released by the caller once the
    /// GPU has finished with them.</summary>
    public IReadOnlyList<ChunkCoord> Stale => _stale;
}
