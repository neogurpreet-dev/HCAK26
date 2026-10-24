using System;
using Godot;

namespace Atlas;

public readonly record struct CameraView(Vector3 Focus, float Yaw, float Pitch, float Distance);

/// <summary>
/// Orbit camera rig. Attach to the CameraRig Node3D that has a Camera3D child.
/// Controls: middle-drag = pan, right-drag = orbit (or pan when RightDragOrbits is off),
/// Shift swaps pan/orbit, wheel = zoom toward cursor, Q/E = rotate, arrows = pan, Home = reset.
/// Selection focus uses a cubic ease-out tween; any manual input kills it first (no competing animations).
/// </summary>
public partial class WorldCamera : Node3D
{
    [Export] public float MinDistance { get; set; } = 14f;
    [Export] public float MaxDistance { get; set; } = 280f;
    [Export] public float MinPitchDegrees { get; set; } = 12f;
    [Export] public float MaxPitchDegrees { get; set; } = 84f;
    /// <summary>When true the pitch limits are ignored (only kept just above/below the poles).</summary>
    [Export] public bool FreeOrbit { get; set; } = false;
    [Export] public bool RightDragOrbits { get; set; } = true;
    [Export] public float FieldOfView { get; set; } = 30f;
    [Export] public float PanSpeed { get; set; } = 0.0016f;
    [Export] public float OrbitSpeed { get; set; } = 0.006f;
    [Export] public float ZoomStep { get; set; } = 1.12f;
    [Export] public float Smoothness { get; set; } = 11f;
    /// <summary>Distance multiplier applied when a building is selected (0.85 = zoom in 15%).</summary>
    [Export] public float SelectZoomFactor { get; set; } = 0.88f;
    [Export] public float DefaultYawDegrees { get; set; } = 45f;
    [Export] public float DefaultPitchDegrees { get; set; } = 36f;

    public Camera3D Camera { get; private set; }
    /// <summary>Maximum distance from the origin the focus point may travel.</summary>
    public float FocusBounds { get; set; } = 120f;
    /// <summary>Current (smoothed) orbit yaw in radians; used to keep the scene lights fixed relative to the view.</summary>
    public float Yaw => _yaw;
    public bool IsInteracting => _dragButton != MouseButton.None;
    /// <summary>Raised when the right button is released without having been dragged (context menu in Part 2).</summary>
    public event Action<Vector2> RightClickWithoutDrag;

    Vector3 _focus, _focusT;
    float _yaw, _yawT, _pitch, _pitchT, _dist, _distT;
    float _defaultDist = 110f;
    Tween _focusTween;
    MouseButton _dragButton = MouseButton.None;
    float _dragTravel;

    public override void _Ready()
    {
        Camera = GetNodeOrNull<Camera3D>("Camera3D");
        if (Camera == null) { Camera = new Camera3D { Name = "Camera3D" }; AddChild(Camera); }
        Camera.Fov = FieldOfView;
        Camera.Near = 0.5f;
        Camera.Far = 1500f;
        Camera.Current = true;
        SnapToDefault();
        UpdateCameraTransform();
    }

    // ------------------------------------------------------------------ public API

    public void SnapToDefault()
    {
        _yaw = _yawT = Mathf.DegToRad(DefaultYawDegrees);
        _pitch = _pitchT = Mathf.DegToRad(DefaultPitchDegrees);
        _dist = _distT = _defaultDist;
        _focus = _focusT = Vector3.Zero;
    }

    /// <summary>Smoothly returns to the polished default isometric-like angle, framed for the given world radius.</summary>
    public void ResetView(float worldRadius, bool animate = true)
    {
        _focusTween?.Kill();
        _defaultDist = Mathf.Clamp(Mathf.Max(40f, worldRadius * 2.35f), MinDistance, MaxDistance);
        FocusBounds = Mathf.Max(30f, worldRadius * 1.3f);
        _yawT = Mathf.DegToRad(DefaultYawDegrees);
        _pitchT = Mathf.DegToRad(DefaultPitchDegrees);
        _distT = _defaultDist;
        _focusT = Vector3.Zero;
        if (!animate) { _yaw = _yawT; _pitch = _pitchT; _dist = _distT; _focus = _focusT; UpdateCameraTransform(); }
    }

    public CameraView CaptureView() => new CameraView(_focusT, _yawT, _pitchT, _distT);

    public void ApplyView(CameraView v)
    {
        _focusTween?.Kill();
        _focusT = v.Focus; _yawT = v.Yaw; _pitchT = v.Pitch; _distT = Mathf.Clamp(v.Distance, MinDistance, MaxDistance);
    }

    /// <summary>Smooth focus on a world point with an optional zoom multiplier.</summary>
    public void FocusOn(Vector3 worldPoint, float zoomFactor = 1f)
    {
        _focusTween?.Kill();
        var startFocus = _focusT;
        var endFocus = ClampFocus(new Vector3(worldPoint.X, 0f, worldPoint.Z).Lerp(worldPoint, 0.5f));
        float startDist = _distT;
        float endDist = Mathf.Clamp(_distT * zoomFactor, MinDistance, MaxDistance);

        _focusTween = CreateTween().SetTrans(Tween.TransitionType.Cubic).SetEase(Tween.EaseType.Out);
        _focusTween.TweenMethod(Callable.From<float>(t =>
        {
            _focusT = startFocus.Lerp(endFocus, t);
            _distT = Mathf.Lerp(startDist, endDist, t);
        }), 0f, 1f, 0.6f);
    }

    // ------------------------------------------------------------------ frame update

    public override void _Process(double delta)
    {
        float k = 1f - Mathf.Exp(-Smoothness * (float)delta);
        _focus = _focus.Lerp(_focusT, k);
        _yaw = Mathf.Lerp(_yaw, _yawT, k);
        _pitch = Mathf.Lerp(_pitch, _pitchT, k);
        _dist = Mathf.Lerp(_dist, _distT, k);
        HandleKeys((float)delta);
        UpdateCameraTransform();
    }

    void HandleKeys(float delta)
    {
        if (Camera == null) return;
        var focusOwner = GetViewport().GuiGetFocusOwner();
        if (focusOwner is LineEdit || focusOwner is TextEdit) return; // don't steer while typing
        Vector2 move = Vector2.Zero;
        if (Input.IsKeyPressed(Key.Left)) move.X -= 1;
        if (Input.IsKeyPressed(Key.Right)) move.X += 1;
        if (Input.IsKeyPressed(Key.Up)) move.Y += 1;
        if (Input.IsKeyPressed(Key.Down)) move.Y -= 1;
        float rot = 0;
        if (Input.IsKeyPressed(Key.Q)) rot -= 1;
        if (Input.IsKeyPressed(Key.E)) rot += 1;
        if (move == Vector2.Zero && rot == 0) return;
        _focusTween?.Kill();
        float speed = _distT * 0.9f * delta;
        _focusT = ClampFocus(_focusT + (Right() * move.X + Forward() * move.Y) * speed);
        _yawT += rot * 1.6f * delta;
    }

    void UpdateCameraTransform()
    {
        if (Camera == null) return;
        float cp = Mathf.Cos(_pitch), sp = Mathf.Sin(_pitch);
        var offset = new Vector3(cp * Mathf.Sin(_yaw), sp, cp * Mathf.Cos(_yaw)) * _dist;
        Camera.Fov = FieldOfView;
        Camera.LookAtFromPosition(_focus + offset, _focus, Vector3.Up);
    }

    Vector3 Forward() => new Vector3(-Mathf.Sin(_yawT), 0, -Mathf.Cos(_yawT));
    Vector3 Right() => new Vector3(Mathf.Cos(_yawT), 0, -Mathf.Sin(_yawT));

    Vector3 ClampFocus(Vector3 f)
    {
        var flat = new Vector2(f.X, f.Z);
        if (flat.Length() > FocusBounds) flat = flat.Normalized() * FocusBounds;
        return new Vector3(flat.X, Mathf.Clamp(f.Y, -2f, 20f), flat.Y);
    }

    // ------------------------------------------------------------------ input

    public override void _UnhandledInput(InputEvent @event)
    {
        if (Camera == null) return;

        if (@event is InputEventMouseButton mb)
        {
            if (mb.ButtonIndex == MouseButton.WheelUp && mb.Pressed) { ZoomAt(mb.Position, 1f / ZoomStep); GetViewport().SetInputAsHandled(); }
            else if (mb.ButtonIndex == MouseButton.WheelDown && mb.Pressed) { ZoomAt(mb.Position, ZoomStep); GetViewport().SetInputAsHandled(); }
            else if (mb.ButtonIndex == MouseButton.Middle || mb.ButtonIndex == MouseButton.Right)
            {
                if (mb.Pressed) { _dragButton = mb.ButtonIndex; _dragTravel = 0f; _focusTween?.Kill(); }
                else if (_dragButton == mb.ButtonIndex)
                {
                    if (mb.ButtonIndex == MouseButton.Right && _dragTravel < 4f) RightClickWithoutDrag?.Invoke(mb.Position);
                    _dragButton = MouseButton.None;
                }
            }
        }
        else if (@event is InputEventMouseMotion mm && _dragButton != MouseButton.None)
        {
            _dragTravel += mm.Relative.Length();
            bool orbit = _dragButton == MouseButton.Right ? RightDragOrbits : false;
            if (mm.ShiftPressed) orbit = !orbit;
            if (_dragButton == MouseButton.Middle && !mm.ShiftPressed) orbit = false;
            if (orbit) Orbit(mm.Relative); else Pan(mm.Relative);
        }
        else if (@event is InputEventKey key && key.Pressed && !key.Echo && key.Keycode == Key.Home)
        {
            ResetView(FocusBounds / 1.3f);
        }
    }

    void Orbit(Vector2 rel)
    {
        _yawT -= rel.X * OrbitSpeed;
        _pitchT += rel.Y * OrbitSpeed;
        float lo = Mathf.DegToRad(FreeOrbit ? 3f : MinPitchDegrees);
        float hi = Mathf.DegToRad(FreeOrbit ? 89f : MaxPitchDegrees);
        _pitchT = Mathf.Clamp(_pitchT, lo, hi);
    }

    void Pan(Vector2 rel)
    {
        float k = PanSpeed * _distT;
        _focusT = ClampFocus(_focusT + Forward() * rel.Y * k - Right() * rel.X * k);
    }

    void ZoomAt(Vector2 screenPos, float factor)
    {
        _focusTween?.Kill();
        float oldDist = _distT;
        float newDist = Mathf.Clamp(oldDist * factor, MinDistance, MaxDistance);
        if (Mathf.IsEqualApprox(newDist, oldDist)) return;

        var from = Camera.ProjectRayOrigin(screenPos);
        var dir = Camera.ProjectRayNormal(screenPos);
        var hit = new Plane(Vector3.Up, 0f).IntersectsRay(from, dir);
        if (hit.HasValue)
        {
            var pivot = hit.Value;
            _focusT = ClampFocus(pivot + (_focusT - pivot) * (newDist / oldDist));
        }
        _distT = newDist;
    }

    public override void _ExitTree()
    {
        _focusTween?.Kill();
    }
}
