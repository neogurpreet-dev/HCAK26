using System;
using Godot;

namespace Atlas;

/// <summary>
/// Mouse picking for the 3D world (hover / click / double-click on buildings and the overflow beacon).
/// Left button only: middle/right drags belong to <see cref="WorldCamera"/>, so selection, camera motion
/// and activation never fight over the same input. Receives only events not consumed by UI controls.
/// </summary>
public partial class WorldInteraction : Node
{
    public Camera3D Camera { get; set; }
    public WorldGenerator Generator { get; set; }
    public WorldCamera CameraRig { get; set; }

    public FileBuilding Hovered { get; private set; }
    public FileBuilding Selected { get; private set; }

    public event Action<FileBuilding> HoverChanged;
    public event Action<FileBuilding> SelectionChanged;
    /// <summary>Double click on a building (folders navigate, files open).</summary>
    public event Action<FileBuilding> ActivateRequested;
    public event Action OverflowClicked;
    /// <summary>Click on the Open All pad of the Ghost Links platform.</summary>
    public event Action OpenAllClicked;
    /// <summary>Pointer entered / left the Open All pad.</summary>
    public event Action<bool> OpenAllHoverChanged;

    const uint BuildingMask = 2;
    const uint OverflowMask = 4;
    const uint GhostMask = 8; // layer 4 "GhostUI": the Open All pad
    bool _padHover;

    public override void _UnhandledInput(InputEvent @event)
    {
        if (Camera == null || Generator == null) return;

        if (@event is InputEventMouseMotion mm && !(CameraRig?.IsInteracting ?? false))
        {
            var pick = Pick(mm.Position);
            SetHover(pick.building);
            SetPadHover(pick.openAll);
        }
        else if (@event is InputEventMouseButton mb && mb.ButtonIndex == MouseButton.Left && mb.Pressed)
        {
            var pick = Pick(mb.Position);
            if (pick.openAll)
            {
                if (!mb.DoubleClick) OpenAllClicked?.Invoke(); // the second press of a double click must not launch a second time
                return;
            }
            if (pick.overflow) { OverflowClicked?.Invoke(); return; }

            if (pick.building == null)
            {
                SetSelected(null);
                return;
            }

            if (mb.DoubleClick)
            {
                ActivateRequested?.Invoke(pick.building);
                return;
            }
            SetSelected(pick.building);
            CameraRig?.FocusOn(pick.building.GlobalPosition + Vector3.Up * (pick.building.Height * 0.5f), 1f); // select without changing zoom
        }
    }

    (FileBuilding building, bool overflow, bool openAll) Pick(Vector2 screenPos)
    {
        var world = Camera.GetWorld3D();
        if (world == null) return (null, false, false);
        var from = Camera.ProjectRayOrigin(screenPos);
        var to = from + Camera.ProjectRayNormal(screenPos) * 1000f;
        var query = PhysicsRayQueryParameters3D.Create(from, to, BuildingMask | OverflowMask | GhostMask);
        query.CollideWithAreas = true;
        query.CollideWithBodies = false;
        var hit = world.DirectSpaceState.IntersectRay(query);
        if (hit.Count == 0) return (null, false, false);

        var collider = hit["collider"].AsGodotObject() as Node;
        if (collider == null) return (null, false, false);
        if (collider.HasMeta("ghost_open_all")) return (null, false, true);
        if (collider.HasMeta("overflow")) return (null, true, false);
        return (collider.GetParent() as FileBuilding, false, false);
    }

    void SetHover(FileBuilding b)
    {
        if (b == Hovered) return;
        if (Hovered != null && !IsGone(Hovered)) Hovered.SetHover(false);
        Hovered = b;
        Hovered?.SetHover(true);
        Input.SetDefaultCursorShape(b != null ? Input.CursorShape.PointingHand : Input.CursorShape.Arrow);
        HoverChanged?.Invoke(b);
    }

    void SetPadHover(bool on)
    {
        if (on) Input.SetDefaultCursorShape(Input.CursorShape.PointingHand);
        if (on == _padHover) return;
        _padHover = on;
        if (!on && Hovered == null) Input.SetDefaultCursorShape(Input.CursorShape.Arrow);
        OpenAllHoverChanged?.Invoke(on);
    }

    public void SetSelected(FileBuilding b)
    {
        if (b == Selected) return;
        Selected = b;
        Generator?.SelectBuilding(b);
        SelectionChanged?.Invoke(b);
    }

    static bool IsGone(Node n) => !IsInstanceValid(n) || n.IsQueuedForDeletion();

    /// <summary>Drops stale references after the world is rebuilt.</summary>
    public void ClearStale()
    {
        if (Hovered != null && IsGone(Hovered)) { Hovered = null; HoverChanged?.Invoke(null); }
        if (Selected != null && IsGone(Selected))
        {
            // re-resolve by path in the rebuilt world
            var path = Selected.Entry?.Path;
            Selected = null;
            var fresh = path != null ? Generator?.FindBuilding(path) : null;
            if (fresh != null) Selected = fresh;
            SelectionChanged?.Invoke(Selected);
        }
    }
}
