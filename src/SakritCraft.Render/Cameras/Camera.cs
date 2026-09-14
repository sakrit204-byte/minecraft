using System.Numerics;
using Silk.NET.Maths;

namespace SakritCraft.Render.Cameras;

/// <summary>
/// A perspective camera whose world position is a <c>double</c> vector and whose matrices contain
/// no translation at all. Geometry is made camera-relative on the CPU in double precision
/// (<see cref="RelativeTo"/>), so everything that reaches the GPU is a small <c>float</c>. This is
/// the rendering half of the coordinate rule in docs/MASTER-PLAN.html section 03: a float world
/// position 16 km from the origin has about 1 mm of precision and visibly jitters; a camera-relative
/// float at 300 m has micrometres.
///
/// The projection is reverse-Z with an infinite far plane: near maps to depth 1, infinity to 0.
/// Combined with a 32-bit float depth buffer this gives near-uniform relative precision from 5 cm to
/// the horizon and is why the pipeline defaults to GREATER_OR_EQUAL. Vulkan's Y-down NDC is handled
/// here by negating the Y row; Vulkan's front-face rule is defined with that convention in mind, so a
/// counter-clockwise mesh remains counter-clockwise and pipelines keep the default front face.
///
/// Matrices follow System.Numerics row-vector convention (clip = v * M). GLSL multiplies column
/// vectors, but its column-major storage reads the same bytes as System.Numerics' row-major storage
/// with the axes swapped, so <c>mat4 * vec4</c> in GLSL computes exactly <c>v * M</c>: the matrix is
/// uploaded as is, no transpose (<see cref="ViewProjectionForGpu"/>).
/// </summary>
public sealed class Camera
{
    private const float MaxPitch = 89.0f * MathF.PI / 180.0f;

    /// <summary>World position in metres, double precision.</summary>
    public Vector3D<double> Position;
    /// <summary>Rotation about +Y in radians. 0 looks down -Z; increasing turns right (toward +X).</summary>
    public float Yaw;
    /// <summary>Rotation above the horizon in radians, clamped just short of straight up/down.</summary>
    public float Pitch
    {
        get => _pitch;
        set => _pitch = Math.Clamp(value, -MaxPitch, MaxPitch);
    }

    private float _pitch;

    /// <summary>Vertical field of view in radians. 60 degrees vertical is about 90 horizontal at 16:9.</summary>
    public float FieldOfViewY = 60.0f * MathF.PI / 180.0f;
    /// <summary>Distance to the near plane in metres. 5 cm keeps the player's own hands from clipping.</summary>
    public float NearPlane = 0.05f;
    public float AspectRatio = 16.0f / 9.0f;

    public Vector3 Forward
    {
        get
        {
            float cp = MathF.Cos(_pitch);
            return new Vector3(MathF.Sin(Yaw) * cp, MathF.Sin(_pitch), -MathF.Cos(Yaw) * cp);
        }
    }

    /// <summary>Horizontal right vector (always level, so strafing never gains height).</summary>
    public Vector3 Right => new(MathF.Cos(Yaw), 0.0f, MathF.Sin(Yaw));

    public Vector3 Up => Vector3.Normalize(Vector3.Cross(Right, Forward));

    /// <summary>Camera-relative position of a world point, computed in double then narrowed.</summary>
    public Vector3 RelativeTo(Vector3D<double> world)
        => new((float)(world.X - Position.X), (float)(world.Y - Position.Y), (float)(world.Z - Position.Z));

    public Vector3 RelativeTo(double x, double y, double z)
        => new((float)(x - Position.X), (float)(y - Position.Y), (float)(z - Position.Z));

    /// <summary>Rotation-only view matrix (row-vector convention). Camera looks down -Z in view space.</summary>
    public Matrix4x4 View
    {
        get
        {
            var f = Forward;
            var r = Right;
            var u = Up;
            return new Matrix4x4(
                r.X, u.X, -f.X, 0,
                r.Y, u.Y, -f.Y, 0,
                r.Z, u.Z, -f.Z, 0,
                0, 0, 0, 1);
        }
    }

    /// <summary>Reverse-Z, infinite far plane, Vulkan Y-down (row-vector convention).</summary>
    public Matrix4x4 Projection
    {
        get
        {
            float f = 1.0f / MathF.Tan(FieldOfViewY * 0.5f);
            // clip.x = x * f/aspect ; clip.y = -y * f ; clip.z = near ; clip.w = -z
            return new Matrix4x4(
                f / AspectRatio, 0, 0, 0,
                0, -f, 0, 0,
                0, 0, 0, -1,
                0, 0, NearPlane, 0);
        }
    }

    /// <summary>View then projection, row-vector convention: clip = v * ViewProjection.</summary>
    public Matrix4x4 ViewProjection => Matrix4x4.Multiply(View, Projection);

    /// <summary>
    /// The matrix GLSL expects for <c>viewProj * vec4(p, 1)</c>. Memory layout: System.Numerics writes
    /// M[row][col] at index row*4+col; GLSL reads index col*4+row as G[col][row]; therefore G[c][r] = M[c][r]
    /// and GLSL's G*v = sum_c M[c][r] v[c] = (v*M)[r]. Identical bytes, no transpose.
    /// </summary>
    public Matrix4x4 ViewProjectionForGpu => ViewProjection;

    public float TanHalfFovY => MathF.Tan(FieldOfViewY * 0.5f);
}
