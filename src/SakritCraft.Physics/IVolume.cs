using SakritCraft.Core.Math;
using SakritCraft.World.Generation;

namespace SakritCraft.Physics;

/// <summary>
/// Anything the physics can collide with, expressed as a scalar field.
/// <para>
/// Collision uses the density field directly rather than a triangle mesh. That choice
/// carries the project: there is no collision mesh to rebuild when the player mines, so
/// an edit costs nothing; thin geometry cannot be tunnelled through; the contact normal
/// is exact rather than interpolated from a face; and the memory cost is zero beyond
/// the density data already resident for rendering.
/// </para>
/// </summary>
public interface IVolume
{
    /// <summary>Signed distance in metres. Negative inside solid, positive in open air.</summary>
    double Sample(double x, double y, double z);
}

/// <summary>Convenience operations available on any <see cref="IVolume"/>.</summary>
public static class VolumeExtensions
{
    public static double Sample(this IVolume volume, Vec3d p) => volume.Sample(p.X, p.Y, p.Z);

    /// <summary>
    /// Outward surface normal from the field gradient. Exact up to the sampling step,
    /// and free of the faceting a triangle normal would give.
    /// </summary>
    public static Vec3d Normal(this IVolume volume, Vec3d p, double epsilon = 0.05)
    {
        double dx = volume.Sample(p.X + epsilon, p.Y, p.Z) - volume.Sample(p.X - epsilon, p.Y, p.Z);
        double dy = volume.Sample(p.X, p.Y + epsilon, p.Z) - volume.Sample(p.X, p.Y - epsilon, p.Z);
        double dz = volume.Sample(p.X, p.Y, p.Z + epsilon) - volume.Sample(p.X, p.Y, p.Z - epsilon);

        var gradient = new Vec3d(dx, dy, dz);
        return gradient.LengthSquared < 1e-18 ? Vec3d.UnitY : gradient.Normalised;
    }

    /// <summary>True if the point is inside solid material.</summary>
    public static bool IsSolid(this IVolume volume, Vec3d p) => volume.Sample(p) < 0.0;
}

/// <summary>Adapts the world generator's density field to the physics interface.</summary>
public sealed class DensityFieldVolume : IVolume
{
    private readonly DensityField _field;

    public DensityFieldVolume(DensityField field) => _field = field;

    public double Sample(double x, double y, double z) => _field.Sample(x, y, z);
}
