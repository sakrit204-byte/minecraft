using System.Globalization;
using System.Numerics;
using System.Runtime.CompilerServices;

namespace SakritCraft.Core.Math;

/// <summary>
/// A double-precision three-component vector, for positions in world space.
/// <para>
/// <see cref="Vector3"/> is single precision and loses sub-centimetre accuracy past
/// roughly 130 km from the origin, which is well inside this world's bounds. Positions,
/// velocities integrated over long distances, and anything derived from generation
/// therefore use this type. Only after subtracting the camera position does geometry
/// drop to <see cref="Vector3"/> for the GPU.
/// </para>
/// </summary>
public readonly struct Vec3d : IEquatable<Vec3d>
{
    public readonly double X, Y, Z;

    public Vec3d(double x, double y, double z) { X = x; Y = y; Z = z; }

    public static Vec3d Zero => new(0, 0, 0);
    public static Vec3d UnitX => new(1, 0, 0);
    public static Vec3d UnitY => new(0, 1, 0);
    public static Vec3d UnitZ => new(0, 0, 1);

    public double LengthSquared
    {
        [MethodImpl(MethodImplOptions.AggressiveInlining)]
        get => X * X + Y * Y + Z * Z;
    }

    public double Length
    {
        [MethodImpl(MethodImplOptions.AggressiveInlining)]
        get => System.Math.Sqrt(X * X + Y * Y + Z * Z);
    }

    /// <summary>Unit vector in the same direction, or zero if the input is degenerate.</summary>
    public Vec3d Normalised
    {
        get
        {
            double length = Length;
            return length < 1e-12 ? Zero : new Vec3d(X / length, Y / length, Z / length);
        }
    }

    /// <summary>The horizontal components only, with Y zeroed.</summary>
    public Vec3d Horizontal => new(X, 0.0, Z);

    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    public static Vec3d operator +(Vec3d a, Vec3d b) => new(a.X + b.X, a.Y + b.Y, a.Z + b.Z);

    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    public static Vec3d operator -(Vec3d a, Vec3d b) => new(a.X - b.X, a.Y - b.Y, a.Z - b.Z);

    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    public static Vec3d operator -(Vec3d v) => new(-v.X, -v.Y, -v.Z);

    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    public static Vec3d operator *(Vec3d v, double s) => new(v.X * s, v.Y * s, v.Z * s);

    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    public static Vec3d operator *(double s, Vec3d v) => v * s;

    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    public static Vec3d operator /(Vec3d v, double s) => new(v.X / s, v.Y / s, v.Z / s);

    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    public static double Dot(Vec3d a, Vec3d b) => a.X * b.X + a.Y * b.Y + a.Z * b.Z;

    public static Vec3d Cross(Vec3d a, Vec3d b) => new(
        a.Y * b.Z - a.Z * b.Y,
        a.Z * b.X - a.X * b.Z,
        a.X * b.Y - a.Y * b.X);

    public static Vec3d Lerp(Vec3d a, Vec3d b, double t) => a + (b - a) * t;

    /// <summary>
    /// Removes the component of this vector pointing into a surface, leaving the part
    /// that slides along it. This is what turns a blocked move into a slide rather than
    /// a stop, and it is the core of the collision response.
    /// </summary>
    public Vec3d SlideAlong(Vec3d surfaceNormal)
    {
        double into = Dot(this, surfaceNormal);
        return into >= 0.0 ? this : this - surfaceNormal * into;
    }

    /// <summary>
    /// Removes all component along a normal, leaving the vector flat in that plane.
    /// Unlike <see cref="SlideAlong"/> this also removes motion <em>away</em> from the
    /// surface, which is what is wanted for movement along a slope: the direction the
    /// player asks for has to be re-expressed in the plane they are standing on.
    /// </summary>
    public Vec3d ProjectOntoPlane(Vec3d normal) => this - normal * Dot(this, normal);

    /// <summary>Converts to single precision, for geometry already made camera-relative.</summary>
    public Vector3 ToVector3() => new((float)X, (float)Y, (float)Z);

    public bool Equals(Vec3d other) => X.Equals(other.X) && Y.Equals(other.Y) && Z.Equals(other.Z);
    public override bool Equals(object? obj) => obj is Vec3d v && Equals(v);
    public override int GetHashCode() => HashCode.Combine(X, Y, Z);

    public override string ToString() => string.Create(CultureInfo.InvariantCulture,
        $"({X:F2}, {Y:F2}, {Z:F2})");
}
