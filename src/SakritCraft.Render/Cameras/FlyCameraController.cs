using System.Numerics;
using Silk.NET.Input;
using Silk.NET.Maths;

namespace SakritCraft.Render.Cameras;

/// <summary>
/// Free-fly camera control: WASD moves in the horizontal look direction, Space/Ctrl move straight up
/// and down, Shift sprints, the mouse looks while captured, and the scroll wheel scales speed. Velocity
/// eases toward the input with a first-order filter so starting and stopping feel weighted rather than
/// binary. Positions are integrated in double (section 03); only the camera's own float basis vectors
/// are used for direction.
///
/// Mouse capture: a click inside the window captures the cursor (raw, unbounded motion), Escape
/// releases it. Starting uncaptured means an automated run never fights the desktop for the pointer.
/// Steady-state <see cref="Update"/> polls key state and allocates nothing.
/// </summary>
public sealed class FlyCameraController : IDisposable
{
    private readonly Camera _camera;
    private readonly IInputContext _input;
    private readonly IKeyboard? _keyboard;
    private readonly IMouse? _mouse;
    private Vector3D<double> _velocity;
    private Vector2 _lastMouse;
    private Vector2 _mouseDelta;
    private bool _haveLastMouse;
    private bool _captured;

    /// <summary>Walking-pace fly speed in m/s before the sprint multiplier.</summary>
    public double BaseSpeed { get; set; } = 12.0;
    public double SprintMultiplier { get; set; } = 5.0;
    /// <summary>Radians per pixel of mouse travel.</summary>
    public float LookSensitivity { get; set; } = 0.0022f;
    /// <summary>Velocity filter rate (1/s). 12 reaches 95% of target speed in a quarter second.</summary>
    public double Acceleration { get; set; } = 12.0;
    /// <summary>Speed multiplier from the scroll wheel, clamped to [0.05, 40].</summary>
    public double SpeedScale { get; private set; } = 1.0;

    public bool IsCaptured => _captured;
    /// <summary>Set by F1 (toggle); read by the renderer for the wireframe debug view.</summary>
    public bool WireframeRequested { get; private set; }

    public FlyCameraController(Camera camera, IInputContext input, bool wireframe = false)
    {
        _camera = camera;
        _input = input;
        WireframeRequested = wireframe;
        _keyboard = input.Keyboards.Count > 0 ? input.Keyboards[0] : null;
        _mouse = input.Mice.Count > 0 ? input.Mice[0] : null;

        if (_mouse is not null)
        {
            _mouse.MouseMove += OnMouseMove;
            _mouse.MouseDown += OnMouseDown;
            _mouse.Scroll += OnScroll;
        }

        if (_keyboard is not null)
        {
            _keyboard.KeyDown += OnKeyDown;
        }
    }

    /// <summary>Advances the camera by <paramref name="dt"/> seconds. Call once per frame before rendering.</summary>
    public void Update(double dt)
    {
        dt = Math.Clamp(dt, 0.0, 0.1); // a hitch must not teleport the camera

        if (_captured)
        {
            _camera.Yaw += _mouseDelta.X * LookSensitivity;
            _camera.Pitch -= _mouseDelta.Y * LookSensitivity;
            if (_camera.Yaw > MathF.PI) _camera.Yaw -= 2 * MathF.PI;
            if (_camera.Yaw < -MathF.PI) _camera.Yaw += 2 * MathF.PI;
        }

        _mouseDelta = Vector2.Zero;

        var wish = Vector3D<double>.Zero;
        if (_keyboard is not null)
        {
            // Horizontal movement follows the look direction projected onto the ground plane so
            // looking down and pressing W does not dive; Space/Ctrl own the vertical axis.
            var forward = _camera.Forward;
            var flat = new Vector3D<double>(forward.X, 0, forward.Z);
            if (flat.LengthSquared > 1e-8) flat = Vector3D.Normalize(flat);
            var right = new Vector3D<double>(_camera.Right.X, 0, _camera.Right.Z);

            if (_keyboard.IsKeyPressed(Key.W)) wish += flat;
            if (_keyboard.IsKeyPressed(Key.S)) wish -= flat;
            if (_keyboard.IsKeyPressed(Key.D)) wish += right;
            if (_keyboard.IsKeyPressed(Key.A)) wish -= right;
            if (_keyboard.IsKeyPressed(Key.Space)) wish += Vector3D<double>.UnitY;
            if (_keyboard.IsKeyPressed(Key.ControlLeft) || _keyboard.IsKeyPressed(Key.ControlRight)) wish -= Vector3D<double>.UnitY;

            if (wish.LengthSquared > 1e-8)
            {
                wish = Vector3D.Normalize(wish);
                double speed = BaseSpeed * SpeedScale;
                if (_keyboard.IsKeyPressed(Key.ShiftLeft) || _keyboard.IsKeyPressed(Key.ShiftRight)) speed *= SprintMultiplier;
                wish *= speed;
            }
        }

        // First-order approach: v += (target - v) * (1 - e^(-k dt)).
        double blend = 1.0 - Math.Exp(-Acceleration * dt);
        _velocity += (wish - _velocity) * blend;
        _camera.Position += _velocity * dt;
    }

    private void OnMouseMove(IMouse mouse, Vector2 position)
    {
        if (_haveLastMouse)
        {
            _mouseDelta += position - _lastMouse;
        }

        _lastMouse = position;
        _haveLastMouse = true;
    }

    private void OnMouseDown(IMouse mouse, MouseButton button)
    {
        if (!_captured && button == MouseButton.Left)
        {
            SetCaptured(true);
        }
    }

    private void OnScroll(IMouse mouse, ScrollWheel wheel)
    {
        if (wheel.Y == 0) return;
        SpeedScale = Math.Clamp(SpeedScale * Math.Pow(1.25, wheel.Y), 0.05, 40.0);
    }

    private void OnKeyDown(IKeyboard keyboard, Key key, int scancode)
    {
        switch (key)
        {
            case Key.Escape when _captured:
                SetCaptured(false);
                break;
            case Key.F1:
                WireframeRequested = !WireframeRequested;
                break;
        }
    }

    private void SetCaptured(bool captured)
    {
        if (_mouse is null) return;
        _captured = captured;
        // Raw mode hides the cursor and reports unbounded motion; the delta is reset so the jump from
        // the click position does not register as a look.
        _mouse.Cursor.CursorMode = captured ? CursorMode.Raw : CursorMode.Normal;
        _haveLastMouse = false;
        _mouseDelta = Vector2.Zero;
    }

    public void Dispose()
    {
        if (_mouse is not null)
        {
            _mouse.MouseMove -= OnMouseMove;
            _mouse.MouseDown -= OnMouseDown;
            _mouse.Scroll -= OnScroll;
            if (_captured) _mouse.Cursor.CursorMode = CursorMode.Normal;
        }

        if (_keyboard is not null)
        {
            _keyboard.KeyDown -= OnKeyDown;
        }

        _input.Dispose();
    }
}
