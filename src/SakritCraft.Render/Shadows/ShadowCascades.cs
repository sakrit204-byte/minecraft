using System.Numerics;
using SakritCraft.Render.Cameras;

namespace SakritCraft.Render.Shadows;

/// <summary>
/// Fits one orthographic light projection per cascade to a slice of the view frustum.
/// <para>
/// Everything is computed in camera-relative space, matching the terrain draw, which is
/// what keeps shadow maps working thousands of kilometres from the origin.
/// </para>
/// <para>
/// Two details do all the work of stopping shadows from crawling as the camera moves.
/// Each cascade is bounded by a <em>sphere</em> rather than by the slice's corners, so
/// its size does not change as the camera rotates; and the sphere's centre is snapped to
/// whole shadow-map texels, so the sampling grid stays locked to the world instead of
/// sliding under it. Without both, the shadow edges shimmer constantly and no amount of
/// filtering hides it.
/// </para>
/// </summary>
public sealed class ShadowCascades
{
    /// <summary>Number of cascades. Four covers a kilometre with usable resolution up close.</summary>
    public const int Count = 4;

    private readonly Matrix4x4[] _matrices = new Matrix4x4[Count];
    private readonly float[] _splits = new float[Count];
    private readonly float[] _texelWorldSize = new float[Count];

    /// <summary>Light view-projection per cascade, camera-relative, row-vector convention.</summary>
    public ReadOnlySpan<Matrix4x4> Matrices => _matrices;

    /// <summary>View-space distance at which each cascade ends, in metres.</summary>
    public ReadOnlySpan<float> Splits => _splits;

    /// <summary>World size of one shadow texel per cascade, for slope-scaled bias in the shader.</summary>
    public ReadOnlySpan<float> TexelWorldSize => _texelWorldSize;

    /// <summary>Distance beyond which nothing is shadowed and the sun is treated as unoccluded.</summary>
    public float MaxDistance { get; private set; }

    /// <summary>
    /// Recomputes every cascade for the current camera and sun direction.
    /// </summary>
    /// <param name="sunDirection">Unit vector pointing <em>toward</em> the sun.</param>
    /// <param name="resolution">Edge length of one shadow map layer, in texels.</param>
    /// <param name="maxDistance">Furthest distance the cascades cover.</param>
    /// <param name="casterMargin">How far behind each cascade to extend the light frustum, so that
    /// terrain outside the slice still casts into it. A mountain a few hundred metres behind the
    /// player must be able to shadow the ground in front of them.</param>
    public void Update(Camera camera, Vector3 sunDirection, uint resolution,
                       float maxDistance = 900.0f, float casterMargin = 600.0f)
    {
        MaxDistance = maxDistance;

        float near = camera.NearPlane;
        float tanV = camera.TanHalfFovY;
        float tanH = tanV * camera.AspectRatio;
        float k = tanH * tanH + tanV * tanV;

        Vector3 forward = camera.Forward;
        Vector3 lightDir = Vector3.Normalize(-sunDirection);    // direction light travels

        // Practical split scheme: a blend of logarithmic and uniform. Pure logarithmic wastes almost
        // the whole first cascade on the few metres in front of the near plane; pure uniform leaves
        // the near field with far too few texels.
        const float lambda = 0.82f;
        float previous = near;

        for (int i = 0; i < Count; i++)
        {
            float p = (i + 1) / (float)Count;
            float logSplit = near * MathF.Pow(maxDistance / near, p);
            float uniSplit = near + (maxDistance - near) * p;
            float split = lambda * logSplit + (1.0f - lambda) * uniSplit;
            _splits[i] = split;

            float n = previous;
            float f = split;
            previous = split;

            // Bounding sphere of this frustum slice, in closed form so its radius depends only on
            // the split distances and the field of view, never on camera orientation.
            float centreDistance = (f + n) * (k + 1.0f) * 0.5f;
            centreDistance = Math.Clamp(centreDistance, n, f * (k + 1.0f));
            float dx = centreDistance - f;
            float radius = MathF.Sqrt(f * f * k + dx * dx);
            radius = MathF.Max(radius, 1.0f);

            Vector3 centre = forward * centreDistance;

            // Light basis. Any up vector works as long as it is not parallel to the light.
            Vector3 up = MathF.Abs(lightDir.Y) > 0.98f ? Vector3.UnitZ : Vector3.UnitY;
            Vector3 right = Vector3.Normalize(Vector3.Cross(up, lightDir));
            up = Vector3.Cross(lightDir, right);

            // Snap the centre to whole texels along the light's own axes.
            float texel = 2.0f * radius / resolution;
            _texelWorldSize[i] = texel;

            float cx = MathF.Round(Vector3.Dot(centre, right) / texel) * texel;
            float cy = MathF.Round(Vector3.Dot(centre, up) / texel) * texel;
            float cz = Vector3.Dot(centre, lightDir);

            // Pull the light back far enough that casters behind the slice are still inside.
            float back = radius + casterMargin;
            float depthRange = back + radius;

            // Light view, row-vector: v' = (v - eye) * basis, with the light looking down -Z.
            // eye = right*cx + up*cy + lightDir*(cz - back)
            float ex = cx, ey = cy, ez = cz - back;   // components along (right, up, lightDir)

            var view = new Matrix4x4(
                right.X, up.X, -lightDir.X, 0,
                right.Y, up.Y, -lightDir.Y, 0,
                right.Z, up.Z, -lightDir.Z, 0,
                -ex, -ey, ez, 1);

            // Orthographic, Vulkan Y-down, depth 0 at the near plane and 1 at the far plane.
            var proj = new Matrix4x4(
                1.0f / radius, 0, 0, 0,
                0, -1.0f / radius, 0, 0,
                0, 0, -1.0f / depthRange, 0,
                0, 0, 0.0f, 1);

            _matrices[i] = Matrix4x4.Multiply(view, proj);
        }
    }
}
