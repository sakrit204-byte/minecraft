using SakritCraft.Core.Math;

namespace SakritCraft.Physics;

/// <summary>
/// The player capsule, swept against the density field.
/// <para>
/// Collision resolves by depenetration along the field gradient, then by sliding the
/// remaining motion along the contact plane. The capsule is approximated by a short
/// column of spheres, because a sphere against a distance field is a single sample and
/// a true capsule query is not.
/// </para>
/// <para>
/// This is the most-felt system in the game: a player spends far longer walking than
/// crafting. Everything here is expected to be tuned repeatedly, which is why every
/// constant lives in <see cref="CharacterConfig"/>.
/// </para>
/// </summary>
public sealed class CharacterController
{
    /// <summary>
    /// Spheres approximating the capsule. Three is enough for a 1.8 m body at 0.3 m
    /// radius: consecutive spheres overlap, so nothing thin can slip between them.
    /// </summary>
    private const int SphereCount = 3;

    private readonly IVolume _volume;
    private readonly CharacterConfig _config;

    private Vec3d _wishDirection;
    private double _wishSpeed;
    private double _timeSinceGrounded;
    private double _timeSinceJumpPressed = double.MaxValue;
    private double _timeSinceStaminaSpent;

    /// <summary>Feet position. The capsule rises from here.</summary>
    public Vec3d Position { get; set; }

    public Vec3d Velocity { get; private set; }

    public bool IsGrounded { get; private set; }

    /// <summary>Surface normal under the feet. Meaningless when airborne.</summary>
    public Vec3d GroundNormal { get; private set; } = Vec3d.UnitY;

    public MovementState State { get; private set; } = MovementState.Idle;

    public double Stamina { get; private set; }

    public bool IsCrouching { get; private set; }

    /// <summary>Set true when carrying more than the strength limit allows. See §13.</summary>
    public bool IsEncumbered { get; set; }

    /// <summary>
    /// Impact speed of the most recent landing, in metres per second, or zero. Read and
    /// cleared by whatever applies fall damage.
    /// </summary>
    public double LandingImpact { get; private set; }

    /// <summary>Eye position, for the camera and for line-of-sight checks.</summary>
    public Vec3d EyePosition => Position + Vec3d.UnitY * (IsCrouching
        ? _config.EyeHeight * (_config.CrouchHeight / _config.Height)
        : _config.EyeHeight);

    public double CurrentHeight => IsCrouching ? _config.CrouchHeight : _config.Height;

    /// <summary>
    /// Why the last tick did what it did. Surfaced by the <c>/debug</c> overlay, and the
    /// only practical way to tell a step-up that was never attempted from one that was
    /// attempted and rejected.
    /// </summary>
    public StepDiagnostics LastStep { get; private set; }

    public CharacterController(IVolume volume, CharacterConfig config, Vec3d startPosition)
    {
        _volume = volume;
        _config = config;
        Position = startPosition;
        Stamina = config.MaxStamina;
    }

    /// <summary>Advances one fixed tick. Call at the simulation rate, not the frame rate.</summary>
    public void Update(double dt, in CharacterInput input)
    {
        if (dt <= 0.0) return;

        LandingImpact = 0.0;
        _timeSinceJumpPressed = input.Jump ? 0.0 : _timeSinceJumpPressed + dt;

        UpdateCrouch(input);
        ProbeGround();

        _timeSinceGrounded = IsGrounded ? 0.0 : _timeSinceGrounded + dt;

        ApplyMovement(dt, input);
        ApplyGravity(dt);
        TryJump();
        Integrate(dt);
        UpdateStamina(dt, input);
        UpdateState(input);
    }

    // ── Movement ─────────────────────────────────────────────────────────────────

    private void ApplyMovement(double dt, in CharacterInput input)
    {
        var wish = new Vec3d(input.MoveX, 0.0, input.MoveZ);
        double wishLength = wish.Length;
        if (wishLength > 1.0) { wish /= wishLength; wishLength = 1.0; }

        double target = TargetSpeed(input, wishLength);

        // Remembered for the step-up attempt. It has to come from the input rather than
        // from the current velocity: at a ledge the velocity has already been cancelled
        // by the contact, so keying the step off it means the character stops dead and
        // never tries to climb.
        _wishDirection = wishLength > 1e-6 ? wish : Vec3d.Zero;
        _wishSpeed = target;

        // On ground too steep to stand on, the player has no purchase and slides.
        if (IsGrounded && !IsWalkable(GroundNormal))
        {
            Vec3d downhill = new Vec3d(GroundNormal.X, 0.0, GroundNormal.Z).Normalised;
            Velocity += downhill * (_config.SlideAcceleration * dt);
            return;
        }

        double acceleration = IsGrounded ? _config.GroundAcceleration : _config.AirAcceleration;

        if (IsGrounded)
        {
            // Move within the plane of the ground rather than in the horizontal plane.
            // Walking up a slope then genuinely follows the surface, and there is no
            // constant downward term to be turned into a sideways shove by a tilted
            // contact normal.
            Vec3d planar = Velocity.ProjectOntoPlane(GroundNormal);

            if (wishLength > 1e-6)
            {
                Vec3d slopeDirection = wish.ProjectOntoPlane(GroundNormal);
                if (slopeDirection.LengthSquared > 1e-12)
                {
                    Vec3d desired = slopeDirection.Normalised * target;
                    Vec3d delta = desired - planar;
                    double deltaLength = delta.Length;
                    double step = acceleration * dt;
                    planar += deltaLength <= step ? delta : delta.Normalised * step;
                }
            }
            else
            {
                double speed = planar.Length;
                double drop = _config.GroundFriction * dt;
                planar = speed <= drop ? Vec3d.Zero : planar.Normalised * (speed - drop);
            }

            Velocity = planar;
            return;
        }

        Vec3d horizontal = Velocity.Horizontal;
        if (wishLength > 1e-6)
        {
            Vec3d desired = wish * target;
            Vec3d delta = desired - horizontal;
            double deltaLength = delta.Length;
            double step = acceleration * dt;
            horizontal += deltaLength <= step ? delta : delta.Normalised * step;
        }

        Velocity = new Vec3d(horizontal.X, Velocity.Y, horizontal.Z);
    }

    private double TargetSpeed(in CharacterInput input, double wishLength)
    {
        double speed;
        if (IsCrouching) speed = _config.SneakSpeed;
        else if (input.Sprint && CanSprint()) speed = _config.SprintSpeed;
        else speed = _config.WalkSpeed;

        if (IsEncumbered) speed *= _config.EncumberedMultiplier;

        // Walking up a slope costs speed, proportional to how steep it is. Free on flat
        // ground and roughly a third slower at the walkable limit.
        if (IsGrounded && GroundNormal.Y < 1.0)
        {
            double steepness = 1.0 - GroundNormal.Y;
            speed *= 1.0 - System.Math.Clamp(steepness * 1.6, 0.0, 0.35);
        }

        return speed * System.Math.Min(wishLength, 1.0);
    }

    private bool CanSprint()
        => !IsCrouching && Stamina > _config.MaxStamina * _config.SprintStaminaFloor;

    private void ApplyGravity(double dt)
    {
        if (IsGrounded)
        {
            // Keep the capsule pinned to the ground over convex edges, pushing along the
            // surface normal rather than straight down. A constant world-down term gets
            // re-expressed as sideways motion by the contact projection on any tilted
            // surface, which reads to the player as being shoved away from ledges.
            Velocity -= GroundNormal * 1.5;
            return;
        }

        double vy = Velocity.Y - _config.Gravity * dt;
        if (vy < -_config.MaxFallSpeed) vy = -_config.MaxFallSpeed;
        Velocity = new Vec3d(Velocity.X, vy, Velocity.Z);
    }

    private void TryJump()
    {
        bool wantsJump = _timeSinceJumpPressed <= _config.JumpBufferTime;
        bool canJump = _timeSinceGrounded <= _config.CoyoteTime;

        if (!wantsJump || !canJump) return;
        if (Stamina < _config.JumpStaminaCost) return;
        if (IsGrounded && !IsWalkable(GroundNormal)) return;

        Velocity = new Vec3d(Velocity.X, _config.JumpSpeed, Velocity.Z);
        SpendStamina(_config.JumpStaminaCost);

        IsGrounded = false;
        _timeSinceGrounded = _config.CoyoteTime + 1.0;
        _timeSinceJumpPressed = double.MaxValue;
    }

    // ── Integration and collision ────────────────────────────────────────────────

    private void Integrate(double dt)
    {
        Vec3d motion = Velocity * dt;

        // Step up: attempt the horizontal move raised by the step height, and keep the
        // raised result only if it clears an obstacle the low move could not. This is
        // what lets the player walk up rubble and stairs without jumping.
        if (IsGrounded && _wishDirection.LengthSquared > 1e-10 && _wishSpeed > 1e-6)
        {
            Vec3d intended = _wishDirection * (_wishSpeed * dt);
            Vec3d lowResult = MoveAndSlide(Position, motion);
            double lowProgress = (lowResult - Position).Horizontal.Length;
            double wanted = intended.Length;

            LastStep = new StepDiagnostics
            {
                LowProgress = lowProgress,
                Wanted = wanted,
                Blocked = lowProgress < wanted * 0.7,
            };

            if (lowProgress < wanted * 0.7)
            {
                Vec3d raised = Position + Vec3d.UnitY * _config.StepHeight;
                LastStep = LastStep with { RaisedClear = !CapsuleOverlaps(raised) };
                if (!CapsuleOverlaps(raised))
                {
                    Vec3d highResult = MoveAndSlide(raised, intended);
                    double highProgress = (highResult - raised).Horizontal.Length;
                    LastStep = LastStep with { HighProgress = highProgress };
                    if (highProgress > lowProgress + 0.01)
                    {
                        // Decide and place from two different positions.
                        //
                        // Walkability is judged from a probe pushed a full capsule radius
                        // ahead, because a single tick of movement is only a few
                        // centimetres and lands the capsule on the rounded lip of the
                        // step, where the surface normal is the edge rather than the top.
                        // Judging there rejects every real step: a 0.35 m stair read as a
                        // 62 degree slope. Placement still uses the intended distance, so
                        // the character never moves faster than its own speed.
                        double probeDistance = System.Math.Max(intended.Length, _config.Radius * 1.2);
                        Vec3d probed = MoveAndSlide(raised, _wishDirection * probeDistance);
                        SnapDown(probed, _config.StepHeight + 0.1, out bool landed, out Vec3d landingNormal);

                        Vec3d settled = SnapDown(highResult, _config.StepHeight + 0.1, out _, out _);
                        LastStep = LastStep with
                        {
                            Landed = landed,
                            LandingNormalY = landingNormal.Y,
                            LandingWalkable = IsWalkable(landingNormal),
                            SettledClear = !CapsuleOverlaps(settled),
                        };

                        // Accept only if the surface stepped onto can be stood on.
                        // Without this the step-up ratchets the character up a vertical
                        // wall: each tick it lifts by the step height, finds the wall
                        // still ahead, and climbs again, turning a cliff into a staircase.
                        if (landed && IsWalkable(landingNormal) && !CapsuleOverlaps(settled))
                        {
                            Position = settled;
                            Velocity = new Vec3d(Velocity.X, System.Math.Max(Velocity.Y, -2.0), Velocity.Z);
                            LastStep = LastStep with { Accepted = true };
                            return;
                        }
                    }
                }
            }

            Position = lowResult;
            ProjectVelocityOntoContacts();
            return;
        }

        Position = MoveAndSlide(Position, motion);
        ProjectVelocityOntoContacts();
    }

    /// <summary>
    /// Moves from a position by a motion vector, resolving contacts and sliding along
    /// them. Runs several passes so that a corner, where two surfaces both constrain
    /// the move, resolves rather than jittering between them.
    /// </summary>
    private Vec3d MoveAndSlide(Vec3d from, Vec3d motion)
    {
        Vec3d position = from + motion;

        for (int pass = 0; pass < 4; pass++)
        {
            if (!ResolveCapsule(ref position, out _)) break;
        }

        return position;
    }

    /// <summary>
    /// Pushes the capsule out of anything it overlaps. Returns whether it was touching.
    /// </summary>
    private bool ResolveCapsule(ref Vec3d position, out Vec3d normal)
    {
        Vec3d accumulated = Vec3d.Zero;
        bool touched = false;

        for (int i = 0; i < SphereCount; i++)
        {
            Vec3d centre = SphereCentre(position, i);
            double distance = _volume.Sample(centre);
            double overlap = _config.Radius - distance;
            if (overlap <= 0.0) continue;

            Vec3d n = _volume.Normal(centre);
            position += n * overlap;
            accumulated += n;
            touched = true;
        }

        normal = accumulated.LengthSquared > 1e-12 ? accumulated.Normalised : Vec3d.UnitY;
        return touched;
    }

    private bool CapsuleOverlaps(Vec3d position)
    {
        for (int i = 0; i < SphereCount; i++)
        {
            if (_volume.Sample(SphereCentre(position, i)) < _config.Radius) return true;
        }
        return false;
    }

    /// <summary>Centre of the i-th approximating sphere, given a feet position.</summary>
    private Vec3d SphereCentre(Vec3d feet, int index)
    {
        double low = _config.Radius;
        double high = CurrentHeight - _config.Radius;
        double t = SphereCount == 1 ? 0.0 : index / (double)(SphereCount - 1);
        return feet + Vec3d.UnitY * (low + (high - low) * t);
    }

    /// <summary>
    /// Removes velocity pointing into any surface currently touched, and records the
    /// impact speed before doing so.
    /// <para>
    /// The impact has to be captured here rather than by comparing grounded state
    /// between ticks. At terminal velocity the capsule crosses more than a metre per
    /// tick, so it collides and has its downward velocity cancelled within a single
    /// update; by the next tick the speed that would have caused the damage is already
    /// gone and the fall reads as harmless.
    /// </para>
    /// </summary>
    private void ProjectVelocityOntoContacts()
    {
        for (int i = 0; i < SphereCount; i++)
        {
            Vec3d centre = SphereCentre(Position, i);
            double distance = _volume.Sample(centre);
            if (distance > _config.Radius + 0.02) continue;

            Vec3d normal = _volume.Normal(centre);

            double closingSpeed = -Vec3d.Dot(Velocity, normal);
            if (normal.Y > 0.4 && closingSpeed > _config.SafeLandingSpeed)
            {
                LandingImpact = System.Math.Max(LandingImpact, closingSpeed);
            }

            Velocity = Velocity.SlideAlong(normal);
        }
    }

    /// <summary>
    /// Drops a position onto whatever is below it, reporting what was landed on.
    /// The caller needs the normal: a step is only a step if the thing stepped onto can
    /// actually be stood on.
    /// </summary>
    private Vec3d SnapDown(Vec3d position, double maxDrop, out bool landed, out Vec3d landingNormal)
    {
        Vec3d start = SphereCentre(position, 0);
        TraceHit hit = SphereTrace.Sphere(_volume, start, -Vec3d.UnitY, maxDrop, _config.Radius);
        landed = hit.Hit;
        landingNormal = hit.Normal;
        return hit.Hit ? position - Vec3d.UnitY * hit.Distance : position;
    }

    // ── Ground detection ─────────────────────────────────────────────────────────

    private void ProbeGround()
    {
        // Probe from the lowest sphere straight down. A short probe keeps the character
        // attached over small undulations without gluing it to the ground after a jump.
        Vec3d start = SphereCentre(Position, 0);
        const double probe = 0.18;

        TraceHit hit = SphereTrace.Sphere(_volume, start, -Vec3d.UnitY, probe, _config.Radius);

        // Separating from the surface means airborne. This must be measured along the
        // surface normal, not along world up: walking up a 20 degree ramp at walking
        // pace carries over a metre per second of upward world velocity, and testing
        // Y alone declares the character airborne on every slope they climb.
        bool separating = hit.Hit && Vec3d.Dot(Velocity, hit.Normal) > 0.5;

        if (!hit.Hit || separating)
        {
            IsGrounded = false;
            GroundNormal = Vec3d.UnitY;
            return;
        }

        IsGrounded = true;
        GroundNormal = hit.Normal;
    }

    private bool IsWalkable(Vec3d normal) => normal.Y >= _config.WalkableSlopeCosine;

    // ── Crouch ───────────────────────────────────────────────────────────────────

    private void UpdateCrouch(in CharacterInput input)
    {
        if (input.Crouch)
        {
            IsCrouching = true;
            return;
        }

        if (!IsCrouching) return;

        // Only stand back up if there is headroom, otherwise the capsule would be
        // forced into the ceiling and shoved through it by depenetration.
        bool wasCrouching = IsCrouching;
        IsCrouching = false;
        if (CapsuleOverlaps(Position)) IsCrouching = wasCrouching;
    }

    // ── Stamina ──────────────────────────────────────────────────────────────────

    private void UpdateStamina(double dt, in CharacterInput input)
    {
        bool moving = Velocity.Horizontal.LengthSquared > 0.25;

        if (input.Sprint && moving && IsGrounded && !IsCrouching && CanSprint())
        {
            SpendStamina(_config.SprintStaminaDrain * dt);
        }
        else
        {
            _timeSinceStaminaSpent += dt;
            if (_timeSinceStaminaSpent >= _config.StaminaRegenDelay)
            {
                double regen = _config.StaminaRegen * dt;
                if (IsEncumbered) regen *= 0.4;
                Stamina = System.Math.Min(_config.MaxStamina, Stamina + regen);
            }
        }
    }

    private void SpendStamina(double amount)
    {
        Stamina = System.Math.Max(0.0, Stamina - amount);
        _timeSinceStaminaSpent = 0.0;
    }

    // ── State ────────────────────────────────────────────────────────────────────

    private void UpdateState(in CharacterInput input)
    {
        if (!IsGrounded)
        {
            State = MovementState.Airborne;
            return;
        }

        if (!IsWalkable(GroundNormal))
        {
            State = MovementState.Sliding;
            return;
        }

        bool moving = Velocity.Horizontal.LengthSquared > 0.09;
        if (!moving) { State = MovementState.Idle; return; }

        if (IsCrouching) State = MovementState.Sneaking;
        else if (input.Sprint && CanSprint()) State = MovementState.Sprinting;
        else State = MovementState.Walking;
    }

    /// <summary>
    /// Drops the character onto the ground below, used when spawning or teleporting.
    /// Returns false if no ground was found within the search distance.
    /// </summary>
    public bool SettleOnGround(double searchDistance = 400.0)
    {
        Vec3d start = SphereCentre(Position, 0);
        TraceHit hit = SphereTrace.Sphere(_volume, start, -Vec3d.UnitY, searchDistance, _config.Radius);
        if (!hit.Hit) return false;

        Position -= Vec3d.UnitY * hit.Distance;

        // Nudge clear of any residual overlap so the first tick does not start inside rock.
        Vec3d position = Position;
        for (int pass = 0; pass < 6 && CapsuleOverlaps(position); pass++)
        {
            ResolveCapsule(ref position, out _);
        }
        Position = position;
        Velocity = Vec3d.Zero;
        return true;
    }
}


/// <summary>Why the last step-up attempt succeeded or failed. Diagnostic only.</summary>
public readonly record struct StepDiagnostics
{
    /// <summary>How far the low move actually got, in metres.</summary>
    public double LowProgress { get; init; }

    /// <summary>How far the player asked to move, in metres.</summary>
    public double Wanted { get; init; }

    /// <summary>Whether the low move was obstructed enough to try stepping.</summary>
    public bool Blocked { get; init; }

    /// <summary>Whether the capsule fitted at the raised height.</summary>
    public bool RaisedClear { get; init; }

    /// <summary>How far the raised move got.</summary>
    public double HighProgress { get; init; }

    /// <summary>Whether anything was found to stand on after stepping up.</summary>
    public bool Landed { get; init; }

    /// <summary>Vertical component of the surface stepped onto.</summary>
    public double LandingNormalY { get; init; }

    /// <summary>Whether that surface was shallow enough to stand on.</summary>
    public bool LandingWalkable { get; init; }

    /// <summary>Whether the capsule fitted at the settled position.</summary>
    public bool SettledClear { get; init; }

    /// <summary>Whether the step was taken.</summary>
    public bool Accepted { get; init; }
}
