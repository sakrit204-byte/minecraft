using SakritCraft.Content.Creatures;
using SakritCraft.Core.Hashing;
using SakritCraft.Core.Math;
using SakritCraft.Physics;
using SakritCraft.Sim.Ecs;
using SakritCraft.Sim.Lighting;
using SakritCraft.Sim.Time;
using SakritCraft.World.Generation;

namespace SakritCraft.Sim;

/// <summary>Something the simulation did that the presentation layer may want to react to.</summary>
public readonly record struct SimEvent(SimEventKind Kind, Entity Subject, Vec3d Position, float Value)
{
    public override string ToString() => $"{Kind} {Subject} at {Position} ({Value:F1})";
}

public enum SimEventKind : byte
{
    Damaged,
    Died,
    Spawned,
    Despawned,
    ItemDropped,
    ItemPickedUp,
    Exploded,
    PlayerRespawned,
    /// <summary>Terrain was carved or filled. Value is the brush radius; the renderer rebuilds
    /// every chunk the sphere touches.</summary>
    TerrainEdited,
}

/// <summary>
/// The authority. Everything that is true about the game lives here, advanced on a fixed
/// tick, and nothing in it knows that a renderer exists.
/// <para>
/// Fixed twenty ticks a second, matching Minecraft, so that rules ported from it keep
/// their feel: a growth chance per tick or a spawn attempt per tick means the same thing
/// at the same rate. Presentation interpolates between ticks; it never drives them.
/// </para>
/// </summary>
public sealed partial class Simulation
{
    /// <summary>Ticks per second. Fixed, and not a setting: half the balance numbers assume it.</summary>
    public const int TickRate = 20;

    /// <summary>Seconds per tick.</summary>
    public const double TickSeconds = 1.0 / TickRate;

    private readonly IVolume _volume;
    private readonly List<SimEvent> _events = new(64);
    private readonly Dictionary<int, Inventory> _inventories = new();
    private readonly List<Entity> _players = new();
    private ulong _randomState;

    public EntityStore Entities { get; } = new();
    public LightField Light { get; }
    public WorldClock Clock { get; } = new();
    public DensityField Field { get; }

    /// <summary>Total ticks advanced since the world began.</summary>
    public long TickCount { get; private set; }

    /// <summary>What happened during the most recent tick. Cleared at the start of each one.</summary>
    public IReadOnlyList<SimEvent> Events => _events;

    /// <summary>
    /// Terrain changes awaiting a mesh rebuild.
    /// <para>
    /// Kept separately from <see cref="Events"/>, which is cleared at the start of every tick.
    /// Carving can happen between ticks, from input, and an edit published only as a tick event
    /// is thrown away before anyone reads it: the rock is gone from the field and the drops
    /// appear, but the mesh still shows solid ground. This list survives until the renderer has
    /// acted on it.
    /// </para>
    /// </summary>
    public IReadOnlyList<(Vec3d Centre, double Radius)> PendingTerrainEdits => _pendingTerrainEdits;

    private readonly List<(Vec3d Centre, double Radius)> _pendingTerrainEdits = new();

    /// <summary>Called once the renderer has rebuilt the affected chunks.</summary>
    public void ClearPendingTerrainEdits() => _pendingTerrainEdits.Clear();

    /// <summary>Every living player, for spawning and despawn distance checks.</summary>
    public IReadOnlyList<Entity> Players => _players;

    // Component stores, fetched once so systems do not pay a dictionary lookup per tick.
    internal readonly ComponentStore<Transform> Transforms;
    internal readonly ComponentStore<Motion> Motions;
    internal readonly ComponentStore<Health> Healths;
    internal readonly ComponentStore<Creature> Creatures;
    internal readonly ComponentStore<Perception> Perceptions;
    internal readonly ComponentStore<Combat> Combats;
    internal readonly ComponentStore<PlayerTag> PlayerTags;
    internal readonly ComponentStore<Hunger> Hungers;
    internal readonly ComponentStore<ItemDrop> ItemDrops;
    internal readonly ComponentStore<Lifetime> Lifetimes;

    public Simulation(DensityField field, ulong seed = 0x5EED_5A4B_17C1_0001UL)
    {
        Field = field;
        _volume = new DensityFieldVolume(field);
        Light = new LightField(field);
        _randomState = seed;

        Transforms = Entities.Store<Transform>();
        Motions = Entities.Store<Motion>();
        Healths = Entities.Store<Health>();
        Creatures = Entities.Store<Creature>();
        Perceptions = Entities.Store<Perception>();
        Combats = Entities.Store<Combat>();
        PlayerTags = Entities.Store<PlayerTag>();
        Hungers = Entities.Store<Hunger>();
        ItemDrops = Entities.Store<ItemDrop>();
        Lifetimes = Entities.Store<Lifetime>();
    }

    // ── Deterministic randomness ─────────────────────────────────────────────────
    // The simulation's own stream, separate from world generation, so that spawning and
    // combat rolls cannot perturb the terrain a seed produces.

    internal double NextDouble() => Hash64.ToUnit(Hash64.Next(ref _randomState));

    internal int NextInt(int minInclusive, int maxInclusive)
        => minInclusive + (int)(NextDouble() * (maxInclusive - minInclusive + 1));

    // ── Creation ─────────────────────────────────────────────────────────────────

    public Entity SpawnPlayer(Vec3d position)
    {
        Entity entity = Entities.Create();
        Transforms.Add(entity, new Transform { Position = position });
        Motions.Add(entity, default);
        Healths.Add(entity, Health.With(max: 20.0f, regenPerSecond: 0.5f, regenDelay: 5.0f));
        Hungers.Add(entity, Hunger.Full);
        PlayerTags.Add(entity, new PlayerTag { SpawnPoint = position });
        Combats.Add(entity, new Combat
        {
            Damage = 1.0f, DamageType = DamageType.Blunt, Reach = 3.2f, AttackInterval = 0.55f, Knockback = 3.0f,
        });
        _inventories[entity.Index] = new Inventory();
        _players.Add(entity);
        _events.Add(new SimEvent(SimEventKind.Spawned, entity, position, 0));
        return entity;
    }

    public Entity SpawnCreature(ushort speciesId, Vec3d position, bool despawnable = true)
    {
        CreatureDefinition definition = CreatureCatalog.Get(speciesId);
        Entity entity = Entities.Create();

        Transforms.Add(entity, new Transform { Position = position, Yaw = (float)(NextDouble() * Math.Tau) });
        Motions.Add(entity, default);

        var health = Health.With(definition.MaxHealth, definition.HealthRegenPerSecond);
        health.Resistance = new DamageResistance
        {
            Slashing = definition.ResistSlashing,
            Piercing = definition.ResistPiercing,
            Blunt = definition.ResistBlunt,
            Fire = definition.ResistFire,
            Frost = definition.ResistFrost,
            Shock = definition.ResistShock,
        };
        Healths.Add(entity, health);

        Creatures.Add(entity, new Creature
        {
            SpeciesId = speciesId,
            Faction = (Faction)definition.Faction,
            Despawnable = despawnable && definition.DespawnDistance > 0.0,
        });
        Perceptions.Add(entity, new Perception { State = AwarenessState.Idle, Target = Entity.None });
        Combats.Add(entity, new Combat
        {
            Damage = definition.AttackDamage,
            DamageType = (DamageType)definition.AttackDamageType,
            Reach = (float)definition.AttackReach,
            AttackInterval = (float)definition.AttackInterval,
            Knockback = (float)definition.Knockback,
        });

        _events.Add(new SimEvent(SimEventKind.Spawned, entity, position, speciesId));
        return entity;
    }

    public Entity DropItem(ushort itemId, int count, Vec3d position, double pickupDelay = 0.5)
    {
        Entity entity = Entities.Create();
        Transforms.Add(entity, new Transform { Position = position });
        Motions.Add(entity, new Motion { Velocity = new Vec3d(0, 1.2, 0) });
        ItemDrops.Add(entity, new ItemDrop
        {
            ItemId = itemId,
            Count = count,
            PickupDelay = (float)pickupDelay,
        });
        _events.Add(new SimEvent(SimEventKind.ItemDropped, entity, position, count));
        return entity;
    }

    public Inventory InventoryOf(Entity entity)
    {
        if (_inventories.TryGetValue(entity.Index, out Inventory? inventory)) return inventory;
        inventory = new Inventory();
        _inventories[entity.Index] = inventory;
        return inventory;
    }

    // ── Damage ───────────────────────────────────────────────────────────────────

    /// <summary>
    /// Applies damage, respecting resistance and the invulnerability window. Returns the
    /// amount actually dealt, which is zero when the target is still flashing from the
    /// last hit.
    /// </summary>
    public float ApplyDamage(Entity target, float amount, DamageType type, Entity source = default,
                             double invulnerableSeconds = 0.4)
    {
        if (!Entities.IsAlive(target) || !Healths.Has(target) || amount <= 0.0f) return 0.0f;

        ref Health health = ref Healths.Get(target);
        if (health.IsDead || health.InvulnerableFor > 0.0f) return 0.0f;

        float dealt = amount * health.Resistance.Multiplier(type);
        if (dealt <= 0.0f) return 0.0f;

        health.Current -= dealt;
        health.SinceDamaged = 0.0f;
        health.InvulnerableFor = (float)invulnerableSeconds;

        Vec3d position = Transforms.Has(target) ? Transforms.Get(target).Position : Vec3d.Zero;
        _events.Add(new SimEvent(SimEventKind.Damaged, target, position, dealt));

        // Being hit is how a wandering creature learns where you are, even in the dark.
        if (Entities.IsAlive(source) && Perceptions.Has(target))
        {
            ref Perception perception = ref Perceptions.Get(target);
            perception.Target = source;
            perception.Suspicion = 1.0f;
            perception.State = AwarenessState.Combat;
            perception.StateTimer = 12.0f;
            if (Transforms.Has(source)) perception.LastKnownPosition = Transforms.Get(source).Position;
        }

        if (health.Current <= 0.0f) Kill(target);
        return dealt;
    }

    /// <summary>Kills outright, dropping loot. A player is not destroyed but enters a death state.</summary>
    public void Kill(Entity entity)
    {
        if (!Entities.IsAlive(entity)) return;

        Vec3d position = Transforms.Has(entity) ? Transforms.Get(entity).Position : Vec3d.Zero;

        if (Healths.Has(entity))
        {
            ref Health health = ref Healths.Get(entity);
            health.Current = 0.0f;
        }

        _events.Add(new SimEvent(SimEventKind.Died, entity, position, 0));

        if (PlayerTags.Has(entity))
        {
            ref PlayerTag player = ref PlayerTags.Get(entity);
            if (player.IsDead) return;
            player.IsDead = true;
            player.Deaths++;
            player.RespawnCountdown = TickRate * 3;

            // Everything carried falls where you fell, which is what gives a death stakes.
            foreach (ItemStack stack in InventoryOf(entity).TakeAll())
            {
                if (!stack.IsEmpty) DropItem(stack.ItemId, stack.Count, position + new Vec3d(0, 0.5, 0), 2.0);
            }
            return;
        }

        if (Creatures.Has(entity))
        {
            CreatureDefinition definition = CreatureCatalog.Get(Creatures.Get(entity).SpeciesId);
            foreach ((ushort itemId, int min, int max) in definition.Drops)
            {
                int count = NextInt(min, max);
                if (count > 0) DropItem(itemId, count, position + new Vec3d(0, 0.4, 0));
            }
        }

        Entities.Destroy(entity);
    }

    // ── The tick ─────────────────────────────────────────────────────────────────

    /// <summary>Advances the world by exactly one tick.</summary>
    public void Tick()
    {
        _events.Clear();
        TickCount++;

        Clock.Advance(TickSeconds);
        Light.SkyBrightness = Clock.SkyBrightness;

        UpdateAi(TickSeconds);
        UpdateMovement(TickSeconds);
        UpdateHealth(TickSeconds);
        UpdateSunlightBurning(TickSeconds);
        UpdateHunger(TickSeconds);
        UpdateItemDrops(TickSeconds);
        UpdateLifetimes(TickSeconds);
        UpdatePlayerDeath();
        UpdateSpawning();
        UpdateDespawning();

        Entities.FlushDestroyed();
    }

    /// <summary>Advances by real elapsed time, running whole ticks and keeping the remainder.</summary>
    public int Advance(double seconds)
    {
        _accumulator += seconds;
        int ticks = 0;
        // Cap the catch-up so a long stall cannot lock the simulation in a death spiral.
        while (_accumulator >= TickSeconds && ticks < 10)
        {
            _accumulator -= TickSeconds;
            Tick();
            ticks++;
        }
        if (_accumulator > TickSeconds * 10) _accumulator = 0.0;
        return ticks;
    }

    private double _accumulator;

    // ── Core systems ─────────────────────────────────────────────────────────────

    private void UpdateHealth(double dt)
    {
        Span<Health> healths = Healths.Data;
        for (int i = 0; i < healths.Length; i++)
        {
            ref Health health = ref healths[i];
            if (health.InvulnerableFor > 0.0f) health.InvulnerableFor -= (float)dt;
            if (health.IsDead) continue;

            health.SinceDamaged += (float)dt;
            if (health.RegenPerSecond > 0.0f && health.SinceDamaged >= health.RegenDelay)
            {
                health.Current = Math.Min(health.Max, health.Current + health.RegenPerSecond * (float)dt);
            }
        }
    }

    /// <summary>Undead burn once the sun is up, which is what empties the surface at dawn and
    /// makes daylight genuinely safe rather than merely convenient.</summary>
    private void UpdateSunlightBurning(double dt)
    {
        if (!Clock.IsDaylight) return;

        ReadOnlySpan<int> owners = Creatures.Owners;
        for (int i = owners.Length - 1; i >= 0; i--)
        {
            Entity entity = EntityAt(owners[i]);
            if (!Entities.IsAlive(entity)) continue;

            CreatureDefinition definition = CreatureCatalog.Get(Creatures.Data[i].SpeciesId);
            if (!definition.BurnsInSunlight) continue;

            Vec3d position = Transforms.Get(entity).Position;
            if (Light.SkyLight(position + new Vec3d(0, definition.EyeHeight, 0)) < LightField.MaxLevel) continue;

            ApplyDamage(entity, (float)(3.0 * dt), DamageType.Fire, Entity.None, invulnerableSeconds: 0.0);
        }
    }

    private void UpdateHunger(double dt)
    {
        ReadOnlySpan<int> owners = Hungers.Owners;
        for (int i = 0; i < owners.Length; i++)
        {
            ref Hunger hunger = ref Hungers.Data[i];
            Entity entity = EntityAt(owners[i]);
            if (!Entities.IsAlive(entity)) continue;

            // Moving costs energy; standing still costs almost none.
            double speed = Motions.Has(entity) ? Motions.Get(entity).Velocity.Horizontal.Length : 0.0;
            hunger.Exhaustion += (float)((0.006 + speed * 0.011) * dt);

            while (hunger.Exhaustion >= 4.0f)
            {
                hunger.Exhaustion -= 4.0f;
                if (hunger.Saturation > 0.0f) hunger.Saturation = Math.Max(0.0f, hunger.Saturation - 1.0f);
                else hunger.Food = Math.Max(0.0f, hunger.Food - 1.0f);
            }

            if (!Healths.Has(entity)) continue;
            ref Health health = ref Healths.Get(entity);

            // A full belly heals; an empty one kills, slowly.
            if (hunger.Food >= 18.0f && health.Current < health.Max)
            {
                health.Current = Math.Min(health.Max, health.Current + (float)(0.6 * dt));
            }
            else if (hunger.Food <= 0.0f)
            {
                ApplyDamage(entity, (float)(0.5 * dt), DamageType.True, Entity.None, invulnerableSeconds: 0.0);
            }
        }
    }

    /// <summary>Feeds a character, following Minecraft's saturation rule: saturation never
    /// exceeds the food bar, so a big meal on a full stomach is partly wasted.</summary>
    public bool Eat(Entity entity, ushort itemId)
    {
        if (!Hungers.Has(entity)) return false;
        ItemDefinition definition = ItemCatalog.Get(itemId);
        if (definition.Kind != ItemKind.Food) return false;

        Inventory inventory = InventoryOf(entity);
        if (inventory.Remove(itemId, 1) == 0) return false;

        ref Hunger hunger = ref Hungers.Get(entity);
        hunger.Food = Math.Min(20.0f, hunger.Food + definition.Nourishment);
        hunger.Saturation = Math.Min(hunger.Food, hunger.Saturation + definition.Saturation);

        if (definition.IllnessChance > 0.0f && NextDouble() < definition.IllnessChance)
        {
            hunger.Exhaustion += 6.0f;
        }
        return true;
    }

    private void UpdateItemDrops(double dt)
    {
        ReadOnlySpan<int> owners = ItemDrops.Owners;
        for (int i = owners.Length - 1; i >= 0; i--)
        {
            Entity entity = EntityAt(owners[i]);
            if (!Entities.IsAlive(entity)) continue;

            ref ItemDrop drop = ref ItemDrops.Data[i];
            drop.Age += (float)dt;
            if (drop.PickupDelay > 0.0f) drop.PickupDelay -= (float)dt;

            // Drops expire so the ground does not silt up with everything ever dropped.
            if (drop.Age > 300.0f)
            {
                Entities.Destroy(entity);
                continue;
            }

            if (drop.PickupDelay > 0.0f) continue;

            Vec3d position = Transforms.Get(entity).Position;
            foreach (Entity player in _players)
            {
                if (!Entities.IsAlive(player) || PlayerTags.Get(player).IsDead) continue;
                if ((Transforms.Get(player).Position - position).LengthSquared > 2.25) continue;

                int leftover = InventoryOf(player).Add(drop.ItemId, drop.Count);
                if (leftover == drop.Count) continue;   // could not carry any of it

                _events.Add(new SimEvent(SimEventKind.ItemPickedUp, player, position, drop.Count - leftover));
                if (leftover == 0) Entities.Destroy(entity);
                else drop.Count = leftover;
                break;
            }
        }
    }

    private void UpdateLifetimes(double dt)
    {
        ReadOnlySpan<int> owners = Lifetimes.Owners;
        for (int i = owners.Length - 1; i >= 0; i--)
        {
            ref Lifetime lifetime = ref Lifetimes.Data[i];
            lifetime.SecondsRemaining -= (float)dt;
            if (lifetime.SecondsRemaining <= 0.0f) Entities.Destroy(EntityAt(owners[i]));
        }
    }

    private void UpdatePlayerDeath()
    {
        ReadOnlySpan<int> owners = PlayerTags.Owners;
        for (int i = 0; i < owners.Length; i++)
        {
            ref PlayerTag player = ref PlayerTags.Data[i];
            if (!player.IsDead) continue;
            if (--player.RespawnCountdown > 0) continue;

            Entity entity = EntityAt(owners[i]);
            Respawn(entity);
        }
    }

    /// <summary>Returns a dead player to their spawn point at full health.</summary>
    public void Respawn(Entity entity)
    {
        if (!PlayerTags.Has(entity)) return;

        ref PlayerTag player = ref PlayerTags.Get(entity);
        ref Health health = ref Healths.Get(entity);
        ref Transform transform = ref Transforms.Get(entity);

        // Drop the player onto solid ground at their spawn point rather than inside it.
        Vec3d spawn = player.SpawnPoint;
        double surface = Light.SurfaceHeight(spawn.X, spawn.Z);
        transform.Position = new Vec3d(spawn.X, Math.Max(spawn.Y, surface + 0.2), spawn.Z);

        health.Current = health.Max;
        health.InvulnerableFor = 2.0f;
        Motions.Get(entity).Velocity = Vec3d.Zero;
        Hungers.Get(entity).Food = Math.Max(Hungers.Get(entity).Food, 10.0f);

        player.IsDead = false;
        player.RespawnCountdown = 0;

        _events.Add(new SimEvent(SimEventKind.PlayerRespawned, entity, transform.Position, 0));
    }

    /// <summary>The live entity handle for a raw component-owner index.</summary>
    internal Entity EntityAt(int index) => Entities.HandleOf(index);
}
