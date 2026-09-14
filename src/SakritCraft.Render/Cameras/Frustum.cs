using System.Numerics;
using System.Runtime.CompilerServices;

namespace SakritCraft.Render.Cameras;

/// <summary>
/// Five clip planes (left, right, bottom, top, near) extracted from a row-vector view-projection
/// matrix, in the same camera-relative space the geometry is drawn in. There is no far plane because
/// the projection is infinite. Planes point inward: a point with positive distance is inside.
/// </summary>
public readonly struct Frustum
{
    private readonly Vector4 _p0, _p1, _p2, _p3, _p4;

    /// <summary>
    /// For clip = v * M, clip.x = dot(v, column 0) and so on. Inside means -w &lt;= x &lt;= w,
    /// -w &lt;= y &lt;= w and (reverse-Z, depth in [0,1]) z &lt;= w. The far bound z &gt;= 0 is always
    /// satisfied by an infinite projection and is omitted.
    /// </summary>
    public Frustum(in Matrix4x4 viewProjection)
    {
        var m = viewProjection;
        var c0 = new Vector4(m.M11, m.M21, m.M31, m.M41);
        var c1 = new Vector4(m.M12, m.M22, m.M32, m.M42);
        var c2 = new Vector4(m.M13, m.M23, m.M33, m.M43);
        var c3 = new Vector4(m.M14, m.M24, m.M34, m.M44);
        _p0 = Normalize(c3 + c0); // left
        _p1 = Normalize(c3 - c0); // right
        _p2 = Normalize(c3 + c1); // one vertical bound
        _p3 = Normalize(c3 - c1); // the other
        _p4 = Normalize(c3 - c2); // near
    }

    /// <summary>
    /// Conservative AABB test: a box is culled only when it lies entirely outside at least one plane
    /// (p-vertex test). Boxes in a frustum corner may be accepted although they are outside; that is
    /// the standard trade for a five-dot-product test and costs a few needless draws, never a hole.
    /// </summary>
    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    public bool Intersects(in Vector3 min, in Vector3 max)
        => Inside(_p0, min, max) && Inside(_p1, min, max) && Inside(_p2, min, max) && Inside(_p3, min, max) && Inside(_p4, min, max);

    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    private static bool Inside(in Vector4 plane, in Vector3 min, in Vector3 max)
    {
        // The corner furthest along the plane normal; if even that is behind the plane the box is out.
        float x = plane.X >= 0 ? max.X : min.X;
        float y = plane.Y >= 0 ? max.Y : min.Y;
        float z = plane.Z >= 0 ? max.Z : min.Z;
        return plane.X * x + plane.Y * y + plane.Z * z + plane.W >= 0;
    }

    private static Vector4 Normalize(Vector4 plane)
    {
        float length = MathF.Sqrt(plane.X * plane.X + plane.Y * plane.Y + plane.Z * plane.Z);
        return length > 1e-12f ? plane / length : plane;
    }
}
