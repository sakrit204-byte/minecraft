namespace SakritCraft.Physics;

/// <summary>
/// Tuning for the player capsule. Every number here is felt directly, so they live in
/// one place and are expected to be adjusted many times. Defaults follow §10 of the
/// design document.
/// </summary>
public sealed class CharacterConfig
{
    // ── Shape ────────────────────────────────────────────────────────────────────
    /// <summary>Standing height in metres, eyes slightly below the top.</summary>
    public double Height { get; set; } = 1.8;

    /// <summary>Capsule radius. Narrow enough to fit a one-metre tunnel.</summary>
    public double Radius { get; set; } = 0.3;

    /// <summary>Height while crouched.</summary>
    public double CrouchHeight { get; set; } = 1.1;

    /// <summary>Eye height above the feet, standing.</summary>
    public double EyeHeight { get; set; } = 1.65;

    // ── Speeds, metres per second ────────────────────────────────────────────────
    public double WalkSpeed { get; set; } = 3.2;
    public double SprintSpeed { get; set; } = 6.0;
    public double SneakSpeed { get; set; } = 1.5;
    public double SwimSpeed { get; set; } = 1.8;

    /// <summary>Multiplier applied when over the carry limit. See §13.</summary>
    public double EncumberedMultiplier { get; set; } = 0.55;

    // ── Acceleration ─────────────────────────────────────────────────────────────
    /// <summary>Ground acceleration. High enough to feel responsive, low enough to have weight.</summary>
    public double GroundAcceleration { get; set; } = 38.0;

    /// <summary>Air acceleration. Deliberately weak: you commit to a jump.</summary>
    public double AirAcceleration { get; set; } = 6.0;

    /// <summary>Deceleration when no input is given and the character is grounded.</summary>
    public double GroundFriction { get; set; } = 32.0;

    // ── Gravity and jumping ──────────────────────────────────────────────────────
    /// <summary>Stronger than Earth, which is near universal in first-person games
    /// because true 9.81 makes jumps feel floaty at game scale.</summary>
    public double Gravity { get; set; } = 22.0;

    /// <summary>Terminal velocity, so a long fall does not reach absurd speed.</summary>
    public double MaxFallSpeed { get; set; } = 78.0;

    /// <summary>Initial upward speed on jump. Gives roughly a 1.15 m apex.</summary>
    public double JumpSpeed { get; set; } = 7.1;

    /// <summary>
    /// How long after walking off an edge a jump still works. Without this window,
    /// jumping from a ledge demands frame-accurate timing and feels broken rather
    /// than demanding.
    /// </summary>
    public double CoyoteTime { get; set; } = 0.12;

    /// <summary>How long before landing a jump press is remembered and then applied.</summary>
    public double JumpBufferTime { get; set; } = 0.10;

    // ── Slopes, which matter more here than in a block game ──────────────────────
    /// <summary>Steepest slope that can be walked, in degrees.</summary>
    public double WalkableSlopeDegrees { get; set; } = 35.0;

    /// <summary>Above this, the surface cannot be held at all and must be climbed.</summary>
    public double SlideLimitDegrees { get; set; } = 55.0;

    /// <summary>Acceleration down a slope too steep to stand on.</summary>
    public double SlideAcceleration { get; set; } = 12.0;

    // ── Steps and ledges ─────────────────────────────────────────────────────────
    /// <summary>Tallest obstacle walked over without jumping. Covers rubble and stairs.</summary>
    public double StepHeight { get; set; } = 0.45;

    /// <summary>Lowest ledge that can be mantled.</summary>
    public double MantleMinHeight { get; set; } = 0.5;

    /// <summary>Highest ledge that can be mantled.</summary>
    public double MantleMaxHeight { get; set; } = 1.4;

    /// <summary>How far ahead a ledge is searched for.</summary>
    public double MantleReach { get; set; } = 0.75;

    // ── Stamina ──────────────────────────────────────────────────────────────────
    public double MaxStamina { get; set; } = 100.0;
    public double SprintStaminaDrain { get; set; } = 12.0;
    public double JumpStaminaCost { get; set; } = 8.0;
    public double SwimStaminaDrain { get; set; } = 4.0;
    public double StaminaRegen { get; set; } = 18.0;

    /// <summary>Delay after spending stamina before it starts coming back.</summary>
    public double StaminaRegenDelay { get; set; } = 1.1;

    /// <summary>Sprinting requires at least this fraction of the bar.</summary>
    public double SprintStaminaFloor { get; set; } = 0.10;

    // ── Damage ───────────────────────────────────────────────────────────────────
    /// <summary>Impact speed below which a landing is free.</summary>
    public double SafeLandingSpeed { get; set; } = 13.0;

    /// <summary>Cosine of the walkable slope, precomputed per query.</summary>
    public double WalkableSlopeCosine => System.Math.Cos(WalkableSlopeDegrees * System.Math.PI / 180.0);

    /// <summary>Cosine of the slide limit.</summary>
    public double SlideLimitCosine => System.Math.Cos(SlideLimitDegrees * System.Math.PI / 180.0);
}

/// <summary>What the player is asking the character to do this tick.</summary>
public struct CharacterInput
{
    /// <summary>Desired horizontal direction in world space. Magnitude above one is clamped.</summary>
    public double MoveX;
    public double MoveZ;

    public bool Jump;
    public bool Sprint;
    public bool Crouch;

    public static CharacterInput None => default;
}

/// <summary>What the character is currently doing. Drives animation, audio and stamina.</summary>
public enum MovementState
{
    Idle,
    Walking,
    Sprinting,
    Sneaking,
    Airborne,
    Sliding,
    Swimming,
    Mantling,
}
