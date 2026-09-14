using SakritCraft.Content.Creatures;
using SakritCraft.Core.Math;
using SakritCraft.Physics;
using SakritCraft.Sim.Ecs;
using SakritCraft.World.Editing;
using SakritCraft.World.Generation;

namespace SakritCraft.Sim;

/// <summary>What a swing is currently achieving, for the crosshair and the mining sound.</summary>
public readonly record struct MiningState(
    bool Hit,
    Vec3d Point,
    Vec3d Normal,
    byte Material,
    /// <summary>Progress toward breaking, 0 to 1.</summary>
    float Progress,
    bool Broke,
    /// <summary>Why nothing is happening, when nothing is. Empty when progress is being made.</summary>
    string Blocked)
{
    public static MiningState Miss => new(false, Vec3d.Zero, Vec3d.UnitY, 0, 0.0f, false, string.Empty);
}

public sealed partial class Simulation
{
    /// <summary>How far a swing reaches, in metres.</summary>
    public const double MiningReach = 4.5;

    /// <summary>Progress is abandoned if the aim wanders further than this from where it started.</summary>
    private const double AimTolerance = 1.1;

    private readonly Dictionary<int, (Vec3d Point, float Progress)> _miningProgress = new();

    /// <summary>
    /// Advances a swing along a ray, and carves the rock when it completes.
    /// <para>
    /// The break-time formula keeps Minecraft's shape deliberately, so anyone who has internalised
    /// that game's pacing is at home. What differs is the effect: instead of a cube disappearing,
    /// a sphere is subtracted from the density field, so the hole has the shape and the slope of
    /// the swing that made it, and the yield is proportional to the volume actually removed rather
    /// than to a count of blocks.
    /// </para>
    /// </summary>
    public MiningState Mine(Entity miner, Vec3d origin, Vec3d direction, double dt)
    {
        if (!Entities.IsAlive(miner)) return MiningState.Miss;

        TraceHit hit = SphereTrace.Ray(_volume, origin, direction, MiningReach);
        if (!hit.Hit)
        {
            _miningProgress.Remove(miner.Index);
            return MiningState.Miss;
        }

        // Sample just inside the surface: exactly on it the field is zero and the material rule
        // would be reading a point that is arguably already air.
        Vec3d inside = hit.Point - hit.Normal * 0.15;
        byte material = TerrainMaterials.MaterialAt(Field, inside.X, inside.Y, inside.Z);

        Inventory inventory = InventoryOf(miner);
        bool wantsPickaxe = TerrainMaterials.WantsPickaxe(material);
        ItemDefinition? tool = inventory.BestTool(wantsPickaxe ? ToolKind.Pickaxe : ToolKind.Shovel)
                            ?? inventory.BestTool(wantsPickaxe ? ToolKind.Shovel : ToolKind.Pickaxe);

        int tier = tool?.Tier ?? 0;
        int required = TerrainMaterials.RequiredTier(material);
        if (tier < required)
        {
            _miningProgress.Remove(miner.Index);
            return new MiningState(true, hit.Point, hit.Normal, material, 0.0f, false,
                $"{TerrainMaterials.Name(material)} needs a tier {required} tool");
        }

        // Restarting whenever the aim wanders is what stops a player chipping at five different
        // spots at once and breaking all of them together.
        if (_miningProgress.TryGetValue(miner.Index, out (Vec3d Point, float Progress) state)
            && (state.Point - hit.Point).Length > AimTolerance)
        {
            state = (hit.Point, 0.0f);
        }
        else if (state.Progress <= 0.0f)
        {
            state = (hit.Point, state.Progress);
        }

        // Minecraft's shape: hardness against tool speed, with a heavy penalty for the wrong tool.
        double efficiency = tool?.Efficiency ?? 0.35;
        bool rightTool = tool is not null
                      && ((wantsPickaxe && tool.Tool == ToolKind.Pickaxe)
                       || (!wantsPickaxe && tool.Tool == ToolKind.Shovel));
        double breakSeconds = TerrainMaterials.Hardness(material) * 1.5 / efficiency;
        if (!rightTool) breakSeconds *= 5.0;

        state.Point = hit.Point;
        state.Progress += (float)(dt / Math.Max(breakSeconds, 0.05));
        _miningProgress[miner.Index] = state;

        if (state.Progress < 1.0f)
        {
            return new MiningState(true, hit.Point, hit.Normal, material, state.Progress, false, string.Empty);
        }

        _miningProgress.Remove(miner.Index);
        double radius = tool?.CarveRadius > 0.0 ? tool.CarveRadius : 0.22;
        Carve(hit.Point - hit.Normal * (radius * 0.55), radius, material, miner);

        return new MiningState(true, hit.Point, hit.Normal, material, 1.0f, true, string.Empty);
    }

    /// <summary>
    /// Removes a sphere of terrain and drops what came out of it.
    /// <para>
    /// Yield is proportional to the volume removed, not to a block count, so a wider tool mines
    /// faster in every sense. Roughly 60% of a brush centred on a surface is rock; assuming all of
    /// it would pay a player for the air they also removed.
    /// </para>
    /// </summary>
    public void Carve(Vec3d centre, double radius, byte material, Entity miner = default)
    {
        Field.Edits.Add(new TerrainEdit(centre.X, centre.Y, centre.Z, (float)radius,
            BrushMode.Carve, TerrainMaterials.None));

        _events.Add(new SimEvent(SimEventKind.TerrainEdited, miner, centre, (float)radius));
        _pendingTerrainEdits.Add((centre, radius));

        double volume = 4.0 / 3.0 * Math.PI * radius * radius * radius * 0.6;
        int yield = (int)Math.Round(volume * TerrainMaterials.YieldPerCubicMetre(material));
        if (yield <= 0) return;

        ushort item = ItemFor(material);
        if (item == ItemIds.None) return;

        // Straight into the pack when there is room, on the ground when there is not.
        int leftover = Entities.IsAlive(miner) ? InventoryOf(miner).Add(item, yield) : yield;
        if (leftover > 0) DropItem(item, leftover, centre + new Vec3d(0, 0.3, 0));
    }

    /// <summary>Adds a sphere of material. The same operation as mining with the sign flipped,
    /// which is why repairing a wall and digging a tunnel use one verb.</summary>
    public bool Fill(Vec3d centre, double radius, byte material, Entity builder = default)
    {
        if (Entities.IsAlive(builder))
        {
            ushort item = ItemFor(material);
            double volume = 4.0 / 3.0 * Math.PI * radius * radius * radius * 0.6;
            int cost = Math.Max(1, (int)Math.Round(volume * TerrainMaterials.YieldPerCubicMetre(material)));
            if (InventoryOf(builder).Remove(item, cost) < cost) return false;
        }

        Field.Edits.Add(new TerrainEdit(centre.X, centre.Y, centre.Z, (float)radius,
            BrushMode.Fill, material));
        _events.Add(new SimEvent(SimEventKind.TerrainEdited, builder, centre, (float)radius));
        _pendingTerrainEdits.Add((centre, radius));
        return true;
    }

    /// <summary>What a material yields when mined.</summary>
    public static ushort ItemFor(byte material) => material switch
    {
        TerrainMaterials.Rock => ItemIds.Stone,
        TerrainMaterials.Soil => ItemIds.Soil,
        TerrainMaterials.Sand => ItemIds.Sand,
        TerrainMaterials.Gravel => ItemIds.Gravel,
        TerrainMaterials.Snow => ItemIds.None,
        _ => ItemIds.None,
    };

    /// <summary>The material a carried item places back into the world.</summary>
    public static byte MaterialFor(ushort itemId) => itemId switch
    {
        ItemIds.Stone => TerrainMaterials.Rock,
        ItemIds.Soil => TerrainMaterials.Soil,
        ItemIds.Sand => TerrainMaterials.Sand,
        ItemIds.Gravel => TerrainMaterials.Gravel,
        _ => TerrainMaterials.None,
    };
}
