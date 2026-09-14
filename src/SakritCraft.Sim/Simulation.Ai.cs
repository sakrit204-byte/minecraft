using SakritCraft.Content.Creatures;
using SakritCraft.Core.Math;
using SakritCraft.Physics;
using SakritCraft.Sim.Ecs;

namespace SakritCraft.Sim;

public sealed partial class Simulation
{
    /// <summary>Gravity for creatures and dropped items. Matches the player's for consistency.</summary>
    private const double Gravity = 22.0;

    // ── Perception and decision ──────────────────────────────────────────────────

    private void UpdateAi(double dt)
    {
        ReadOnlySpan<int> owners = Creatures.Owners;
        for (int i = 0; i < owners.Length; i++)
        {
            Entity self = EntityAt(owners[i]);
            if (!Entities.IsAlive(self)) continue;

            CreatureDefinition definition = CreatureCatalog.Get(Creatures.Data[i].SpeciesId);
            Faction faction = Creatures.Data[i].Faction;

            ref Perception perception = ref Perceptions.Get(self);
            ref Transform transform = ref Transforms.Get(self);
            ref Motion motion = ref Motions.Get(self);
            ref Health health = ref Healths.Get(self);

            if (perception.AttackCooldown > 0.0f) perception.AttackCooldown -= (float)dt;
            if (perception.StateTimer > 0.0f) perception.StateTimer -= (float)dt;

            Entity target = FindTarget(self, transform.Position, faction, definition);
            bool detected = !target.IsNone;

            if (detected)
            {
                perception.Target = target;
                perception.LastKnownPosition = Transforms.Get(target).Position;
                // Suspicion builds rather than snapping, so a creature does not flicker between
                // calm and alert on the edge of its senses.
                perception.Suspicion = Math.Min(1.0f, perception.Suspicion + (float)(dt * 2.4));
            }
            else
            {
                perception.Suspicion = Math.Max(0.0f, perception.Suspicion - (float)(dt * 0.5));
            }

            // Badly hurt creatures disengage, if their species is the kind that does.
            bool wantsToFlee = definition.FleeBelowHealthFraction > 0.0f
                            && health.Fraction <= definition.FleeBelowHealthFraction
                            && !perception.Target.IsNone;

            perception.State = wantsToFlee ? AwarenessState.Fleeing
                : perception.Suspicion >= 0.85f ? AwarenessState.Combat
                : perception.Suspicion >= 0.30f ? AwarenessState.Suspicious
                : perception.State == AwarenessState.Combat || perception.State == AwarenessState.Searching
                    ? (perception.StateTimer > 0.0f ? AwarenessState.Searching : AwarenessState.Idle)
                    : AwarenessState.Idle;

            if (perception.State == AwarenessState.Combat) perception.StateTimer = 10.0f;

            Act(self, definition, ref perception, ref transform, ref motion, dt);
        }
    }

    /// <summary>
    /// The best thing this creature can currently perceive, or none.
    /// <para>
    /// Two independent senses. Sight is a range and a cone, and for most species it is
    /// defeated by darkness. Hearing is a radius that grows with how fast the target is
    /// moving, and darkness does nothing to it. That split is what makes a blind cave
    /// hunter play differently from a surface one: against the first you crouch, against
    /// the second you carry a torch, and the two strategies are opposites.
    /// </para>
    /// </summary>
    private Entity FindTarget(Entity self, Vec3d selfPosition, Faction faction, CreatureDefinition definition)
    {
        if (faction == Faction.Neutral && definition.AttackDamage <= 0.0f) return Entity.None;

        Entity best = Entity.None;
        double bestDistance = double.MaxValue;

        foreach (Entity candidate in _players)
        {
            if (!Entities.IsAlive(candidate)) continue;
            if (PlayerTags.Get(candidate).IsDead) continue;
            if (faction == Faction.Player) continue;

            Vec3d position = Transforms.Get(candidate).Position;
            double distance = (position - selfPosition).Length;
            if (distance >= bestDistance) continue;

            double speed = Motions.Has(candidate) ? Motions.Get(candidate).Velocity.Horizontal.Length : 0.0;

            if (!CanPerceive(selfPosition, position, speed, definition)) continue;

            best = candidate;
            bestDistance = distance;
        }

        return best;
    }

    private bool CanPerceive(Vec3d observer, Vec3d target, double targetSpeed, CreatureDefinition definition)
    {
        double distance = (target - observer).Length;

        // Hearing. A still target is quiet but not silent: a floor of zero would make standing
        // perfectly still an invisibility cloak, and nothing would ever find the player.
        double loudness = Math.Clamp(targetSpeed / 6.0, 0.0, 1.6);
        double hearing = definition.Senses.HearingRange * (0.55 + loudness);
        if (distance <= hearing) return true;

        // Sight.
        if (definition.Senses.SightRange <= 0.0 || distance > definition.Senses.SightRange) return false;

        if (definition.Senses.NeedsLightToSee)
        {
            // Darkness shortens sight but never abolishes it. Scaling all the way down meant a
            // night creature was blind at night, which is exactly backwards: the floor is what
            // keeps a dark cave dangerous rather than a hiding place.
            int light = Light.Effective(target + new Vec3d(0, 1.0, 0));
            double visibleRange = definition.Senses.SightRange * Math.Clamp(light / 12.0, 0.45, 1.0);
            if (distance > visibleRange) return false;
        }

        // Line of sight through the terrain, from eye to chest.
        Vec3d eye = observer + new Vec3d(0, definition.EyeHeight, 0);
        Vec3d chest = target + new Vec3d(0, 1.1, 0);
        Vec3d toTarget = chest - eye;
        TraceHit hit = SphereTrace.Ray(_volume, eye, toTarget, toTarget.Length);
        return !hit.Hit;
    }

    // ── Acting on the decision ───────────────────────────────────────────────────

    private void Act(Entity self, CreatureDefinition definition, ref Perception perception,
                     ref Transform transform, ref Motion motion, double dt)
    {
        Vec3d desired = Vec3d.Zero;
        double speed = definition.WalkSpeed;

        switch (perception.State)
        {
            case AwarenessState.Idle:
                // Wander: change heading occasionally rather than every tick, so the path is a
                // walk rather than a jitter.
                if (NextDouble() < dt * 0.35)
                {
                    transform.Yaw += (float)((NextDouble() - 0.5) * 2.2);
                }

                // Hostiles drift toward a player they cannot yet perceive. A pure random walk
                // converges on nobody: creatures spawn beyond their own senses by design, so
                // without a weak pull they wander until they despawn and the night is empty.
                // This is not detection; they still have to see or hear the player to engage.
                Vec3d pull = NearestPlayerDrift(transform.Position, definition);
                Vec3d wander = NextDouble() < 0.55
                    ? new Vec3d(Math.Sin(transform.Yaw), 0, -Math.Cos(transform.Yaw))
                    : Vec3d.Zero;
                // Blended rather than replaced. A full pull turns every hostile within the radius
                // into a homing missile and the whole population arrives at once; a partial one
                // makes them converge over a minute or two, which is the pressure that was wanted.
                desired = pull.LengthSquared > 1e-6 ? (wander * 0.55 + pull * 0.45) : wander;
                speed = definition.WalkSpeed * 0.55;
                break;

            case AwarenessState.Suspicious:
                // Turn toward the disturbance and edge closer.
                desired = (perception.LastKnownPosition - transform.Position).Horizontal.Normalised;
                speed = definition.WalkSpeed * 0.8;
                break;

            case AwarenessState.Searching:
                desired = (perception.LastKnownPosition - transform.Position).Horizontal.Normalised;
                speed = definition.WalkSpeed;
                if ((perception.LastKnownPosition - transform.Position).Horizontal.Length < 1.5)
                {
                    perception.StateTimer = Math.Min(perception.StateTimer, 1.0f);
                }
                break;

            case AwarenessState.Combat:
                desired = Pursue(self, definition, ref perception, ref transform, dt);
                speed = definition.ChaseSpeed;
                break;

            case AwarenessState.Fleeing:
                if (Entities.IsAlive(perception.Target))
                {
                    desired = (transform.Position - Transforms.Get(perception.Target).Position).Horizontal.Normalised;
                }
                speed = definition.ChaseSpeed;
                break;
        }

        if (desired.LengthSquared > 1e-6)
        {
            desired = desired.Normalised;
            transform.Yaw = (float)Math.Atan2(desired.X, -desired.Z);
        }

        // Accelerate toward the wish rather than snapping, so creatures have weight.
        Vec3d wish = desired * speed;
        Vec3d horizontal = motion.Velocity.Horizontal;
        Vec3d delta = wish - horizontal;
        double step = 14.0 * dt;
        horizontal += delta.Length <= step ? delta : delta.Normalised * step;

        motion.Velocity = new Vec3d(horizontal.X, motion.Velocity.Y, horizontal.Z);
    }

    /// <summary>Direction toward the nearest player, if one is within the drift radius and this
    /// species hunts. Zero otherwise.</summary>
    private Vec3d NearestPlayerDrift(Vec3d position, CreatureDefinition definition)
    {
        if (definition.Faction != CreatureFaction.Hostile) return Vec3d.Zero;

        const double DriftRadius = 30.0;
        Vec3d best = Vec3d.Zero;
        double bestDistance = DriftRadius;

        foreach (Entity player in _players)
        {
            if (!Entities.IsAlive(player) || PlayerTags.Get(player).IsDead) continue;
            Vec3d toPlayer = (Transforms.Get(player).Position - position).Horizontal;
            double distance = toPlayer.Length;
            if (distance >= bestDistance || distance < 1e-3) continue;
            bestDistance = distance;
            best = toPlayer / distance;
        }

        return best;
    }

    private Vec3d Pursue(Entity self, CreatureDefinition definition, ref Perception perception,
                         ref Transform transform, double dt)
    {
        if (!Entities.IsAlive(perception.Target)) return Vec3d.Zero;

        Vec3d targetPosition = Transforms.Get(perception.Target).Position;
        Vec3d toTarget = (targetPosition - transform.Position).Horizontal;
        double distance = toTarget.Length;

        // Detonators close and blow up rather than trading blows.
        if (definition.Detonates && distance <= definition.AttackReach)
        {
            Detonate(self, transform.Position, definition);
            return Vec3d.Zero;
        }

        if (distance <= definition.AttackReach && perception.AttackCooldown <= 0.0f)
        {
            Attack(self, perception.Target, definition);
            perception.AttackCooldown = (float)definition.AttackInterval;
        }

        // Ranged attackers hold their distance; melee ones close.
        if (definition.PreferredRange > 0.0)
        {
            if (distance < definition.PreferredRange * 0.75) return -toTarget.Normalised;
            if (distance > definition.PreferredRange * 1.15) return toTarget.Normalised;
            // In the pocket: strafe rather than stand still, which makes archers awkward to hit.
            return new Vec3d(-toTarget.Z, 0, toTarget.X).Normalised * (NextDouble() < 0.5 ? 1.0 : -1.0);
        }

        return distance > definition.AttackReach * 0.8 ? toTarget.Normalised : Vec3d.Zero;
    }

    private void Attack(Entity attacker, Entity target, CreatureDefinition definition)
    {
        if (definition.AttackDamage <= 0.0f) return;

        float dealt = ApplyDamage(target, definition.AttackDamage, (DamageType)definition.AttackDamageType, attacker);
        if (dealt <= 0.0f || !Motions.Has(target)) return;

        // Knockback, away from the attacker and slightly up so it reads as an impact.
        Vec3d away = (Transforms.Get(target).Position - Transforms.Get(attacker).Position).Horizontal.Normalised;
        ref Motion motion = ref Motions.Get(target);
        motion.Velocity += away * definition.Knockback + new Vec3d(0, definition.Knockback * 0.35, 0);
    }

    /// <summary>
    /// The creeper role: the creature destroys itself and everything near it. Terrain damage
    /// is reported as an event rather than applied here, because carving the density field is
    /// the world's business, not the simulation's.
    /// </summary>
    private void Detonate(Entity self, Vec3d position, CreatureDefinition definition)
    {
        _events.Add(new SimEvent(SimEventKind.Exploded, self, position, (float)definition.BlastRadius));

        double radius = definition.BlastRadius;
        foreach (Entity victim in NearbyDamageable(position, radius))
        {
            double distance = (Transforms.Get(victim).Position - position).Length;
            float falloff = (float)Math.Clamp(1.0 - distance / radius, 0.0, 1.0);
            ApplyDamage(victim, 22.0f * falloff * falloff, DamageType.Blunt, self, invulnerableSeconds: 0.0);

            if (Motions.Has(victim))
            {
                Vec3d away = (Transforms.Get(victim).Position - position).Normalised;
                Motions.Get(victim).Velocity += away * (10.0 * falloff);
            }
        }

        Kill(self);
    }

    private List<Entity> NearbyDamageable(Vec3d position, double radius)
    {
        var found = new List<Entity>();
        double radiusSquared = radius * radius;

        foreach (Entity player in _players)
        {
            if (Entities.IsAlive(player) && (Transforms.Get(player).Position - position).LengthSquared <= radiusSquared)
            {
                found.Add(player);
            }
        }

        ReadOnlySpan<int> owners = Creatures.Owners;
        for (int i = 0; i < owners.Length; i++)
        {
            Entity entity = EntityAt(owners[i]);
            if (!Entities.IsAlive(entity)) continue;
            if ((Transforms.Get(entity).Position - position).LengthSquared <= radiusSquared) found.Add(entity);
        }

        return found;
    }

    // ── Movement ─────────────────────────────────────────────────────────────────

    /// <summary>
    /// Integrates every moving entity against the density field.
    /// <para>
    /// Deliberately simpler than the player's controller: creatures and dropped items are
    /// approximated by a single sphere rather than a capsule, with no step-up or mantling.
    /// They are pushed out along the field gradient, which is enough to keep them on the
    /// surface, and the cost of the full controller for hundreds of entities is not repaid.
    /// </para>
    /// </summary>
    private void UpdateMovement(double dt)
    {
        ReadOnlySpan<int> owners = Motions.Owners;
        for (int i = 0; i < owners.Length; i++)
        {
            Entity entity = EntityAt(owners[i]);
            if (!Entities.IsAlive(entity)) continue;
            if (PlayerTags.Has(entity)) continue;   // players are driven by their own controller

            ref Motion motion = ref Motions.Data[i];
            ref Transform transform = ref Transforms.Get(entity);

            double radius = 0.35;
            bool isCreature = Creatures.Has(entity);
            if (isCreature) radius = CreatureCatalog.Get(Creatures.Get(entity).SpeciesId).Radius;
            else if (ItemDrops.Has(entity)) radius = 0.18;

            // Gravity, with a small downward bias when grounded so the entity stays attached
            // walking over a convex edge.
            motion.Velocity = motion.Grounded && motion.Velocity.Y <= 0.0
                ? new Vec3d(motion.Velocity.X, -1.5, motion.Velocity.Z)
                : new Vec3d(motion.Velocity.X, Math.Max(-70.0, motion.Velocity.Y - Gravity * dt), motion.Velocity.Z);

            Vec3d centre = transform.Position + new Vec3d(0, radius, 0) + motion.Velocity * dt;

            centre = SphereTrace.Depenetrate(_volume, centre, radius, out Vec3d contactNormal, out bool touched);

            if (touched)
            {
                motion.Velocity = motion.Velocity.SlideAlong(contactNormal);
                motion.Grounded = contactNormal.Y > 0.6;
            }
            else
            {
                motion.Grounded = false;
            }

            transform.Position = centre - new Vec3d(0, radius, 0);

            // Ground friction for items, so a dropped pickaxe settles instead of sliding forever.
            if (motion.Grounded && ItemDrops.Has(entity))
            {
                Vec3d horizontal = motion.Velocity.Horizontal;
                double drop = 8.0 * dt;
                horizontal = horizontal.Length <= drop ? Vec3d.Zero : horizontal.Normalised * (horizontal.Length - drop);
                motion.Velocity = new Vec3d(horizontal.X, motion.Velocity.Y, horizontal.Z);
            }

            // Falling out of the world is fatal rather than infinite.
            if (transform.Position.Y < World.Generation.DensityField.WorldBottom + 1.0)
            {
                Kill(entity);
            }
        }
    }
}
