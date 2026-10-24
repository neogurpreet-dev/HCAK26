using System;
using Godot;

namespace Atlas;

public readonly record struct CameraView(Vector3 Focus, float Yaw, float Pitch, float Distance);

/// <summary>
/// Orbit camera rig. Attach to the CameraRig Node3D that has a Camera3D child.
/// Standard 3D-viewer controls, for mouse and touchpad:
///   Mouse     left-drag = orbit | Ctrl+left-drag = pan | right-drag or middle-drag = pan | Shift+left-drag = pan | wheel = zoom toward the cursor
///   Touchpad  click-and-drag (or tap-drag) = orbit | two-finger scroll up/down = zoom toward the cursor |
///             two-finger scroll sideways = rotate | pinch = zoom | Shift + two-finger scroll = pan
///   Keyboard  arrows = pan, Q/E = rotate, Home = reset
/// Shift swaps pan and orbit on any drag. A click without movement is left to WorldInteraction (selection).
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
    [Export] public bool RightDragOrbits { get; set; } = false;
    [Export] public float FieldOfView { get; set; } = 30f;
    [Export] public float PanSpeed { get; set; } = 0.0016f;
    [Export] public float OrbitSpeed { get; set; } = 0.006f;
    [Export] public float ZoomStep { get; set; } = 1.12f;
    /// <summary>Pixels a pressed button must travel before a click becomes a camera drag.</summary>
    [Export] public float DragThreshold { get; set; } = 4f;
    /// <summary>Touchpad two-finger scroll: how strongly it zooms (exponent applied to ZoomStep per scroll unit).</summary>
    [Export] public float GestureZoomSpeed { get; set; } = 0.6f;
    /// <summary>Touchpad / wheel sideways scroll or Shift+scroll: pixels of drag equivalent per scroll unit.</summary>
    [Export] public float ScrollDragPixels { get; set; } = 25f;
    [Export] public float Smoothness { get; set; } = 11f;
    /// <summary>Distance multiplier applied when a building is selected (0.85 = zoom in 15%).</summary>
    [Export] public float SelectZoomFactor { get; set; } = 0.88f;
    [Export] public float DefaultYawDegrees { get; set; } = 45f;
    [Export] public float DefaultPitchDegrees { get; set; } = 36f;
    /// <summary>Folder-transition spin: angle of one transition (the camera orbits this far, then settles on the exact target yaw).</summary>
    [Export] public float NavSpinDegrees { get; set; } = 180f;
    /// <summary>Folder-transition spin duration in seconds (cubic ease-out: fast start, zero angular velocity at the end).</summary>
    [Export] public float NavSpinDuration { get; set; } = 0.85f;
    /// <summary>+1 or -1: sign of the yaw change when OPENING a folder. Returning to the parent always uses the opposite sign.</summary>
    [Export] public int OpenSpinSign { get; set; } = 1;

    public Camera3D Camera { get; private set; }
    /// <summary>Maximum distance from the origin the focus point may travel.</summary>
    public float FocusBounds { get; set; } = 120f;
    /// <summary>Camera distance of the default (reset) view for the current world.</summary>
    public float DefaultDistance => _defaultDist;
    /// <summary>Current (smoothed) orbit yaw in radians; used to keep the scene lights fixed relative to the view.</summary>
    public float Yaw => _yaw;
    public bool IsInteracting => _dragButton != MouseButton.None;
    /// <summary>While a menu or dialog is open the arrow keys / Q / E belong to it, not to the camera.</summary>
    public bool KeyboardSuspended { get; set; }
    /// <summary>Raised when the right button is released without having been dragged (context menu in Part 2).</summary>
    public event Action<Vector2> RightClickWithoutDrag;

    Vector3 _focus, _focusT;
    float _yaw, _yawT, _pitch, _pitchT, _dist, _distT;
    float _defaultDist = 110f;
    Tween _focusTween;
    Tween _spinTween;
    bool _spinning;
    float _spinFinalYaw;
    MouseButton _dragButton = MouseButton.None;
    float _dragTravel;
    bool _leftDown;
    float _leftTravel;

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
        StopSpin();
        _yaw = _yawT = Mathf.DegToRad(DefaultYawDegrees);
        _pitch = _pitchT = Mathf.DegToRad(DefaultPitchDegrees);
        _dist = _distT = _defaultDist;
        _focus = _focusT = Vector3.Zero;
    }

    /// <summary>Smoothly returns to the polished default isometric-like angle, framed for the given world radius.</summary>
    public void ResetView(float worldRadius, bool animate = true)
    {
        _focusTween?.Kill();
        StopSpin();
        _defaultDist = Mathf.Clamp(Mathf.Max(40f, worldRadius * 2.35f), MinDistance, MaxDistance);
        FocusBounds = Mathf.Max(30f, worldRadius * 1.3f);
        _yawT = Mathf.DegToRad(DefaultYawDegrees);
        _pitchT = Mathf.DegToRad(DefaultPitchDegrees);
        _distT = _defaultDist;
        _focusT = Vector3.Zero;
        if (!animate) { _yaw = _yawT; _pitch = _pitchT; _dist = _distT; _focus = _focusT; UpdateCameraTransform(); }
    }

    /// <summary>Saved views use the SETTLED yaw: while a navigation spin is running the live yaw is mid-animation and must never be stored.</summary>
    public CameraView CaptureView() => new CameraView(_focusT, _spinning ? _spinFinalYaw : _yawT, _pitchT, _distT);

    public void ApplyView(CameraView v)
    {
        _focusTween?.Kill();
        StopSpin();
        _focusT = v.Focus; _yawT = v.Yaw; _pitchT = v.Pitch; _distT = Mathf.Clamp(v.Distance, MinDistance, MaxDistance);
    }

    /// <summary>
    /// Folder-transition spin. Call AFTER ResetView/ApplyView has set the target view: the yaw it left in place is the exact
    /// final orientation. The camera starts NavSpinDegrees away from it and orbits onto it with a cubic ease-out
    /// (angle = final - dir*angle*(1-E(t)), E(t) = 1-(1-t)^3), so the spin starts fast and ends with zero angular velocity.
    /// direction > 0 = opening a folder, direction &lt; 0 = returning to the parent (same axis, same angle, opposite sign).
    /// Because the endpoint is always the target yaw (never "current yaw + angle"), repeated navigation cannot drift.
    /// A new spin or any manual orbit cancels the running one; the camera simply continues from where it is.
    /// </summary>
    public void PlayNavigationSpin(int direction)
    {
        if (direction == 0 || Camera == null) return;
        StopSpin();
        float finalYaw = _yawT;
        float angle = Mathf.DegToRad(NavSpinDegrees) * (direction > 0 ? 1f : -1f) * (OpenSpinSign >= 0 ? 1f : -1f);
        _spinning = true;
        _spinFinalYaw = finalYaw;
        _yaw = _yawT = finalYaw - angle;   // start pose; _yaw and _yawT move together so the exp. smoothing never fights the tween
        UpdateCameraTransform();

        _spinTween = CreateTween().SetTrans(Tween.TransitionType.Cubic).SetEase(Tween.EaseType.Out);
        _spinTween.TweenMethod(Callable.From<float>(t => { _yaw = _yawT = finalYaw - angle * (1f - t); }), 0f, 1f, Mathf.Max(0.05f, NavSpinDuration));
        _spinTween.TweenCallback(Callable.From(() => { _yaw = _yawT = finalYaw; _spinning = false; _spinTween = null; }));
    }

    void StopSpin()
    {
        _spinTween?.Kill();
        _spinTween = null;
        _spinning = false;   // yaw stays wherever it is (_yaw == _yawT), no pull-back
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
        if (Camera == null || KeyboardSuspended) return;
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
        if (rot != 0) StopSpin();
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
            switch (mb.ButtonIndex)
            {
                case MouseButton.WheelUp:
                case MouseButton.WheelDown:
                case MouseButton.WheelLeft:
                case MouseButton.WheelRight:
                    if (mb.Pressed) { HandleWheel(mb); GetViewport().SetInputAsHandled(); }
                    break;

                case MouseButton.Left:
                    if (mb.Pressed) { _leftDown = true; _leftTravel = 0f; }   // not consumed: a plain click still reaches WorldInteraction
                    else
                    {
                        _leftDown = false;
                        if (_dragButton == MouseButton.Left) _dragButton = MouseButton.None;
                    }
                    break;

                case MouseButton.Middle:
                case MouseButton.Right:
                    if (mb.Pressed) { _dragButton = mb.ButtonIndex; _dragTravel = 0f; _focusTween?.Kill(); }
                    else if (_dragButton == mb.ButtonIndex)
                    {
                        if (mb.ButtonIndex == MouseButton.Right && _dragTravel < 4f) RightClickWithoutDrag?.Invoke(mb.Position);
                        _dragButton = MouseButton.None;
                    }
                    break;
            }
        }
        else if (@event is InputEventMouseMotion mm)
        {
            // the release may have happened outside the window: never keep dragging without the button held
            if ((_leftDown || _dragButton == MouseButton.Left) && (mm.ButtonMask & MouseButtonMask.Left) == 0)
            {
                _leftDown = false;
                if (_dragButton == MouseButton.Left) _dragButton = MouseButton.None;
            }
            if (_leftDown && _dragButton == MouseButton.None)
            {
                _leftTravel += mm.Relative.Length();
                if (_leftTravel >= DragThreshold) { _dragButton = MouseButton.Left; _focusTween?.Kill(); } // it is a drag now, not a click
            }
            if (_dragButton == MouseButton.None) return;

            _dragTravel += mm.Relative.Length();
            // left-drag orbits; right/middle-drag pan; Shift swaps the two
            bool orbit = _dragButton == MouseButton.Left || (_dragButton == MouseButton.Right && RightDragOrbits);
            if (_dragButton == MouseButton.Left && (mm.CtrlPressed || mm.MetaPressed)) orbit = false; // Ctrl + left-drag always pans
            else if (mm.ShiftPressed) orbit = !orbit;
            if (orbit) Orbit(mm.Relative); else Pan(mm.Relative);
        }
        else if (@event is InputEventMagnifyGesture mg)
        {
            // touchpad pinch: spread = zoom in
            float f = Mathf.Clamp(mg.Factor, 0.5f, 2f);
            if (f > 0.0001f) { ZoomAt(mg.Position, 1f / f); GetViewport().SetInputAsHandled(); }
        }
        else if (@event is InputEventPanGesture pg)
        {
            // touchpad two-finger scroll
            var d = pg.Delta;
            if (pg.ShiftPressed)
                Pan(new Vector2(-d.X, -d.Y) * ScrollDragPixels * 0.5f);                 // Shift + scroll: pan
            else if (Mathf.Abs(d.Y) >= Mathf.Abs(d.X))
                ZoomAt(GetViewport().GetMousePosition(), Mathf.Pow(ZoomStep, Mathf.Clamp(d.Y * GestureZoomSpeed, -2f, 2f))); // up/down: zoom
            else
                Orbit(new Vector2(-d.X * ScrollDragPixels * 0.5f, 0f));                  // sideways: rotate
            GetViewport().SetInputAsHandled();
        }
        else if (@event is InputEventKey key && key.Pressed && !key.Echo && key.Keycode == Key.Home)
        {
            ResetView(FocusBounds / 1.3f);
        }
    }

    /// <summary>
    /// Wheel events from a mouse (one notch = Factor 1) and from touchpads that report two-finger scroll as wheel events
    /// (many small fractional steps). Both go through the same maths, so a notch zooms one step and a touchpad zooms smoothly.
    /// </summary>
    void HandleWheel(InputEventMouseButton mb)
    {
        float f = mb.Factor > 0.0001f ? Mathf.Min(mb.Factor, 4f) : 1f;
        bool vertical = mb.ButtonIndex == MouseButton.WheelUp || mb.ButtonIndex == MouseButton.WheelDown;
        float dir = (mb.ButtonIndex == MouseButton.WheelUp || mb.ButtonIndex == MouseButton.WheelLeft) ? 1f : -1f; // up/left = content moves down/right

        if (mb.ShiftPressed)
        {
            // Shift + scroll: pan (vertical scroll moves forward/back, sideways scroll moves left/right)
            _focusTween?.Kill();
            Pan(vertical ? new Vector2(0f, dir * f * ScrollDragPixels) : new Vector2(dir * f * ScrollDragPixels, 0f));
        }
        else if (vertical)
        {
            // wheel up = zoom in, wheel down = zoom out
            ZoomAt(mb.Position, Mathf.Pow(ZoomStep, -dir * f));
        }
        else
        {
            // sideways scroll: rotate around the focus point
            _focusTween?.Kill();
            Orbit(new Vector2(dir * f * ScrollDragPixels, 0f));
        }
    }

    void Orbit(Vector2 rel)
    {
        StopSpin();
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
        _spinTween?.Kill();
    }
}
