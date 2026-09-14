using SakritCraft.Content.Creatures;
using SakritCraft.Core.Math;
using SakritCraft.Physics;

namespace SakritCraft.Sim.Building;

/// <summary>The kinds of part a player can build with.</summary>
public enum PartKind : byte
{
    Wall = 0,
    Floor = 1,
    Roof = 2,
    /// <summary>Carries load much further than anything else. The answer to a sagging ceiling.</summary>
    Beam = 3,
    DoorFrame = 4,
}

/// <summary>One placed piece of construction.</summary>
public sealed class PlacedPart
{
    public required int Id { get; init; }
    public required PartKind Kind { get; init; }
    public required Vec3d Position { get; set; }
    public required double Yaw { get; set; }
    public required ushort ItemId { get; init; }

    /// <summary>How well load reaches this part, 0 to 1. Below the threshold it falls.</summary>
    public double Support { get; internal set; }

    /// <summary>Seconds of visible sagging before it collapses, so the player gets a warning.</summary>
    public double CollapseCountdown { get; internal set; }

    public bool IsGrounded { get; internal set; }

    /// <summary>Half-extent of the part, in metres. Walls are tall and thin, floors wide and flat.</summary>
    public Vec3d HalfExtent => Kind switch
    {
        PartKind.Wall => new Vec3d(1.5, 1.5, 0.15),
        PartKind.Floor => new Vec3d(1.5, 0.12, 1.5),
        PartKind.Roof => new Vec3d(1.6, 0.14, 1.6),
        PartKind.Beam => new Vec3d(0.16, 1.5, 0.16),
        PartKind.DoorFrame => new Vec3d(1.5, 1.5, 0.2),
        _ => new Vec3d(1.0, 1.0, 1.0),
    };

    /// <summary>How far this material carries load to its neighbours, as a fraction lost per link.</summary>
    public double SupportLoss => Kind switch
    {
        PartKind.Beam => 0.055,     // timber framing spans a long way
        PartKind.Wall => 0.10,
        PartKind.DoorFrame => 0.11,
        PartKind.Floor => 0.16,
        PartKind.Roof => 0.20,      // a roof is the first thing to come down
        _ => 0.2,
    };
}

/// <summary>
/// Everything the player has built, and whether it is still standing up.
/// <para>
/// This is the second half of the hybrid in §05 of the design. Terrain is a field you
/// carve; construction is authored parts that snap together, because those two goals are
/// in direct conflict and one representation serves neither well. Carving gives organic
/// results and is exactly wrong for a straight wall with a clean corner.
/// </para>
/// <para>
/// Both halves feed one load simulation, which is what ties them together: mining out a
/// cave ceiling lowers the support of everything above it, and a timber beam raises it
/// again. That turns mining from collection into engineering.
/// </para>
/// </summary>
public sealed class BuildGrid
{
    private const double CellSize = 4.0;

    /// <summary>Below this, a part is falling. Above it, it holds.</summary>
    public const double CollapseThreshold = 0.12;

    /// <summary>Seconds a doomed part sags and creaks before it falls. The warning is the point:
    /// a structure that collapses without notice reads as a bug rather than a consequence.</summary>
    public const double CollapseWarning = 2.5;

    private readonly IVolume _terrain;
    private readonly Dictionary<(int X, int Y, int Z), List<PlacedPart>> _cells = new();
    private readonly Dictionary<int, PlacedPart> _parts = new();
    private readonly Queue<PlacedPart> _frontier = new();
    private int _nextId = 1;

    /// <summary>Parts that fell during the most recent support update.</summary>
    public List<PlacedPart> Collapsed { get; } = new();

    public int Count => _parts.Count;
    public IEnumerable<PlacedPart> All => _parts.Values;

    public BuildGrid(IVolume terrain) => _terrain = terrain;

    // ── Placement ────────────────────────────────────────────────────────────────

    /// <summary>
    /// Snaps a proposed placement to a nearby compatible part, if one is close enough.
    /// Returns the position and yaw to build at.
    /// </summary>
    public (Vec3d Position, double Yaw, bool Snapped) Snap(PartKind kind, Vec3d position, double yaw,
                                                           double tolerance = 1.1)
    {
        PlacedPart? best = null;
        Vec3d bestPoint = position;
        double bestDistance = tolerance * tolerance;

        foreach (PlacedPart part in Near(position, 6.0))
        {
            foreach (Vec3d socket in SocketsOf(part, kind))
            {
                double distance = (socket - position).LengthSquared;
                if (distance >= bestDistance) continue;
                bestDistance = distance;
                bestPoint = socket;
                best = part;
            }
        }

        // Adopting the neighbour's rotation is what keeps a wall run straight instead of
        // fanning out by a degree or two per piece.
        return best is null ? (position, yaw, false) : (bestPoint, best.Yaw, true);
    }

    /// <summary>Sockets on a part where a new part of the given kind may attach.</summary>
    private static IEnumerable<Vec3d> SocketsOf(PlacedPart part, PartKind incoming)
    {
        Vec3d half = part.HalfExtent;
        double c = Math.Cos(part.Yaw), s = Math.Sin(part.Yaw);
        Vec3d Rotate(Vec3d v) => new(v.X * c - v.Z * s, v.Y, v.X * s + v.Z * c);

        switch (part.Kind)
        {
            case PartKind.Wall:
            case PartKind.DoorFrame:
                // Along the run, and above for a second storey.
                yield return part.Position + Rotate(new Vec3d(half.X * 2.0, 0, 0));
                yield return part.Position + Rotate(new Vec3d(-half.X * 2.0, 0, 0));
                yield return part.Position + new Vec3d(0, half.Y * 2.0, 0);
                if (incoming is PartKind.Floor or PartKind.Roof)
                {
                    yield return part.Position + new Vec3d(0, half.Y, 0);
                }
                break;

            case PartKind.Floor:
            case PartKind.Roof:
                yield return part.Position + Rotate(new Vec3d(half.X * 2.0, 0, 0));
                yield return part.Position + Rotate(new Vec3d(-half.X * 2.0, 0, 0));
                yield return part.Position + Rotate(new Vec3d(0, 0, half.Z * 2.0));
                yield return part.Position + Rotate(new Vec3d(0, 0, -half.Z * 2.0));
                if (incoming is PartKind.Wall or PartKind.Beam or PartKind.DoorFrame)
                {
                    yield return part.Position + new Vec3d(0, half.Y, 0);
                }
                break;

            case PartKind.Beam:
                yield return part.Position + new Vec3d(0, half.Y * 2.0, 0);
                yield return part.Position + new Vec3d(0, -half.Y * 2.0, 0);
                break;
        }
    }

    public PlacedPart Place(PartKind kind, Vec3d position, double yaw, ushort itemId)
    {
        var part = new PlacedPart
        {
            Id = _nextId++,
            Kind = kind,
            Position = position,
            Yaw = yaw,
            ItemId = itemId,
        };

        _parts[part.Id] = part;
        CellOf(position).Add(part);
        return part;
    }

    public bool Remove(int id)
    {
        if (!_parts.Remove(id, out PlacedPart? part)) return false;

        var key = CellKey(part.Position);
        if (_cells.TryGetValue(key, out List<PlacedPart>? list))
        {
            list.Remove(part);
            if (list.Count == 0) _cells.Remove(key);
        }
        return true;
    }

    public IEnumerable<PlacedPart> Near(Vec3d position, double radius)
    {
        int cells = (int)Math.Ceiling(radius / CellSize);
        (int cx, int cy, int cz) = CellKey(position);
        double radiusSquared = radius * radius;

        for (int z = cz - cells; z <= cz + cells; z++)
        {
            for (int y = cy - cells; y <= cy + cells; y++)
            {
                for (int x = cx - cells; x <= cx + cells; x++)
                {
                    if (!_cells.TryGetValue((x, y, z), out List<PlacedPart>? list)) continue;
                    foreach (PlacedPart part in list)
                    {
                        if ((part.Position - position).LengthSquared <= radiusSquared) yield return part;
                    }
                }
            }
        }
    }

    // ── Structural support ───────────────────────────────────────────────────────

    /// <summary>
    /// Recomputes how well load reaches every part, and brings down anything that has lost
    /// its footing.
    /// <para>
    /// A breadth-first flood outward from whatever is standing on solid ground, losing
    /// strength per link according to the material. Beams carry load roughly three times
    /// further than a roof panel, which is what makes them worth carrying underground.
    /// </para>
    /// </summary>
    public void UpdateSupport(double dt)
    {
        Collapsed.Clear();
        _frontier.Clear();

        foreach (PlacedPart part in _parts.Values)
        {
            part.IsGrounded = RestsOnTerrain(part);
            part.Support = part.IsGrounded ? 1.0 : 0.0;
            if (part.IsGrounded) _frontier.Enqueue(part);
        }

        while (_frontier.Count > 0)
        {
            PlacedPart part = _frontier.Dequeue();
            double passed = part.Support - part.SupportLoss;
            if (passed <= CollapseThreshold) continue;

            foreach (PlacedPart neighbour in Near(part.Position, 3.6))
            {
                if (ReferenceEquals(neighbour, part) || neighbour.Support >= passed) continue;
                neighbour.Support = passed;
                _frontier.Enqueue(neighbour);
            }
        }

        foreach (PlacedPart part in _parts.Values)
        {
            if (part.Support > CollapseThreshold)
            {
                part.CollapseCountdown = 0.0;
                continue;
            }

            // Sag and creak first. The warning window is what makes the rule feel fair.
            part.CollapseCountdown += dt;
            if (part.CollapseCountdown >= CollapseWarning) Collapsed.Add(part);
        }

        foreach (PlacedPart part in Collapsed) Remove(part.Id);
    }

    /// <summary>Whether the part's base is close enough to solid terrain to be load-bearing.</summary>
    private bool RestsOnTerrain(PlacedPart part)
    {
        Vec3d foot = part.Position - new Vec3d(0, part.HalfExtent.Y, 0);
        // Probe a little below; the field is negative inside rock.
        return _terrain.Sample(foot.X, foot.Y - 0.25, foot.Z) < 0.0;
    }

    private List<PlacedPart> CellOf(Vec3d position)
    {
        var key = CellKey(position);
        if (!_cells.TryGetValue(key, out List<PlacedPart>? list))
        {
            list = new List<PlacedPart>(4);
            _cells[key] = list;
        }
        return list;
    }

    private static (int, int, int) CellKey(Vec3d p) => (
        (int)Math.Floor(p.X / CellSize),
        (int)Math.Floor(p.Y / CellSize),
        (int)Math.Floor(p.Z / CellSize));

    /// <summary>The part kind an item places, or null if the item is not a building part.</summary>
    public static PartKind? KindOf(ushort itemId) => itemId switch
    {
        ItemIds.WallSegment => PartKind.Wall,
        ItemIds.FloorSegment => PartKind.Floor,
        ItemIds.RoofSegment => PartKind.Roof,
        ItemIds.SupportBeam => PartKind.Beam,
        ItemIds.DoorFrame => PartKind.DoorFrame,
        _ => null,
    };
}
