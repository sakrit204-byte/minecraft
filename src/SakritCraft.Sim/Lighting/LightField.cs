using SakritCraft.Core.Math;
using SakritCraft.World.Generation;

namespace SakritCraft.Sim.Lighting;

/// <summary>A placed light source: a torch, a lantern, a lava pool.</summary>
public readonly record struct LightEmitter(Vec3d Position, byte Level)
{
    /// <summary>How far this source still contributes, in metres. One level per metre.</summary>
    public double Reach => Level;
}

/// <summary>
/// Gameplay light: the discrete quantity that decides what spawns, what grows, and
/// whether the player is in the dark.
/// <para>
/// This is deliberately separate from anything the renderer does. Visual light is
/// physical, continuous, and expensive; gameplay light is an integer from 0 to 15 that a
/// player can reason about and rely on. Trying to drive spawning from the rendered image
/// is what makes realistic voxel games feel arbitrary: a rule you cannot predict is not
/// a rule, it is a surprise. A torch is level 14 and reaches 14 metres, exactly as
/// players already expect.
/// </para>
/// <para>
/// Block light falls off with true distance from each emitter rather than by a
/// breadth-first flood through cells. In a continuous world there are no cells to flood,
/// and the cost of an exact propagation is not repaid: the visible difference is that
/// light no longer bends around a corner, and in exchange any position in the world can
/// be queried in microseconds without maintaining a grid.
/// </para>
/// </summary>
public sealed class LightField
{
    /// <summary>Brightest possible light, matching Minecraft's scale.</summary>
    public const int MaxLevel = 15;

    /// <summary>A torch. Reaches fourteen metres, which is the number players already know.</summary>
    public const byte TorchLevel = 14;

    /// <summary>Level at or below which most hostile creatures may spawn.</summary>
    public const int HostileSpawnThreshold = 7;

    /// <summary>Level at or above which most crops grow.</summary>
    public const int CropGrowthThreshold = 9;

    private const double CellSize = 16.0;

    private readonly DensityField _field;
    private readonly Dictionary<(int X, int Y, int Z), List<LightEmitter>> _emitters = new();
    private readonly Dictionary<(int X, int Z), double> _surfaceCache = new();

    /// <summary>Fraction of full sky light currently reaching the ground, from the day cycle.</summary>
    public double SkyBrightness { get; set; } = 1.0;

    public int EmitterCount { get; private set; }

    public LightField(DensityField field) => _field = field;

    // ── Emitters ─────────────────────────────────────────────────────────────────

    public void AddEmitter(in LightEmitter emitter)
    {
        var cell = CellOf(emitter.Position);
        if (!_emitters.TryGetValue(cell, out List<LightEmitter>? list))
        {
            list = new List<LightEmitter>(4);
            _emitters[cell] = list;
        }
        list.Add(emitter);
        EmitterCount++;
    }

    /// <summary>Removes the emitter nearest the given point within a small tolerance.</summary>
    public bool RemoveEmitterNear(Vec3d position, double tolerance = 0.6)
    {
        var cell = CellOf(position);
        if (!_emitters.TryGetValue(cell, out List<LightEmitter>? list)) return false;

        int best = -1;
        double bestDistance = tolerance * tolerance;
        for (int i = 0; i < list.Count; i++)
        {
            double d = (list[i].Position - position).LengthSquared;
            if (d <= bestDistance) { bestDistance = d; best = i; }
        }

        if (best < 0) return false;
        list.RemoveAt(best);
        EmitterCount--;
        if (list.Count == 0) _emitters.Remove(cell);
        return true;
    }

    public void ClearEmitters()
    {
        _emitters.Clear();
        EmitterCount = 0;
    }

    // ── Queries ──────────────────────────────────────────────────────────────────

    /// <summary>
    /// Light contributed by placed sources, 0 to 15. One level lost per metre, so a torch
    /// at level 14 lights a 14 metre radius.
    /// </summary>
    public int BlockLight(Vec3d position)
    {
        int best = 0;
        (int cx, int cy, int cz) = CellOf(position);

        // A level-15 source reaches 15 m, so one 16 m cell in each direction always suffices.
        for (int z = cz - 1; z <= cz + 1; z++)
        {
            for (int y = cy - 1; y <= cy + 1; y++)
            {
                for (int x = cx - 1; x <= cx + 1; x++)
                {
                    if (!_emitters.TryGetValue((x, y, z), out List<LightEmitter>? list)) continue;

                    foreach (LightEmitter emitter in list)
                    {
                        double distance = (emitter.Position - position).Length;
                        int level = (int)Math.Floor(emitter.Level - distance);
                        if (level > best) best = level;
                    }
                }
            }
        }

        return Math.Clamp(best, 0, MaxLevel);
    }

    /// <summary>
    /// Light from the open sky, 0 to 15, before the day cycle is applied. Full above the
    /// terrain surface and zero below it, which is what makes caves dark and therefore
    /// dangerous without any extra rule.
    /// </summary>
    public int SkyLight(Vec3d position)
    {
        double surface = SurfaceHeight(position.X, position.Z);
        if (position.Y >= surface) return MaxLevel;

        // A metre or two under an overhang is dim rather than pitch black, which stops a
        // shallow ledge from spawning creatures right next to the player in daylight.
        double depth = surface - position.Y;
        return depth >= 3.0 ? 0 : (int)Math.Round(MaxLevel * (1.0 - depth / 3.0));
    }

    /// <summary>
    /// What the gameplay rules actually read: the brighter of placed light and sky light
    /// scaled by time of day.
    /// </summary>
    public int Effective(Vec3d position)
    {
        int sky = (int)Math.Floor(SkyLight(position) * SkyBrightness);
        return Math.Max(BlockLight(position), Math.Clamp(sky, 0, MaxLevel));
    }

    /// <summary>True where a hostile creature is allowed to appear.</summary>
    public bool IsDarkEnoughToSpawn(Vec3d position, int threshold = HostileSpawnThreshold)
        => Effective(position) <= threshold;

    /// <summary>Terrain surface height at a column, memoised because spawning probes the
    /// same columns repeatedly and each miss costs a bisection through the density field.</summary>
    public double SurfaceHeight(double x, double z)
    {
        var key = ((int)Math.Floor(x), (int)Math.Floor(z));
        if (_surfaceCache.TryGetValue(key, out double cached)) return cached;

        double height = _field.SurfaceHeight(x, z);
        if (_surfaceCache.Count > 200_000) _surfaceCache.Clear();
        _surfaceCache[key] = height;
        return height;
    }

    private static (int, int, int) CellOf(Vec3d p) => (
        (int)Math.Floor(p.X / CellSize),
        (int)Math.Floor(p.Y / CellSize),
        (int)Math.Floor(p.Z / CellSize));
}
