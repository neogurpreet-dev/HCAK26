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

    const uint BuildingMask = 2;
    const uint OverflowMask = 4;

    public override void _UnhandledInput(InputEvent @event)
    {
        if (Camera == null || Generator == null) return;

        if (@event is InputEventMouseMotion mm && !(CameraRig?.IsInteracting ?? false))
        {
            var pick = Pick(mm.Position);
            SetHover(pick.building);
        }
        else if (@event is InputEventMouseButton mb && mb.ButtonIndex == MouseButton.Left && mb.Pressed)
        {
            var pick = Pick(mb.Position);
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
            CameraRig?.FocusOn(pick.building.GlobalPosition + Vector3.Up * (pick.building.Height * 0.5f), CameraRig.SelectZoomFactor);
        }
    }

    (FileBuilding building, bool overflow) Pick(Vector2 screenPos)
    {
        var world = Camera.GetWorld3D();
        if (world == null) return (null, false);
        var from = Camera.ProjectRayOrigin(screenPos);
        var to = from + Camera.ProjectRayNormal(screenPos) * 1000f;
        var query = PhysicsRayQueryParameters3D.Create(from, to, BuildingMask | OverflowMask);
        query.CollideWithAreas = true;
        query.CollideWithBodies = false;
        var hit = world.DirectSpaceState.IntersectRay(query);
        if (hit.Count == 0) return (null, false);

        var collider = hit["collider"].AsGodotObject() as Node;
        if (collider == null) return (null, false);
        if (collider.HasMeta("overflow")) return (null, true);
        return (collider.GetParent() as FileBuilding, false);
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
