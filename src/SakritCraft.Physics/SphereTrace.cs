using SakritCraft.Core.Math;

namespace SakritCraft.Physics;

/// <summary>Result of a trace against the volume.</summary>
public readonly struct TraceHit
{
    /// <summary>Whether anything was struck.</summary>
    public readonly bool Hit;

    /// <summary>Distance travelled before contact, in metres.</summary>
    public readonly double Distance;

    /// <summary>Contact point on the surface.</summary>
    public readonly Vec3d Point;

    /// <summary>Outward surface normal at the contact.</summary>
    public readonly Vec3d Normal;

    public TraceHit(bool hit, double distance, Vec3d point, Vec3d normal)
    {
        Hit = hit; Distance = distance; Point = point; Normal = normal;
    }

    public static TraceHit Miss(double distance) => new(false, distance, Vec3d.Zero, Vec3d.UnitY);
}

/// <summary>
/// Ray and sphere tracing against a density field by sphere marching.
/// <para>
/// Each step advances by the field value at the current point, which cannot overshoot
/// the surface when the field is a true distance. This field is only approximately
/// metric, so steps are scaled by a safety factor below one. That costs a few extra
/// iterations and removes the entire class of bug where a fast-moving body steps
/// straight through a thin wall.
/// </para>
/// </summary>
public static class SphereTrace
{
    /// <summary>
    /// Fraction of the reported distance actually stepped. The density field
    /// overestimates distance in places, most notably where a cave subtraction meets
    /// the terrain surface, so stepping the full amount can jump past the surface.
    /// </summary>
    private const double SafetyFactor = 0.6;

    /// <summary>Contact is declared when the field falls below this, in metres.</summary>
    private const double ContactEpsilon = 0.01;

    private const int MaxIterations = 96;

    /// <summary>
    /// Traces a ray. Used for the player's mining reach, for line of sight, and for
    /// the ground probe.
    /// </summary>
    public static TraceHit Ray(IVolume volume, Vec3d origin, Vec3d direction, double maxDistance)
        => Sphere(volume, origin, direction, maxDistance, 0.0);

    /// <summary>
    /// Traces a sphere of the given radius. Contact happens when the field value falls
    /// below the radius rather than below zero, which is what makes a swept sphere as
    /// cheap as a ray against a distance field.
    /// </summary>
    public static TraceHit Sphere(IVolume volume, Vec3d origin, Vec3d direction,
                                  double maxDistance, double radius)
    {
        Vec3d dir = direction.Normalised;
        if (dir.LengthSquared < 0.5) return TraceHit.Miss(0.0);

        double travelled = 0.0;

        // Starting already in contact: report immediately so the caller depenetrates
        // rather than tunnelling forward out of the solid.
        double initial = volume.Sample(origin) - radius;
        if (initial <= ContactEpsilon)
        {
            return new TraceHit(true, 0.0, origin, volume.Normal(origin));
        }

        for (int i = 0; i < MaxIterations && travelled < maxDistance; i++)
        {
            Vec3d p = origin + dir * travelled;
            double distance = volume.Sample(p) - radius;

            if (distance <= ContactEpsilon)
            {
                return new TraceHit(true, travelled, p, volume.Normal(p));
            }

            // Never step less than a millimetre, or a grazing trace stalls out.
            travelled += System.Math.Max(distance * SafetyFactor, 0.001);
        }

        return TraceHit.Miss(System.Math.Min(travelled, maxDistance));
    }

    /// <summary>
    /// Pushes a sphere out of any solid it overlaps, returning the corrected centre.
    /// <para>
    /// Iterated rather than solved once, because moving along the gradient can push the
    /// sphere into a different surface in a corner. Three passes resolve essentially
    /// every case encountered in practice.
    /// </para>
    /// </summary>
    public static Vec3d Depenetrate(IVolume volume, Vec3d centre, double radius,
                                    out Vec3d contactNormal, out bool touched)
    {
        contactNormal = Vec3d.Zero;
        touched = false;

        for (int pass = 0; pass < 4; pass++)
        {
            double distance = volume.Sample(centre);
            double overlap = radius - distance;
            if (overlap <= 0.0) break;

            Vec3d normal = volume.Normal(centre);
            centre += normal * overlap;
            contactNormal += normal;
            touched = true;
        }

        contactNormal = contactNormal.LengthSquared > 1e-12 ? contactNormal.Normalised : Vec3d.UnitY;
        return centre;
    }
}
