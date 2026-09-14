using System.Collections.Concurrent;

namespace SakritCraft.World.Editing;

/// <summary>What a brush does to the rock it touches.</summary>
public enum BrushMode : byte
{
    /// <summary>Removes material, leaving a rounded hollow. Mining.</summary>
    Carve = 0,
    /// <summary>Adds material. Filling a hole, or building a ramp.</summary>
    Fill = 1,
}

/// <summary>One edit the player made. Spheres only, for now: they are what a swing removes.</summary>
public readonly record struct TerrainEdit(
    double X,
    double Y,
    double Z,
    float Radius,
    BrushMode Mode,
    /// <summary>Material left behind by a fill. Ignored when carving.</summary>
    byte Material)
{
    /// <summary>Signed distance to this brush's surface. Negative inside.</summary>
    public double DistanceTo(double x, double y, double z)
    {
        double dx = x - X, dy = y - Y, dz = z - Z;
        return Math.Sqrt(dx * dx + dy * dy + dz * dz) - Radius;
    }

    public bool Touches(double x, double y, double z, double margin = 0.0)
    {
        double dx = x - X, dy = y - Y, dz = z - Z;
        double reach = Radius + margin;
        return dx * dx + dy * dy + dz * dz <= reach * reach;
    }
}

/// <summary>
/// Everything the player has changed about the terrain.
/// <para>
/// Generation is a pure function of the seed, so the world does not need storing: only the
/// difference between what the seed produced and what the player did. That difference is
/// this. A chunk nobody has touched costs nothing, and a save file of an explored but
/// unmodified world is a few hundred kilobytes rather than gigabytes. See §18 of the design.
/// </para>
/// <para>
/// Edits are applied as boolean operations on signed distances, which is what makes mining
/// work at all in a continuous world: carving is a union with a sphere of air, filling is an
/// intersection with a sphere of rock, and both compose with the generated field without any
/// notion of a cell or a block.
/// </para>
/// <para>
/// Reads happen on many chunk-generation workers at once while writes happen on the
/// simulation thread, so each cell holds an immutable array that a write replaces wholesale.
/// A reader therefore always sees a complete, self-consistent set, and never a half-written
/// list; the cost is one small allocation per edit, which is nothing against the cost of
/// remeshing the chunk it dirties.
/// </para>
/// </summary>
public sealed class TerrainEdits
{
    /// <summary>Edge of one bucket in metres. Chosen to match the chunk size at level zero.</summary>
    private const double CellSize = 16.0;

    /// <summary>Largest brush allowed, so a lookup only ever needs the neighbouring cells.</summary>
    public const double MaxRadius = 8.0;

    private readonly ConcurrentDictionary<(int X, int Y, int Z), TerrainEdit[]> _cells = new();

    /// <summary>Bumped on every change, so consumers can tell whether anything moved.</summary>
    public int Version { get; private set; }

    public int Count { get; private set; }

    public bool IsEmpty => Count == 0;

    /// <summary>
    /// Records an edit. Returns the axis-aligned bounds it affects, so the caller knows which
    /// chunks have to be rebuilt.
    /// </summary>
    public (double MinX, double MinY, double MinZ, double MaxX, double MaxY, double MaxZ) Add(TerrainEdit edit)
    {
        if (edit.Radius <= 0.0f || edit.Radius > MaxRadius)
        {
            throw new ArgumentOutOfRangeException(nameof(edit),
                $"Brush radius must be between 0 and {MaxRadius} m; got {edit.Radius}.");
        }

        // A brush can straddle cells, so it is recorded in every cell it touches. Duplicates are
        // harmless: applying the same sphere twice gives the same distance.
        double r = edit.Radius;
        int minX = CellOf(edit.X - r), maxX = CellOf(edit.X + r);
        int minY = CellOf(edit.Y - r), maxY = CellOf(edit.Y + r);
        int minZ = CellOf(edit.Z - r), maxZ = CellOf(edit.Z + r);

        for (int z = minZ; z <= maxZ; z++)
        {
            for (int y = minY; y <= maxY; y++)
            {
                for (int x = minX; x <= maxX; x++)
                {
                    _cells.AddOrUpdate((x, y, z),
                        _ => [edit],
                        (_, existing) =>
                        {
                            var grown = new TerrainEdit[existing.Length + 1];
                            existing.CopyTo(grown, 0);
                            grown[^1] = edit;
                            return grown;
                        });
                }
            }
        }

        Count++;
        Version++;
        return (edit.X - r, edit.Y - r, edit.Z - r, edit.X + r, edit.Y + r, edit.Z + r);
    }

    /// <summary>
    /// Applies every edit touching a point to a generated density value.
    /// <para>
    /// Carving is <c>max(rock, -sphere)</c> and filling is <c>min(rock, sphere)</c>: the standard
    /// boolean difference and union on signed distances. Order matters, so edits are applied in
    /// the order they were made, which is what lets a player fill a hole they dug.
    /// </para>
    /// </summary>
    public double Apply(double density, double x, double y, double z)
    {
        if (Count == 0) return density;
        if (!_cells.TryGetValue((CellOf(x), CellOf(y), CellOf(z)), out TerrainEdit[]? edits)) return density;

        foreach (TerrainEdit edit in edits)
        {
            double sphere = edit.DistanceTo(x, y, z);
            density = edit.Mode == BrushMode.Carve
                ? Math.Max(density, -sphere)    // difference: the sphere's interior becomes air
                : Math.Min(density, sphere);    // union: the sphere's interior becomes rock
        }

        return density;
    }

    /// <summary>
    /// The material an edit leaves at a point, or zero if no fill covers it. Carved space has
    /// no material, so only fills answer.
    /// </summary>
    public byte MaterialAt(double x, double y, double z)
    {
        if (Count == 0) return 0;
        if (!_cells.TryGetValue((CellOf(x), CellOf(y), CellOf(z)), out TerrainEdit[]? edits)) return 0;

        byte material = 0;
        foreach (TerrainEdit edit in edits)
        {
            if (edit.Mode == BrushMode.Fill && edit.DistanceTo(x, y, z) <= 0.0) material = edit.Material;
            else if (edit.Mode == BrushMode.Carve && edit.DistanceTo(x, y, z) <= 0.0) material = 0;
        }
        return material;
    }

    /// <summary>Whether any edit could affect a box. Used to skip work on untouched chunks.</summary>
    public bool AffectsBox(double minX, double minY, double minZ, double maxX, double maxY, double maxZ)
    {
        if (Count == 0) return false;

        for (int z = CellOf(minZ); z <= CellOf(maxZ); z++)
        {
            for (int y = CellOf(minY); y <= CellOf(maxY); y++)
            {
                for (int x = CellOf(minX); x <= CellOf(maxX); x++)
                {
                    if (_cells.ContainsKey((x, y, z))) return true;
                }
            }
        }
        return false;
    }

    public void Clear()
    {
        _cells.Clear();
        Count = 0;
        Version++;
    }

    private static int CellOf(double v) => (int)Math.Floor(v / CellSize);
}
