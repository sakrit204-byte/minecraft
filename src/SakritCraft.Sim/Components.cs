using SakritCraft.Core.Math;
using SakritCraft.Sim.Ecs;

namespace SakritCraft.Sim;

/// <summary>Where something is and which way it faces.</summary>
public struct Transform
{
    /// <summary>Feet position in world metres.</summary>
    public Vec3d Position;
    /// <summary>Heading in radians. Pitch is not simulated; nothing here aims vertically.</summary>
    public float Yaw;
}

/// <summary>Linear motion, integrated against the density field by the movement system.</summary>
public struct Motion
{
    public Vec3d Velocity;
    public bool Grounded;
    /// <summary>Set for the tick in which a hard landing happened, in metres per second.</summary>
    public float LandingImpact;
}

/// <summary>Damage categories. Armour resists each differently, which is what gives weapon
/// choice a reason to exist beyond a single number.</summary>
public enum DamageType : byte
{
    Slashing = 0,
    Piercing = 1,
    Blunt = 2,
    Fire = 3,
    Frost = 4,
    Shock = 5,
    /// <summary>Falling, drowning, suffocation. Ignores armour entirely.</summary>
    True = 6,
}

/// <summary>Hit points, and the rules for losing and regaining them.</summary>
public struct Health
{
    public float Current;
    public float Max;

    /// <summary>Hit points regained per second once regeneration is allowed.</summary>
    public float RegenPerSecond;

    /// <summary>Seconds after taking damage before regeneration resumes.</summary>
    public float RegenDelay;

    /// <summary>Counts down after a hit; regeneration waits for it.</summary>
    public float SinceDamaged;

    /// <summary>
    /// Counts down after a hit and blocks further damage while positive. Without it a
    /// creature standing in a fire or overlapping two attackers takes damage every tick
    /// and dies instantly, which reads as a bug even when it is arithmetically correct.
    /// </summary>
    public float InvulnerableFor;

    /// <summary>Per-type multipliers. 1 is unresisted, 0 is immune, above 1 is a weakness.</summary>
    public DamageResistance Resistance;

    public readonly bool IsDead => Current <= 0.0f;
    public readonly float Fraction => Max > 0.0f ? Math.Clamp(Current / Max, 0.0f, 1.0f) : 0.0f;

    public static Health With(float max, float regenPerSecond = 0.0f, float regenDelay = 6.0f) => new()
    {
        Current = max,
        Max = max,
        RegenPerSecond = regenPerSecond,
        RegenDelay = regenDelay,
        Resistance = DamageResistance.Neutral,
    };
}

/// <summary>Multipliers applied to incoming damage, one per <see cref="DamageType"/>.</summary>
public struct DamageResistance
{
    public float Slashing, Piercing, Blunt, Fire, Frost, Shock;

    public static DamageResistance Neutral => new()
    {
        Slashing = 1, Piercing = 1, Blunt = 1, Fire = 1, Frost = 1, Shock = 1,
    };

    public readonly float Multiplier(DamageType type) => type switch
    {
        DamageType.Slashing => Slashing,
        DamageType.Piercing => Piercing,
        DamageType.Blunt => Blunt,
        DamageType.Fire => Fire,
        DamageType.Frost => Frost,
        DamageType.Shock => Shock,
        _ => 1.0f,   // True damage ignores resistance
    };
}

/// <summary>Food, following Minecraft's shape: saturation buffers the food bar, exhaustion
/// from activity drains saturation first and food second.</summary>
public struct Hunger
{
    public float Food;          // 0..20, as the player sees it
    public float Saturation;    // hidden buffer, never exceeds Food
    public float Exhaustion;    // accumulates; each 4.0 costs one saturation or food

    public static Hunger Full => new() { Food = 20.0f, Saturation = 5.0f, Exhaustion = 0.0f };
}

/// <summary>Marks the entity a human player controls.</summary>
public struct PlayerTag
{
    /// <summary>Where this player respawns. Set by a bed or the world spawn.</summary>
    public Vec3d SpawnPoint;
    /// <summary>Ticks until the player may act again after dying.</summary>
    public int RespawnCountdown;
    public bool IsDead;
    public int Deaths;
}

/// <summary>Marks a non-player creature and names its species.</summary>
public struct Creature
{
    /// <summary>Index into the creature catalogue.</summary>
    public ushort SpeciesId;
    /// <summary>Which side it fights for. Creatures never attack their own faction.</summary>
    public Faction Faction;
    /// <summary>True if it was spawned by the natural spawner and may be despawned again.</summary>
    public bool Despawnable;
}

public enum Faction : byte
{
    Neutral = 0,
    Player = 1,
    Hostile = 2,
    Wildlife = 3,
}

/// <summary>What a creature currently believes and intends.</summary>
public struct Perception
{
    public AwarenessState State;
    /// <summary>Who it is interested in. May be stale; always re-check liveness.</summary>
    public Entity Target;
    /// <summary>Where the target was last perceived, which is where a searching creature goes.</summary>
    public Vec3d LastKnownPosition;
    /// <summary>Seconds remaining in the current state before it decays.</summary>
    public float StateTimer;
    /// <summary>Builds while a target is detected and decays otherwise; crossing a threshold
    /// promotes the state. Without it, creatures flick between calm and alert every tick.</summary>
    public float Suspicion;
    /// <summary>Seconds until the next attack is allowed.</summary>
    public float AttackCooldown;
}

public enum AwarenessState : byte
{
    /// <summary>Nothing detected. Wanders or stands.</summary>
    Idle = 0,
    /// <summary>Something was sensed but not identified. Turns toward it.</summary>
    Suspicious = 1,
    /// <summary>Lost a confirmed target; moves to where it was last seen.</summary>
    Searching = 2,
    /// <summary>Target confirmed and engaged.</summary>
    Combat = 3,
    /// <summary>Badly hurt; runs away.</summary>
    Fleeing = 4,
}

/// <summary>An item lying on the ground, waiting to be picked up.</summary>
public struct ItemDrop
{
    public ushort ItemId;
    public int Count;
    /// <summary>Seconds before anyone may pick it up, so a thrown item does not return instantly.</summary>
    public float PickupDelay;
    /// <summary>Seconds alive. Drops expire so the ground does not fill with debris.</summary>
    public float Age;
}

/// <summary>Ticks until the entity removes itself. Used by projectiles and effects.</summary>
public struct Lifetime
{
    public float SecondsRemaining;
}

/// <summary>What an entity can do in a fight.</summary>
public struct Combat
{
    public float Damage;
    public DamageType DamageType;
    /// <summary>Metres at which an attack can land.</summary>
    public float Reach;
    /// <summary>Seconds between attacks.</summary>
    public float AttackInterval;
    /// <summary>Knockback impulse applied to the victim, in metres per second.</summary>
    public float Knockback;
}
