using SakritCraft.Content.Creatures;
using SakritCraft.Core.Math;
using SakritCraft.Sim.Ecs;
using SakritCraft.Sim.Lighting;
using SakritCraft.World.Generation;

namespace SakritCraft.Sim;

public sealed partial class Simulation
{
    /// <summary>Ticks between spawn passes. Every tick is wasteful; this is Minecraft's cadence.</summary>
    private const int SpawnIntervalTicks = 8;

    /// <summary>Candidate positions tried per pass, per player.</summary>
    private const int SpawnAttempts = 12;

    /// <summary>Nothing appears nearer than this, so creatures never pop into view.</summary>
    private const double MinSpawnDistance = 24.0;

    /// <summary>Nothing appears further than this, because it would despawn before mattering.</summary>
    private const double MaxSpawnDistance = 88.0;

    /// <summary>Clear headroom a spawn point needs, in metres.</summary>
    private const double RequiredClearance = 2.0;

    /// <summary>
    /// Beyond this, a creature may be culled at random even though it is still within its
    /// species' hard despawn range.
    /// <para>
    /// Without this churn the population is permanent: creatures that spawn in the caves
    /// under a player never die, never leave, and hold the cap indefinitely, so nothing new
    /// can appear on the surface no matter how dark it gets. Torches then stop mattering,
    /// which quietly breaks the single most important rule in the game.
    /// </para>
    /// </summary>
    private const double SoftDespawnDistance = 44.0;

    /// <summary>Chance per despawn pass that a creature beyond the soft distance is culled.</summary>
    private const double SoftDespawnChance = 0.05;

    /// <summary>Most hostiles alive near one player at once.</summary>
    public int HostileCap { get; set; } = 26;

    /// <summary>Most wildlife alive near one player at once.</summary>
    public int WildlifeCap { get; set; } = 14;

    /// <summary>Set false to stop natural spawning, for the <c>/gamerule</c> command and for tests.</summary>
    public bool NaturalSpawning { get; set; } = true;

    private readonly List<CreatureDefinition> _eligible = new(8);

    private void UpdateSpawning()
    {
        if (!NaturalSpawning) return;
        if (TickCount % SpawnIntervalTicks != 0) return;

        foreach (Entity player in _players)
        {
            if (!Entities.IsAlive(player) || PlayerTags.Get(player).IsDead) continue;

            Vec3d origin = Transforms.Get(player).Position;
            // Count out to the despawn distance, not the spawn distance. Counting the smaller
            // radius leaves a band where creatures are neither counted nor culled, and the
            // population there grows without limit until it saturates the cap from outside.
            (int hostiles, int wildlife) = CountNear(origin, 170.0);

            for (int attempt = 0; attempt < SpawnAttempts; attempt++)
            {
                if (hostiles >= HostileCap && wildlife >= WildlifeCap) break;
                if (!TryFindSpawnSite(origin, out Vec3d site, out bool openSky)) continue;

                CreatureDefinition? choice = ChooseSpecies(site, openSky, hostiles, wildlife);
                if (choice is null) continue;

                int packSize = NextInt(choice.Spawn!.Value.PackMin, choice.Spawn.Value.PackMax);
                for (int i = 0; i < packSize; i++)
                {
                    // Spread the pack out a little so they do not stand inside one another.
                    Vec3d offset = i == 0
                        ? Vec3d.Zero
                        : new Vec3d((NextDouble() - 0.5) * 4.0, 0, (NextDouble() - 0.5) * 4.0);

                    Vec3d position = site + offset;
                    position = new Vec3d(position.X, Light.SurfaceHeight(position.X, position.Z), position.Z);
                    if (offset.LengthSquared > 0 && Math.Abs(position.Y - site.Y) > 3.0) continue;

                    SpawnCreature(choice.Id, i == 0 ? site : position);

                    if (choice.Faction == CreatureFaction.Hostile) hostiles++;
                    else wildlife++;
                }
            }
        }
    }

    /// <summary>
    /// Looks for somewhere a creature could stand: a solid surface with headroom, in the shell
    /// of distance around the player where a spawn is neither visible nor wasted.
    /// </summary>
    private bool TryFindSpawnSite(Vec3d origin, out Vec3d site, out bool openSky)
    {
        site = Vec3d.Zero;
        openSky = false;

        double angle = NextDouble() * Math.Tau;
        double distance = MinSpawnDistance + NextDouble() * (MaxSpawnDistance - MinSpawnDistance);
        double x = origin.X + Math.Cos(angle) * distance;
        double z = origin.Z + Math.Sin(angle) * distance;

        double surface = Light.SurfaceHeight(x, z);

        // Half the attempts look underground, which is where the cave dwellers live and where a
        // surface-only search would never place anything.
        bool underground = NextDouble() < 0.5;
        if (!underground)
        {
            if (surface <= DensityField.WorldBottom + 1.0) return false;
            site = new Vec3d(x, surface + 0.05, z);
            openSky = true;
            return HasClearance(site);
        }

        // Search a band around the player's own depth rather than a fixed slab under the
        // surface. Spawning ninety metres down while the player stands in daylight fills the
        // population cap with creatures they will never meet, and the surface then stays empty
        // no matter how dark it gets.
        double top = Math.Min(surface - 3.0, origin.Y + 18.0);
        double bottom = Math.Max(DensityField.WorldBottom + 4.0, origin.Y - 30.0);
        if (top <= bottom) return false;

        double y = bottom + NextDouble() * (top - bottom);
        bool previousSolid = Field.Sample(x, y + 3.0, z) < 0.0;

        for (double probe = y + 3.0; probe > y - 24.0; probe -= 0.5)
        {
            bool solid = Field.Sample(x, probe, z) < 0.0;
            if (!previousSolid && solid)
            {
                site = new Vec3d(x, probe + 0.5, z);
                openSky = false;
                return HasClearance(site);
            }
            previousSolid = solid;
        }

        return false;
    }

    private bool HasClearance(Vec3d site)
    {
        // Feet must be in air, and there must be room to stand.
        if (Field.Sample(site.X, site.Y + 0.2, site.Z) < 0.0) return false;
        if (Field.Sample(site.X, site.Y + RequiredClearance, site.Z) < 0.0) return false;
        // And something solid to stand on.
        return Field.Sample(site.X, site.Y - 0.4, site.Z) < 0.0;
    }

    /// <summary>
    /// Picks a species allowed here, weighted. Returns null when nothing qualifies, which is
    /// the common case in daylight on open ground and is exactly the point.
    /// </summary>
    private CreatureDefinition? ChooseSpecies(Vec3d site, bool openSky, int hostiles, int wildlife)
    {
        int light = Light.Effective(site + new Vec3d(0, 1.0, 0));
        ColumnSample column = Field.Landform.Sample(site.X, site.Z);

        _eligible.Clear();
        double totalWeight = 0.0;

        foreach (CreatureDefinition definition in CreatureCatalog.All)
        {
            if (definition.Spawn is not SpawnRule rule) continue;

            bool hostile = definition.Faction == CreatureFaction.Hostile;
            if (hostile && hostiles >= HostileCap) continue;
            if (!hostile && wildlife >= WildlifeCap) continue;

            if (light > rule.MaxLight) continue;
            if (site.Y < rule.MinY || site.Y > rule.MaxY) continue;
            if (column.TemperatureC < rule.MinTemperature || column.TemperatureC > rule.MaxTemperature) continue;
            if (rule.RequiresSky && !openSky) continue;
            if (rule.ForbidsSky && openSky) continue;

            _eligible.Add(definition);
            totalWeight += rule.Weight;
        }

        if (_eligible.Count == 0) return null;

        double roll = NextDouble() * totalWeight;
        foreach (CreatureDefinition definition in _eligible)
        {
            roll -= definition.Spawn!.Value.Weight;
            if (roll <= 0.0) return definition;
        }
        return _eligible[^1];
    }

    private (int Hostiles, int Wildlife) CountNear(Vec3d origin, double radius)
    {
        int hostiles = 0, wildlife = 0;
        double radiusSquared = radius * radius;

        ReadOnlySpan<int> owners = Creatures.Owners;
        for (int i = 0; i < owners.Length; i++)
        {
            Entity entity = EntityAt(owners[i]);
            if (!Entities.IsAlive(entity)) continue;
            if ((Transforms.Get(entity).Position - origin).LengthSquared > radiusSquared) continue;

            if (Creatures.Data[i].Faction == Faction.Hostile) hostiles++;
            else if (Creatures.Data[i].Faction == Faction.Wildlife) wildlife++;
        }

        return (hostiles, wildlife);
    }

    /// <summary>
    /// Removes naturally spawned creatures nobody is near. Without this the world fills
    /// permanently with everything that ever spawned, and the population cap then stops any
    /// new creature appearing where the player actually is.
    /// </summary>
    private void UpdateDespawning()
    {
        if (TickCount % 40 != 0) return;

        ReadOnlySpan<int> owners = Creatures.Owners;
        for (int i = owners.Length - 1; i >= 0; i--)
        {
            Entity entity = EntityAt(owners[i]);
            if (!Entities.IsAlive(entity)) continue;
            if (!Creatures.Data[i].Despawnable) continue;

            CreatureDefinition definition = CreatureCatalog.Get(Creatures.Data[i].SpeciesId);
            if (definition.DespawnDistance <= 0.0) continue;

            Vec3d position = Transforms.Get(entity).Position;
            double nearest = double.MaxValue;
            foreach (Entity player in _players)
            {
                if (!Entities.IsAlive(player)) continue;
                nearest = Math.Min(nearest, (Transforms.Get(player).Position - position).Length);
            }

            bool beyondHardLimit = nearest > definition.DespawnDistance;
            bool culledForChurn = nearest > SoftDespawnDistance && NextDouble() < SoftDespawnChance;
            if (!beyondHardLimit && !culledForChurn) continue;

            _events.Add(new SimEvent(SimEventKind.Despawned, entity, position, 0));
            Entities.Destroy(entity);
        }
    }

    // ── Placed light, which is how a player makes somewhere safe ─────────────────

    /// <summary>Places a light source and returns whether it was accepted.</summary>
    public bool PlaceTorch(Vec3d position)
    {
        Light.AddEmitter(new LightEmitter(position, LightField.TorchLevel));
        return true;
    }

    public bool RemoveTorch(Vec3d position) => Light.RemoveEmitterNear(position);
}
