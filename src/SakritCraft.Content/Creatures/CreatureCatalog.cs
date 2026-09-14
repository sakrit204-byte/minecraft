namespace SakritCraft.Content.Creatures;

/// <summary>Which side a creature fights for. Mirrors SakritCraft.Sim.Faction.</summary>
public enum CreatureFaction : byte
{
    Neutral = 0,
    Player = 1,
    Hostile = 2,
    Wildlife = 3,
}

/// <summary>How a creature notices things. A blind creature that hunts by sound plays
/// completely differently from one that hunts by sight, and the difference is entirely
/// in these numbers.</summary>
public readonly record struct Senses(
    double SightRange,
    /// <summary>Half-angle of the vision cone in degrees. 180 means it sees all round.</summary>
    double SightConeDegrees,
    /// <summary>Range at which ordinary movement is heard. Sprinting and mining extend it.</summary>
    double HearingRange,
    /// <summary>Whether darkness hides the player from it. A creature that hunts by sound does
    /// not care how dark it is.</summary>
    bool NeedsLightToSee);

/// <summary>Where and when a creature is allowed to appear naturally.</summary>
public readonly record struct SpawnRule(
    /// <summary>Highest gameplay light level it will spawn in. 15 means light is no barrier.</summary>
    int MaxLight,
    double MinY,
    double MaxY,
    /// <summary>Column temperature band, in degrees Celsius.</summary>
    double MinTemperature,
    double MaxTemperature,
    /// <summary>Must the spawn point see open sky? Surface creatures do; cave dwellers must not.</summary>
    bool RequiresSky,
    bool ForbidsSky,
    /// <summary>How many appear together.</summary>
    int PackMin,
    int PackMax,
    /// <summary>Relative likelihood against other creatures eligible at the same place.</summary>
    double Weight);

/// <summary>Everything the simulation needs to know about one species.</summary>
public sealed record CreatureDefinition
{
    public required ushort Id { get; init; }
    public required string Name { get; init; }
    public required CreatureFaction Faction { get; init; }

    public required float MaxHealth { get; init; }
    public float HealthRegenPerSecond { get; init; }

    /// <summary>Metres per second when moving without urgency.</summary>
    public required double WalkSpeed { get; init; }
    /// <summary>Metres per second when pursuing or fleeing.</summary>
    public required double ChaseSpeed { get; init; }

    public float AttackDamage { get; init; }
    public DamageKind AttackDamageType { get; init; } = DamageKind.Slashing;
    /// <summary>Metres at which an attack lands. A ranged attacker has a large value here.</summary>
    public double AttackReach { get; init; } = 1.6;
    public double AttackInterval { get; init; } = 1.2;
    public double Knockback { get; init; } = 3.0;

    /// <summary>Distance it prefers to keep from its target. Archers hold at range.</summary>
    public double PreferredRange { get; init; }

    public required Senses Senses { get; init; }
    public SpawnRule? Spawn { get; init; }

    /// <summary>Height of the eyes above the feet, for line of sight.</summary>
    public double EyeHeight { get; init; } = 1.5;
    public double Radius { get; init; } = 0.35;
    public double Height { get; init; } = 1.8;

    /// <summary>Catches fire in daylight, which is what empties the surface at dawn.</summary>
    public bool BurnsInSunlight { get; init; }

    /// <summary>Destroys itself on reaching its target, carving terrain. The creeper role.</summary>
    public bool Detonates { get; init; }
    public double BlastRadius { get; init; }

    /// <summary>Runs away below this fraction of health. Zero means it never flees.</summary>
    public float FleeBelowHealthFraction { get; init; }

    /// <summary>Despawns beyond this distance from any player. Zero means it never despawns.</summary>
    public double DespawnDistance { get; init; } = 128.0;

    /// <summary>Per-damage-type multipliers, 1 being unresisted.</summary>
    public float ResistSlashing { get; init; } = 1.0f;
    public float ResistPiercing { get; init; } = 1.0f;
    public float ResistBlunt { get; init; } = 1.0f;
    public float ResistFire { get; init; } = 1.0f;
    public float ResistFrost { get; init; } = 1.0f;
    public float ResistShock { get; init; } = 1.0f;

    /// <summary>What it leaves behind. Item id and how many.</summary>
    public IReadOnlyList<(ushort ItemId, int Min, int Max)> Drops { get; init; } = Array.Empty<(ushort, int, int)>();
}

/// <summary>Mirrors SakritCraft.Sim.DamageType; duplicated so content does not depend on the simulation.</summary>
public enum DamageKind : byte
{
    Slashing = 0, Piercing = 1, Blunt = 2, Fire = 3, Frost = 4, Shock = 5, True = 6,
}

/// <summary>
/// Every species in the game.
/// <para>
/// The spawn rules here are deliberately close to Minecraft's, because they are what make
/// darkness meaningful and torches valuable. What differs is behaviour: these creatures
/// have senses that can be defeated, so a player who understands a species can avoid it
/// rather than only out-fight it.
/// </para>
/// </summary>
public static class CreatureCatalog
{
    public const ushort Husk = 1;
    public const ushort Bonewalker = 2;
    public const ushort DeepStalker = 3;
    public const ushort Gloomweaver = 4;
    public const ushort Bloater = 5;
    public const ushort Deer = 6;
    public const ushort Boar = 7;
    public const ushort Villager = 8;

    private static readonly Dictionary<ushort, CreatureDefinition> Definitions = Build();

    public static IReadOnlyCollection<CreatureDefinition> All => Definitions.Values;

    public static CreatureDefinition Get(ushort id) => Definitions.TryGetValue(id, out var d)
        ? d
        : throw new KeyNotFoundException($"No creature with id {id}.");

    public static bool TryGet(ushort id, out CreatureDefinition definition)
        => Definitions.TryGetValue(id, out definition!);

    private static Dictionary<ushort, CreatureDefinition> Build()
    {
        var list = new List<CreatureDefinition>
        {
            // ── The baseline hostile. Slow, relentless, dangerous in groups ───────────
            new()
            {
                Id = Husk,
                Name = "Husk",
                Faction = CreatureFaction.Hostile,
                MaxHealth = 20.0f,
                WalkSpeed = 1.5,
                ChaseSpeed = 2.6,
                AttackDamage = 3.5f,
                AttackDamageType = DamageKind.Blunt,
                AttackReach = 1.8,
                AttackInterval = 1.1,
                Senses = new Senses(SightRange: 24.0, SightConeDegrees: 90.0, HearingRange: 14.0, NeedsLightToSee: false),
                Spawn = new SpawnRule(MaxLight: 7, MinY: 0, MaxY: 320, MinTemperature: -30, MaxTemperature: 45,
                                      RequiresSky: false, ForbidsSky: false, PackMin: 1, PackMax: 3, Weight: 1.0),
                BurnsInSunlight = true,
                ResistBlunt = 0.75f,     // rotted and soft; blunt weapons work
                ResistPiercing = 1.25f,  // arrows pass through without much effect
                Drops = new[] { (ItemIds.RottenFlesh, 0, 2) },
            },

            // ── Archer. Keeps its distance and punishes open ground ──────────────────
            new()
            {
                Id = Bonewalker,
                Name = "Bonewalker",
                Faction = CreatureFaction.Hostile,
                MaxHealth = 16.0f,
                WalkSpeed = 1.8,
                ChaseSpeed = 3.0,
                AttackDamage = 3.0f,
                AttackDamageType = DamageKind.Piercing,
                AttackReach = 16.0,
                AttackInterval = 1.8,
                PreferredRange = 9.0,
                Knockback = 1.0,
                Senses = new Senses(26.0, 80.0, 12.0, NeedsLightToSee: false),
                Spawn = new SpawnRule(7, 0, 320, -30, 45, false, false, 1, 2, 0.7),
                BurnsInSunlight = true,
                ResistBlunt = 1.4f,      // brittle
                ResistPiercing = 0.5f,   // arrows pass between the bones
                Drops = new[] { (ItemIds.Bone, 0, 2) },
            },

            // ── Blind cave hunter. Sneak past it, or bring it down on yourself ───────
            new()
            {
                Id = DeepStalker,
                Name = "Deep Stalker",
                Faction = CreatureFaction.Hostile,
                MaxHealth = 24.0f,
                WalkSpeed = 1.2,
                ChaseSpeed = 4.2,
                AttackDamage = 5.0f,
                AttackDamageType = DamageKind.Slashing,
                AttackReach = 2.0,
                AttackInterval = 1.0,
                // Sight range of zero: light is no defence, and neither is darkness a danger.
                Senses = new Senses(SightRange: 0.0, SightConeDegrees: 180.0, HearingRange: 26.0, NeedsLightToSee: false),
                Spawn = new SpawnRule(4, -400, 40, -30, 45, RequiresSky: false, ForbidsSky: true, PackMin: 1, PackMax: 2, Weight: 0.9),
                ResistFire = 1.5f,
                Drops = new[] { (ItemIds.RottenFlesh, 0, 1) },
            },

            // ── Climbs, drops on you, webs the floor ─────────────────────────────────
            new()
            {
                Id = Gloomweaver,
                Name = "Gloomweaver",
                Faction = CreatureFaction.Hostile,
                MaxHealth = 14.0f,
                WalkSpeed = 2.2,
                ChaseSpeed = 4.6,
                AttackDamage = 2.5f,
                AttackDamageType = DamageKind.Piercing,
                AttackReach = 1.5,
                AttackInterval = 0.8,
                Senses = new Senses(18.0, 160.0, 18.0, NeedsLightToSee: false),
                Spawn = new SpawnRule(7, -400, 200, -5, 45, false, false, 1, 3, 0.8),
                Height = 0.9,
                EyeHeight = 0.7,
                Radius = 0.6,
                FleeBelowHealthFraction = 0.2f,
                Drops = new[] { (ItemIds.Silk, 0, 2) },
            },

            // ── The creeper role: quiet approach, then it takes the ground with it ───
            new()
            {
                Id = Bloater,
                Name = "Bloater",
                Faction = CreatureFaction.Hostile,
                MaxHealth = 12.0f,
                WalkSpeed = 1.6,
                ChaseSpeed = 2.9,
                AttackDamage = 0.0f,     // the detonation does the damage
                AttackReach = 2.2,
                AttackInterval = 1.5,
                Senses = new Senses(20.0, 100.0, 10.0, NeedsLightToSee: false),
                Spawn = new SpawnRule(7, -200, 320, -30, 45, false, false, 1, 1, 0.55),
                Detonates = true,
                BlastRadius = 3.2,
                ResistFire = 1.8f,
                Drops = new[] { (ItemIds.Saltpetre, 0, 1) },
            },

            // ── Wildlife: food, and something alive in the world by day ──────────────
            new()
            {
                Id = Deer,
                Name = "Deer",
                Faction = CreatureFaction.Wildlife,
                MaxHealth = 10.0f,
                WalkSpeed = 1.4,
                ChaseSpeed = 6.5,
                AttackDamage = 0.0f,
                Senses = new Senses(30.0, 160.0, 22.0, NeedsLightToSee: true),
                Spawn = new SpawnRule(MaxLight: 15, MinY: 60, MaxY: 260, MinTemperature: -8, MaxTemperature: 34,
                                      RequiresSky: true, ForbidsSky: false, PackMin: 2, PackMax: 5, Weight: 1.0),
                FleeBelowHealthFraction = 1.0f,   // always flees
                DespawnDistance = 160.0,
                Drops = new[] { (ItemIds.RawMeat, 1, 3), (ItemIds.Hide, 0, 2) },
            },

            new()
            {
                Id = Boar,
                Name = "Boar",
                Faction = CreatureFaction.Wildlife,
                MaxHealth = 14.0f,
                WalkSpeed = 1.3,
                ChaseSpeed = 4.4,
                AttackDamage = 3.0f,
                AttackDamageType = DamageKind.Piercing,
                AttackReach = 1.5,
                AttackInterval = 1.4,
                Senses = new Senses(22.0, 140.0, 20.0, NeedsLightToSee: true),
                Spawn = new SpawnRule(15, 60, 200, 0, 36, true, false, 1, 3, 0.8),
                FleeBelowHealthFraction = 0.25f,
                Height = 1.0,
                EyeHeight = 0.8,
                DespawnDistance = 160.0,
                Drops = new[] { (ItemIds.RawMeat, 1, 3), (ItemIds.Hide, 0, 1) },
            },

            // ── A person. Neutral, defends itself, lives in settlements ──────────────
            new()
            {
                Id = Villager,
                Name = "Villager",
                Faction = CreatureFaction.Neutral,
                MaxHealth = 20.0f,
                HealthRegenPerSecond = 0.4f,
                WalkSpeed = 1.6,
                ChaseSpeed = 3.4,
                AttackDamage = 2.0f,
                AttackDamageType = DamageKind.Blunt,
                Senses = new Senses(28.0, 110.0, 16.0, NeedsLightToSee: true),
                Spawn = null,            // placed by settlements, never spawned at random
                FleeBelowHealthFraction = 0.5f,
                DespawnDistance = 0.0,   // never despawns
                Drops = Array.Empty<(ushort, int, int)>(),
            },
        };

        return list.ToDictionary(d => d.Id);
    }
}
