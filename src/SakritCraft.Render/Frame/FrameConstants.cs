using System.Numerics;
using System.Runtime.InteropServices;

namespace SakritCraft.Render.Frame;

/// <summary>
/// Everything a shader needs that changes once per frame rather than per draw. Lives in a small
/// storage buffer per frame slot reached through the bindless heap; draws carry only its handle in
/// their push constants. Mirrors <c>FrameConstants</c> in shaders/include/frame.glsl with scalar
/// layout, so the C# and GLSL declarations must be edited together.
///
/// The camera position is deliberately absent: geometry is camera-relative, so the camera is at the
/// origin and a fragment's distance is simply the length of its position.
/// </summary>
[StructLayout(LayoutKind.Sequential, Size = 208)]
public struct FrameConstants
{
    /// <summary>View-projection as System.Numerics lays it out; GLSL reads the same bytes as the column-major mat4 it needs (see Camera.ViewProjectionForGpu).</summary>
    public Matrix4x4 ViewProjection;
    public Vector3 CameraRight;
    public float TanHalfFovY;
    public Vector3 CameraUp;
    public float AspectRatio;
    public Vector3 CameraForward;
    public float Time;
    /// <summary>Unit vector toward the sun.</summary>
    public Vector3 SunDirection;
    public float SunIntensity;
    public Vector3 SunColor;
    /// <summary>Extinction coefficient per metre for the distance fog term.</summary>
    public float FogDensity;
    public Vector3 SkyZenith;
    /// <summary>How fast fog thins with height above the camera (1/m).</summary>
    public float FogHeightFalloff;
    public Vector3 SkyHorizon;
    public float Exposure;
    public Vector3 GroundAmbient;
    /// <summary>Absolute camera height in metres. Small enough for a float; lets shaders vary colour by altitude.</summary>
    public float CameraHeight;
    /// <summary>
    /// Camera position modulo <see cref="PatternPeriod"/>. Surface patterns evaluate camera-relative
    /// position plus this, so their coordinates stay small everywhere in the world (no swimming far
    /// from the origin) at the cost of repeating every period, which the hash makes seamless.
    /// </summary>
    public Vector3 CameraPositionWrapped;
    public float Pad1;

    /// <summary>Period of shader-side surface patterns in metres; mirrors PATTERN_PERIOD in shaders/include/surface.glsl.</summary>
    public const double PatternPeriod = 1024.0;
}
