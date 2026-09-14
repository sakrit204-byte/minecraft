using SakritCraft.Core.Hashing;
using SakritCraft.World.Seed;

namespace SakritCraft.World.Generation;

/// <summary>Kinds of built thing the generator places.</summary>
public enum StructureKind : byte
{
    /// <summary>A handful of houses and a well. Villagers live here.</summary>
    Village = 0,
    /// <summary>Collapsed stonework. Loot, and something for a story to have happened to.</summary>
    Ruin = 1,
    /// <summary>A single tower on high ground, visible from a long way off.</summary>
    Watchtower = 2,
    /// <summary>A boarded entrance leading into the cave network.</summary>
    MineEntrance = 3,
    /// <summary>A lone shelter by water or on a pass. Somewhere to reach before dark.</summary>
    Camp = 4,
}

/// <summary>One placed structure. Everything about it is a function of the seed and its cell.</summary>
public readonly record struct PlacedStructure(
    StructureKind Kind,
    double X,
    double Y,
    double Z,
    /// <summary>Rotation about the vertical axis, in radians.</summary>
    double Yaw,
    /// <summary>Horizontal extent, in metres. Used for spacing and for streaming.</summary>
    double Radius,
    /// <summary>Stable identifier, so a structure can be referenced in a save without storing it.</summary>
    ulong Id);

/// <summary>
/// Places settlements and ruins across the world.
/// <para>
/// Placement is a jittered grid: each cell of a coarse lattice decides for itself whether
/// it holds a structure and where inside the cell it sits. That gives even coverage without
/// the visible regularity of a plain grid, and, more importantly, it is a pure function of
/// the cell. Whether a village exists at some far coordinate can be answered without
/// generating anything between here and there, which is what makes <c>/locate</c> possible
/// and what keeps structures identical no matter which direction a player approaches from.
/// </para>
/// </summary>
public sealed class StructurePlacer
{
    private readonly LandformFields _landform;
    private readonly ulong _stream;

    /// <summary>Cell size per kind, in metres. Larger means rarer and further apart.</summary>
    private static readonly (StructureKind Kind, double CellSize, double Chance, double Radius)[] Kinds =
    [
        (StructureKind.Village,      760.0, 0.42, 34.0),
        (StructureKind.Ruin,         420.0, 0.38, 14.0),
        (StructureKind.Watchtower,   680.0, 0.30, 8.0),
        (StructureKind.MineEntrance, 520.0, 0.34, 7.0),
        (StructureKind.Camp,         300.0, 0.30, 6.0),
    ];

    public StructurePlacer(WorldSeed seed, Dimension dimension = Dimension.Overworld)
        => (_landform, _stream) = (new LandformFields(seed, dimension), seed.Stream(dimension, SeedDomains.Structure));

    /// <summary>
    /// Every structure whose centre lies within a radius of a point. Cheap enough to call
    /// per frame: it inspects a handful of lattice cells and evaluates the two-dimensional
    /// landform at each candidate, never the density field.
    /// </summary>
    public List<PlacedStructure> Near(double x, double z, double searchRadius)
    {
        var found = new List<PlacedStructure>();

        foreach ((StructureKind kind, double cellSize, double chance, double radius) in Kinds)
        {
            int minCellX = (int)Math.Floor((x - searchRadius) / cellSize);
            int maxCellX = (int)Math.Floor((x + searchRadius) / cellSize);
            int minCellZ = (int)Math.Floor((z - searchRadius) / cellSize);
            int maxCellZ = (int)Math.Floor((z + searchRadius) / cellSize);

            for (int cz = minCellZ; cz <= maxCellZ; cz++)
            {
                for (int cx = minCellX; cx <= maxCellX; cx++)
                {
                    if (!TryPlace(kind, cx, cz, cellSize, chance, radius, out PlacedStructure structure)) continue;

                    double dx = structure.X - x, dz = structure.Z - z;
                    if (dx * dx + dz * dz <= searchRadius * searchRadius) found.Add(structure);
                }
            }
        }

        return found;
    }

    /// <summary>The nearest structure of a kind, for the <c>/locate</c> command. Searches
    /// outward in rings so it terminates as soon as it finds one.</summary>
    public PlacedStructure? Locate(StructureKind kind, double x, double z, int maxRings = 24)
    {
        (double cellSize, double chance, double radius) = SettingsFor(kind);
        int originX = (int)Math.Floor(x / cellSize);
        int originZ = (int)Math.Floor(z / cellSize);

        PlacedStructure? best = null;
        double bestDistance = double.MaxValue;

        for (int ring = 0; ring <= maxRings; ring++)
        {
            for (int cz = originZ - ring; cz <= originZ + ring; cz++)
            {
                for (int cx = originX - ring; cx <= originX + ring; cx++)
                {
                    // Only the ring's perimeter is new; the interior was covered already.
                    if (ring > 0 && Math.Abs(cx - originX) != ring && Math.Abs(cz - originZ) != ring) continue;
                    if (!TryPlace(kind, cx, cz, cellSize, chance, radius, out PlacedStructure structure)) continue;

                    double dx = structure.X - x, dz = structure.Z - z;
                    double distance = dx * dx + dz * dz;
                    if (distance < bestDistance) { bestDistance = distance; best = structure; }
                }
            }

            // Having searched rings 0..R, anything still unseen lies in ring R+1 or beyond, whose
            // cells are at least R cells away from the query point (which may sit anywhere inside
            // its own cell). So a hit closer than that cannot be beaten. Using R+1 here instead
            // returns too early and misses a nearer structure just across a cell boundary.
            if (best is not null && bestDistance <= Math.Pow(ring * cellSize, 2)) return best;
        }

        return best;
    }

    private static (double CellSize, double Chance, double Radius) SettingsFor(StructureKind kind)
    {
        foreach ((StructureKind k, double size, double chance, double radius) in Kinds)
        {
            if (k == kind) return (size, chance, radius);
        }
        throw new ArgumentOutOfRangeException(nameof(kind));
    }

    /// <summary>
    /// Decides whether a lattice cell holds a structure, and where. Every answer comes from
    /// hashing the cell, so it never depends on what has been generated or visited.
    /// </summary>
    private bool TryPlace(StructureKind kind, int cellX, int cellZ, double cellSize, double chance,
                          double radius, out PlacedStructure structure)
    {
        structure = default;

        ulong hash = Hash64.Coord(_stream, cellX, cellZ, (int)kind);
        if (Hash64.ToUnit(hash) > chance) return false;

        // Jitter inside the cell, keeping clear of the edges so neighbours cannot collide.
        double jitterX = Hash64.ToUnit(Hash64.Mix(hash ^ 0xA1)) * 0.7 + 0.15;
        double jitterZ = Hash64.ToUnit(Hash64.Mix(hash ^ 0xB2)) * 0.7 + 0.15;
        double x = (cellX + jitterX) * cellSize;
        double z = (cellZ + jitterZ) * cellSize;

        ColumnSample column = _landform.Sample(x, z);
        if (!SuitsTerrain(kind, in column)) return false;

        structure = new PlacedStructure(
            kind, x, column.BaseHeight, z,
            Hash64.ToUnit(Hash64.Mix(hash ^ 0xC3)) * Math.Tau,
            radius,
            Hash64.Mix(hash ^ 0xD4));
        return true;
    }

    /// <summary>
    /// Whether the ground suits this kind of structure. This is where placement stops being
    /// a scatter and starts looking deliberate: villages want flat, low, temperate ground,
    /// and a watchtower on a plain would be pointless.
    /// </summary>
    private static bool SuitsTerrain(StructureKind kind, in ColumnSample column)
    {
        bool aboveWater = column.BaseHeight > LandformFields.SeaLevel + 2.0;
        if (!aboveWater) return false;

        // Erosion is the flatness proxy: high erosion is old, worn, level ground.
        return kind switch
        {
            StructureKind.Village => column.Erosion > 0.30
                                  && column.BaseHeight < LandformFields.SeaLevel + 70.0
                                  && column.TemperatureC is > 2.0 and < 32.0,

            StructureKind.Ruin => column.Erosion > -0.20,

            // High and prominent, so it can be seen and can see.
            StructureKind.Watchtower => column.BaseHeight > LandformFields.SeaLevel + 55.0
                                     && column.Ridge > 0.55,

            // Cut into a slope, where a shaft would actually meet the cave network.
            StructureKind.MineEntrance => column.Erosion < 0.25
                                       && column.BaseHeight > LandformFields.SeaLevel + 12.0,

            StructureKind.Camp => column.Erosion > 0.05,

            _ => false,
        };
    }
}
