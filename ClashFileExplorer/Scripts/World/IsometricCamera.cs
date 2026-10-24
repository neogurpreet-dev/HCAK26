using System;
using System.Collections.Generic;
using Godot;

namespace ClashExplorer.World;

/// <summary>
/// Fixed-angle 2D camera for the isometric world (attach to the Camera2D node).
///
/// Controls: middle- or right-mouse drag pans, wheel zooms toward the cursor.
/// Everything is smoothed by this script; Camera2D's own smoothing stays off.
///
/// Conflict rules:
///  - Input is read in _UnhandledInput, so UI panels in front of the world keep their clicks and wheel.
///  - A right-button press only becomes a pan after moving ClickDragThreshold pixels. A right
///    click that never exceeded it raises ContextClickRequested instead (used by the Part 2 context menu).
///  - Any manual pan or zoom immediately stops a running focus tween, and a new focus replaces the old
///    one, so animations never compete.
/// </summary>
public partial class IsometricCamera : Camera2D
{
    /// <summary>Right button released without dragging: open a context menu here.</summary>
    [Signal] public delegate void ContextClickRequestedEventHandler(Vector2 screenPosition, Vector2 worldPosition);

    /// <summary>The zoom level changed (for labels and ghost links that adapt to zoom).</summary>
    [Signal] public delegate void ZoomChangedEventHandler(float zoom);

    private const int MaxRememberedViews = 64;

    [ExportGroup("Zoom")]
    [Export(PropertyHint.Range, "0.02,1,0.01")] public float MinZoom { get; set; } = 0.12f;
    [Export(PropertyHint.Range, "1,8,0.05")] public float MaxZoom { get; set; } = 2.5f;
    /// <summary>Multiplier per wheel notch.</summary>
    [Export(PropertyHint.Range, "1.02,1.6,0.01")] public float ZoomStep { get; set; } = 1.15f;
    [Export(PropertyHint.Range, "1,40,0.5")] public float ZoomSmoothing { get; set; } = 14f;

    [ExportGroup("Pan")]
    [Export] public bool PanWithRightMouse { get; set; } = true;
    [Export(PropertyHint.Range, "1,40,0.5")] public float PanSmoothing { get; set; } = 12f;
    /// <summary>Pixels the mouse must travel before a button press counts as a drag.</summary>
    [Export(PropertyHint.Range, "0,30,1")] public float ClickDragThreshold { get; set; } = 6f;
    /// <summary>How far past the edge of the world the view center may travel.</summary>
    [Export(PropertyHint.Range, "0,3000,10")] public float PanMargin { get; set; } = 600f;

    [ExportGroup("Focus")]
    /// <summary>Zoom multiplier applied when a building is selected.</summary>
    [Export(PropertyHint.Range, "1,2,0.01")] public float SelectionZoomIn { get; set; } = 1.15f;
    /// <summary>Selection never zooms in beyond this level.</summary>
    [Export(PropertyHint.Range, "0.3,4,0.05")] public float SelectionMaxZoom { get; set; } = 1.5f;
    [Export(PropertyHint.Range, "0,3,0.05")] public float FocusSeconds { get; set; } = 0.55f;
    [Export(PropertyHint.Range, "0,3,0.05")] public float NavigateSeconds { get; set; } = 0.7f;
    /// <summary>Fraction of the screen the world fills when framing a directory.</summary>
    [Export(PropertyHint.Range, "0.3,1,0.01")] public float FitFill { get; set; } = 0.85f;
    [Export(PropertyHint.Range, "0.2,2,0.05")] public float FitMaxZoom { get; set; } = 1.0f;

    private readonly Dictionary<string, (Vector2 Position, float Zoom)> _views =
        new(StringComparer.OrdinalIgnoreCase);

    private Tween? _focusTween;
    private bool _focusActive;
    private Vector2 _targetPosition;
    private float _targetZoom = 1f;
    private float _lastEmittedZoom = -1f;

    private bool _anchorActive;
    private Vector2 _anchorWorld;
    private Vector2 _anchorScreen;

    private bool _dragging;
    private bool _dragExceeded;
    private float _dragDistance;
    private Vector2 _dragPending;
    private MouseButton _dragButton;

    private Rect2 _worldBounds = new Rect2(-2000f, -2000f, 4000f, 4000f);
    private bool _hasBounds;
    private string _currentViewKey = "";

    private Vector2 ViewSize => GetViewportRect().Size;

    public override void _Ready()
    {
        PositionSmoothingEnabled = false;
        _targetPosition = GlobalPosition;
        _targetZoom = Mathf.Clamp(Zoom.X, MinZoom, MaxZoom);
        Zoom = new Vector2(_targetZoom, _targetZoom);
    }

    public override void _ExitTree()
    {
        StopFocus();
    }

    public override void _Notification(int what)
    {
        if (what == NotificationWMWindowFocusOut) CancelDrag();
    }

    // ------------------------------------------------------------------ public API

    /// <summary>Screen (viewport) coordinates to world coordinates.</summary>
    public Vector2 ScreenToWorld(Vector2 screenPoint)
    {
        return GlobalPosition + (screenPoint - ViewSize * 0.5f) / Zoom.X;
    }

    /// <summary>Limits where the view center may go; call after every layout change.</summary>
    public void SetWorldBounds(Rect2 bounds)
    {
        if (bounds.Size.X <= 1f || bounds.Size.Y <= 1f) return;
        _worldBounds = bounds;
        _hasBounds = true;
    }

    /// <summary>Smoothly centers on a world point, optionally changing the zoom (seconds &lt; 0 uses FocusSeconds).</summary>
    public void FocusOn(Vector2 worldPoint, float zoom = -1f, double seconds = -1.0)
    {
        float z = zoom > 0f ? zoom : _targetZoom;
        StartTween(ClampToWorld(worldPoint), z, seconds < 0.0 ? FocusSeconds : seconds);
    }

    /// <summary>Selection focus: centers on the building with a small, capped zoom-in.</summary>
    public void FocusOnBuilding(FileBuilding building)
    {
        if (!IsInstanceValid(building)) return;
        Vector2 center = building.GlobalPosition + new Vector2(0f, -building.TextureSize.Y * building.Scale.Y * 0.5f);
        float current = _targetZoom;
        float boosted = Mathf.Min(current * SelectionZoomIn, SelectionMaxZoom);
        FocusOn(center, Mathf.Max(current, boosted));
    }

    /// <summary>Frames a rectangle of the world (used after loading a directory and by "reset view").</summary>
    public void FitBounds(Rect2 bounds, bool animate = true)
    {
        Vector2 view = ViewSize;
        float zoom = 1f;
        Vector2 center = Vector2.Zero;
        if (bounds.Size.X > 1f && bounds.Size.Y > 1f && view.X > 1f && view.Y > 1f)
        {
            float fit = Mathf.Min(view.X / bounds.Size.X, view.Y / bounds.Size.Y) * FitFill;
            zoom = Mathf.Min(fit, Mathf.Min(FitMaxZoom, MaxZoom));
            center = bounds.GetCenter();
        }
        zoom = Mathf.Clamp(zoom, MinZoom, MaxZoom);
        StartTween(ClampToWorld(center), zoom, animate ? NavigateSeconds : 0.0);
    }

    /// <summary>
    /// Call when a directory finished loading. Remembers the view of the directory just left and
    /// restores the saved view of the new one if it was visited before; otherwise frames its bounds.
    /// Returns true when a saved view was restored.
    /// </summary>
    public bool EnterDirectory(string path, Rect2 bounds, bool animate = true)
    {
        bool hadPreviousDirectory = _currentViewKey.Length > 0;
        SaveCurrentView();
        _currentViewKey = path;
        SetWorldBounds(bounds);

        if (path.Length > 0 && _views.TryGetValue(path, out (Vector2 Position, float Zoom) saved))
        {
            StartTween(ClampToWorld(saved.Position), Mathf.Clamp(saved.Zoom, MinZoom, MaxZoom),
                animate ? NavigateSeconds : 0.0);
            return true;
        }

        FitBounds(bounds, animate && hadPreviousDirectory);
        return false;
    }

    // ------------------------------------------------------------------ input

    public override void _UnhandledInput(InputEvent @event)
    {
        switch (@event)
        {
            case InputEventMouseButton button:
                HandleMouseButton(button);
                break;
            case InputEventMouseMotion motion:
                HandleMouseMotion(motion);
                break;
        }
    }

    private void HandleMouseButton(InputEventMouseButton button)
    {
        if (button.ButtonIndex == MouseButton.WheelUp || button.ButtonIndex == MouseButton.WheelDown)
        {
            if (!button.Pressed) return;
            float factor = button.ButtonIndex == MouseButton.WheelUp ? ZoomStep : 1f / ZoomStep;
            ZoomAt(button.Position, factor);
            GetViewport().SetInputAsHandled();
            return;
        }

        bool isPanButton = button.ButtonIndex == MouseButton.Middle
                           || (PanWithRightMouse && button.ButtonIndex == MouseButton.Right);
        if (!isPanButton) return;

        if (button.Pressed)
        {
            _dragging = true;
            _dragExceeded = false;
            _dragDistance = 0f;
            _dragPending = Vector2.Zero;
            _dragButton = button.ButtonIndex;
            return;
        }

        if (_dragging && button.ButtonIndex == _dragButton)
        {
            bool wasClick = !_dragExceeded;
            _dragging = false;
            _dragExceeded = false;
            if (wasClick && button.ButtonIndex == MouseButton.Right)
            {
                EmitSignal(SignalName.ContextClickRequested, button.Position, ScreenToWorld(button.Position));
            }
        }
    }

    private void HandleMouseMotion(InputEventMouseMotion motion)
    {
        if (!_dragging) return;

        _dragDistance += motion.Relative.Length();
        _dragPending += motion.Relative;
        if (!_dragExceeded && _dragDistance < ClickDragThreshold) return;

        if (!_dragExceeded)
        {
            _dragExceeded = true;
            StopFocus();
            _anchorActive = false;
        }

        Vector2 target = ClampToWorld(GlobalPosition - _dragPending / Zoom.X);
        _dragPending = Vector2.Zero;
        GlobalPosition = target;
        _targetPosition = target;
        GetViewport().SetInputAsHandled();
    }

    private void CancelDrag()
    {
        _dragging = false;
        _dragExceeded = false;
    }

    private void ZoomAt(Vector2 screenPoint, float factor)
    {
        StopFocus();
        float newTarget = Mathf.Clamp(_targetZoom * factor, MinZoom, MaxZoom);
        if (Mathf.IsEqualApprox(newTarget, _targetZoom)) return;

        // Keep the world point under the cursor fixed while the zoom animates.
        _anchorWorld = ScreenToWorld(screenPoint);
        _anchorScreen = screenPoint;
        _anchorActive = true;
        _targetZoom = newTarget;
    }

    // ------------------------------------------------------------------ per-frame smoothing

    public override void _Process(double delta)
    {
        if (!_focusActive)
        {
            float dt = (float)delta;
            float zoom = Zoom.X;

            if (!Mathf.IsEqualApprox(zoom, _targetZoom))
            {
                float k = 1f - Mathf.Exp(-ZoomSmoothing * dt);
                float next = Mathf.Lerp(zoom, _targetZoom, k);
                if (Mathf.Abs(next - _targetZoom) < 0.0005f) next = _targetZoom;
                zoom = next;
                Zoom = new Vector2(zoom, zoom);

                if (_anchorActive)
                {
                    GlobalPosition = ClampToWorld(_anchorWorld - (_anchorScreen - ViewSize * 0.5f) / zoom);
                    _targetPosition = GlobalPosition;
                }
            }
            else
            {
                _anchorActive = false;
            }

            if (!_anchorActive && !_targetPosition.IsEqualApprox(GlobalPosition))
            {
                float k = 1f - Mathf.Exp(-PanSmoothing * dt);
                Vector2 next = GlobalPosition.Lerp(_targetPosition, k);
                if (next.DistanceTo(_targetPosition) < 0.05f) next = _targetPosition;
                GlobalPosition = next;
            }
        }

        float current = Zoom.X;
        if (!Mathf.IsEqualApprox(current, _lastEmittedZoom))
        {
            _lastEmittedZoom = current;
            EmitSignal(SignalName.ZoomChanged, current);
        }
    }

    // ------------------------------------------------------------------ tweening

    private void StartTween(Vector2 position, float zoom, double seconds)
    {
        StopFocus();
        _anchorActive = false;
        _targetPosition = position;
        _targetZoom = zoom;

        if (seconds <= 0.0 || !IsInsideTree())
        {
            GlobalPosition = position;
            Zoom = new Vector2(zoom, zoom);
            return;
        }

        _focusActive = true;
        _focusTween = CreateTween();
        _focusTween.SetTrans(Tween.TransitionType.Cubic).SetEase(Tween.EaseType.Out).SetParallel(true);
        _focusTween.TweenProperty(this, "global_position", position, seconds);
        _focusTween.TweenProperty(this, "zoom", new Vector2(zoom, zoom), seconds);
        _focusTween.Finished += OnFocusFinished;
    }

    private void OnFocusFinished()
    {
        _focusActive = false;
        _focusTween = null;
        GlobalPosition = _targetPosition;
        Zoom = new Vector2(_targetZoom, _targetZoom);
    }

    /// <summary>Stops a running focus tween and adopts the current view as the new target.</summary>
    private void StopFocus()
    {
        if (_focusTween != null)
        {
            if (IsInstanceValid(_focusTween))
            {
                _focusTween.Finished -= OnFocusFinished;
                _focusTween.Kill();
            }
            _focusTween = null;
        }
        if (_focusActive)
        {
            _focusActive = false;
            _targetPosition = GlobalPosition;
            _targetZoom = Mathf.Clamp(Zoom.X, MinZoom, MaxZoom);
        }
    }

    // ------------------------------------------------------------------ helpers

    private void SaveCurrentView()
    {
        if (_currentViewKey.Length == 0) return;
        _views[_currentViewKey] = (_targetPosition, _targetZoom);
        if (_views.Count > MaxRememberedViews)
        {
            // Drop an arbitrary old entry; the cache is only a convenience.
            using var e = _views.Keys.GetEnumerator();
            if (e.MoveNext()) _views.Remove(e.Current);
        }
    }

    private Vector2 ClampToWorld(Vector2 point)
    {
        if (!_hasBounds) return point;
        Rect2 limit = _worldBounds.Grow(PanMargin);
        return new Vector2(
            Mathf.Clamp(point.X, limit.Position.X, limit.End.X),
            Mathf.Clamp(point.Y, limit.Position.Y, limit.End.Y));
    }
}
