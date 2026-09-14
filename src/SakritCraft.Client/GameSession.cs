using System.Globalization;
using System.Numerics;
using SakritCraft.Content.Creatures;
using SakritCraft.Core.Math;
using SakritCraft.Render.Cameras;
using SakritCraft.Render.Entities;
using SakritCraft.Sim;
using SakritCraft.Sim.Ecs;
using SakritCraft.World.Generation;
using SakritCraft.World.Seed;
using Silk.NET.Maths;

namespace SakritCraft.Client;

/// <summary>
/// Joins the authoritative simulation to what is on screen.
/// <para>
/// The renderer has no idea the simulation exists and the simulation has no idea a renderer
/// does. This is the only place that knows both, which is exactly the seam §02 of the design
/// calls for: when multiplayer arrives, the simulation moves behind a socket and this class is
/// what changes.
/// </para>
/// </summary>
public sealed class GameSession
{
    /// <summary>Creatures further than this from the camera are not drawn.</summary>
    private const double DrawDistance = 220.0;

    private readonly Simulation _simulation;
    private readonly Entity _player;
    private readonly StructurePlacer _structures;

    public Simulation Simulation => _simulation;

    /// <summary>Creatures drawn in the most recent frame.</summary>
    public int VisibleCreatures { get; private set; }
    public int VisibleItems { get; private set; }

    public GameSession(DensityField field, WorldSeed seed, Vector3D<double> startPosition, double startTime = 0.515)
    {
        _simulation = new Simulation(field);
        _structures = new StructurePlacer(seed);

        var spawn = new Vec3d(startPosition.X, startPosition.Y, startPosition.Z);
        _player = _simulation.SpawnPlayer(spawn);

        // Default to just after sunset, so the first thing a player sees is the night rule taking
        // effect rather than an empty daylit field. Zero is dawn, a quarter is midday.
        _simulation.Clock.SetDayFraction(startTime);

        PopulateSettlements(spawn);
    }

    /// <summary>
    /// Places the residents of nearby settlements. Villagers are not spawned by the natural
    /// spawner; they belong to a structure, which is why they never despawn.
    /// </summary>
    private void PopulateSettlements(Vec3d origin)
    {
        int placed = 0;
        foreach (PlacedStructure structure in _structures.Near(origin.X, origin.Z, 900.0))
        {
            if (structure.Kind != StructureKind.Village) continue;

            for (int i = 0; i < 6; i++)
            {
                double angle = i * Math.Tau / 6.0;
                double x = structure.X + Math.Cos(angle) * structure.Radius * 0.5;
                double z = structure.Z + Math.Sin(angle) * structure.Radius * 0.5;
                double y = _simulation.Light.SurfaceHeight(x, z);
                _simulation.SpawnCreature(CreatureCatalog.Villager, new Vec3d(x, y + 0.1, z), despawnable: false);
                placed++;
            }
        }

        if (placed > 0)
        {
            SakritCraft.Render.RenderLog.Info("sim", $"Placed {placed} villagers in nearby settlements.");
        }
    }

    /// <summary>
    /// Advances the simulation and keeps the player entity where the camera is.
    /// <para>
    /// The camera flies freely for now, so the player entity is dragged along behind it rather
    /// than driving it. That is temporary, but it is enough for the rules that matter: spawning,
    /// despawning and creature attention all key off where the player is, and this puts the
    /// player exactly where the person is looking from.
    /// </para>
    /// </summary>
    public void Update(double dt, Vector3D<double> cameraPosition)
    {
        var transforms = _simulation.Entities.Store<Transform>();
        if (_simulation.Entities.IsAlive(_player) && transforms.Has(_player))
        {
            ref Transform transform = ref transforms.Get(_player);
            Vec3d target = new(cameraPosition.X, cameraPosition.Y - 1.6, cameraPosition.Z);

            // Velocity is derived from the camera's actual movement, because creature hearing
            // scales with how fast the player is going; leaving it at zero would make a flying
            // player permanently silent.
            ref Motion motion = ref _simulation.Entities.Store<Motion>().Get(_player);
            motion.Velocity = dt > 1e-6 ? (target - transform.Position) / dt : Vec3d.Zero;
            transform.Position = target;
        }

        _simulation.Advance(dt);
    }

    /// <summary>Fills the renderer's instance list from the simulation.</summary>
    public void Populate(EntityRenderer batch, Camera camera)
    {
        batch.Begin();
        VisibleCreatures = 0;
        VisibleItems = 0;

        var transforms = _simulation.Entities.Store<Transform>();
        var creatures = _simulation.Entities.Store<Creature>();
        var healths = _simulation.Entities.Store<Health>();
        var drops = _simulation.Entities.Store<ItemDrop>();

        double cullSquared = DrawDistance * DrawDistance;

        for (int i = 0; i < creatures.Count; i++)
        {
            Entity entity = _simulation.Entities.HandleOf(creatures.Owners[i]);
            if (!_simulation.Entities.IsAlive(entity)) continue;

            Vec3d position = transforms.Get(entity).Position;
            Vector3 relative = camera.RelativeTo(position.X, position.Y, position.Z);
            if (relative.LengthSquared() > cullSquared) continue;

            CreatureDefinition definition = CreatureCatalog.Get(creatures.Data[i].SpeciesId);

            // Hurt creatures flash toward red, which is the cheapest possible damage feedback
            // and the one thing a box genuinely can express.
            uint colour = EntityRenderer.ColourFor(creatures.Data[i].SpeciesId);
            if (healths.TryGet(entity, out Health health) && health.Fraction < 0.99f)
            {
                colour = Blend(colour, 0xFF3040E0, 1.0f - health.Fraction);
            }

            batch.Add(new EntityInstance
            {
                // The box is centred on the body, so lift it by half its height off the feet.
                RelativePosition = relative + new Vector3(0.0f, (float)(definition.Height * 0.5), 0.0f),
                Yaw = transforms.Get(entity).Yaw,
                HalfExtent = new Vector3(
                    (float)definition.Radius,
                    (float)(definition.Height * 0.5),
                    (float)definition.Radius),
                Colour = colour,
            });
            VisibleCreatures++;
        }

        for (int i = 0; i < drops.Count; i++)
        {
            Entity entity = _simulation.Entities.HandleOf(drops.Owners[i]);
            if (!_simulation.Entities.IsAlive(entity)) continue;

            Vec3d position = transforms.Get(entity).Position;
            Vector3 relative = camera.RelativeTo(position.X, position.Y, position.Z);
            if (relative.LengthSquared() > cullSquared) continue;

            batch.Add(new EntityInstance
            {
                RelativePosition = relative + new Vector3(0.0f, 0.18f, 0.0f),
                // Spin slowly so a dropped item catches the eye the way one should.
                Yaw = (float)(_simulation.TickCount * 0.04 + entity.Index),
                HalfExtent = new Vector3(0.18f, 0.18f, 0.18f),
                Colour = EntityRenderer.ColourForItem(drops.Data[i].ItemId),
            });
            VisibleItems++;
        }
    }

    /// <summary>
    /// Drives the renderer's sun and sky from the simulation clock.
    /// <para>
    /// These two must never disagree. The simulation decides whether it is dark enough for
    /// something to spawn; the renderer decides whether it looks dark. If the sun is painted at
    /// a fixed angle while the clock says night, the player is told one thing and ruled by
    /// another, and the single most important rule in the game becomes unreadable.
    /// </para>
    /// </summary>
    public void ApplyLighting(SakritCraft.Render.SceneLighting lighting)
    {
        double elevation = _simulation.Clock.SunElevationDegrees;
        double brightness = _simulation.Clock.SkyBrightness;

        // The sun tracks a full circle through the day, rising in the east and setting west.
        lighting.SunElevationDegrees = (float)Math.Max(elevation, -12.0);
        lighting.SunAzimuthDegrees = (float)(90.0 + _simulation.Clock.DayFraction * 360.0);

        // Below the horizon the sun contributes nothing; what remains is moonlight, which is
        // roughly four hundred thousand times dimmer but not zero.
        float day = (float)brightness;
        // Moonlight is physically about four hundred thousand times dimmer than sunlight, but a
        // dark-adapted eye closes most of that gap. Rendering the true ratio gives a black screen,
        // which is accurate and useless; this is what a person standing outside actually sees.
        lighting.SunIntensity = 9.5f * day + 0.62f * (1.0f - day);
        lighting.SunColor = Lerp(new Vector3(0.62f, 0.70f, 0.95f), new Vector3(1.0f, 0.88f, 0.70f), day);

        lighting.SkyZenith = Lerp(new Vector3(0.020f, 0.034f, 0.082f), new Vector3(0.11f, 0.26f, 0.64f), day);
        lighting.SkyHorizon = Lerp(new Vector3(0.048f, 0.064f, 0.108f), new Vector3(0.66f, 0.74f, 0.86f), day);
        lighting.GroundAmbient = Lerp(new Vector3(0.030f, 0.040f, 0.062f), new Vector3(0.26f, 0.22f, 0.17f), day);

        // A stand-in for eye adaptation: open up at night so a dark scene is legible rather
        // than black, which is what a real eye does over about half a minute.
        lighting.Exposure = 0.9f + 0.85f * (1.0f - day);

        // Haze thins after dark, because most of what makes daytime distance pale is sunlight
        // scattering toward the camera, and at night there is none to scatter.
        lighting.FogDensity = (float)(0.00019 * (0.35 + 0.65 * day));
    }

    private static Vector3 Lerp(Vector3 a, Vector3 b, float t) => a + (b - a) * Math.Clamp(t, 0.0f, 1.0f);

    private static uint Blend(uint a, uint b, float t)
    {
        t = Math.Clamp(t, 0.0f, 1.0f);
        uint Channel(int shift)
        {
            float x = (a >> shift) & 0xFF, y = (b >> shift) & 0xFF;
            return (uint)Math.Clamp(x + (y - x) * t, 0.0f, 255.0f) << shift;
        }
        return 0xFF000000u | Channel(0) | Channel(8) | Channel(16);
    }

    /// <summary>One line of state for the window title.</summary>
    public string Status()
    {
        var healths = _simulation.Entities.Store<Health>();
        var hungers = _simulation.Entities.Store<Hunger>();

        float hp = healths.TryGet(_player, out Health health) ? health.Current : 0.0f;
        float food = hungers.TryGet(_player, out Hunger hunger) ? hunger.Food : 0.0f;

        int hostiles = 0, wildlife = 0, neutral = 0;
        var creatures = _simulation.Entities.Store<Creature>();
        for (int i = 0; i < creatures.Count; i++)
        {
            switch (creatures.Data[i].Faction)
            {
                case Faction.Hostile: hostiles++; break;
                case Faction.Wildlife: wildlife++; break;
                default: neutral++; break;
            }
        }

        var transforms = _simulation.Entities.Store<Transform>();
        int light = transforms.TryGet(_player, out Transform transform)
            ? _simulation.Light.Effective(transform.Position + new Vec3d(0, 1.0, 0))
            : 0;

        return string.Create(CultureInfo.InvariantCulture,
            $"{_simulation.Clock} | light {light,2} | hp {hp,4:F1} food {food,4:F1} | " +
            $"{hostiles} hostile {wildlife} wild {neutral} folk");
    }
}
