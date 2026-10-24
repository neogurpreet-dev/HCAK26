using System;
using System.Collections.Generic;
using Godot;

namespace Atlas;

/// <summary>
/// Mouse picking for the 3D world (hover / click / double-click on buildings and the overflow beacon).
/// Left button only. A left press that turns into a drag belongs to <see cref="WorldCamera"/> (orbit); only a click
/// (released without moving) selects, so selection and camera motion never fight over the same input.
/// A double-click activates at once. Receives only events not consumed by UI controls.
/// Ctrl+click adds / removes a building from a multi-selection (Selected stays the primary one; the rest are tracked by path).
/// The right button is never handled here: a right-click must not select, open or trigger Ghost Links (see FileCommands).
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
    bool _leftDown;
    float _leftTravel;
    Vector2 _pressPos;
    const float ClickSlop = 5f; // pixels: more movement than this is a camera drag, not a click
    bool _pressCtrl;
    /// <summary>Paths of the additionally selected buildings (multi-selection). Never contains the primary Selected path.</summary>
    readonly List<string> _extraPaths = new();

    public override void _UnhandledInput(InputEvent @event)
    {
        if (Camera == null || Generator == null) return;

        if (@event is InputEventMouseMotion mm)
        {
            if (_leftDown && (mm.ButtonMask & MouseButtonMask.Left) == 0) _leftDown = false; // released outside the window
            if (_leftDown) _leftTravel += mm.Relative.Length();
            if (!(CameraRig?.IsInteracting ?? false))
            {
                var pick = Pick(mm.Position);
                SetHover(pick.building);
                SetPadHover(pick.openAll);
            }
        }
        else if (@event is InputEventMouseButton mb && mb.ButtonIndex == MouseButton.Left)
        {
            if (mb.Pressed)
            {
                if (mb.DoubleClick) { _leftDown = false; HandleClick(mb.Position, true); return; }
                _leftDown = true;
                _leftTravel = 0f;
                _pressPos = mb.Position;
                _pressCtrl = mb.CtrlPressed;
            }
            else if (_leftDown)
            {
                _leftDown = false;
                if (_leftTravel < ClickSlop) HandleClick(_pressPos, false, _pressCtrl); // a drag orbits the camera instead
            }
        }
    }

    void HandleClick(Vector2 screenPos, bool isDouble, bool ctrl = false)
    {
        var pick = Pick(screenPos);
        if (pick.openAll)
        {
            if (!isDouble) OpenAllClicked?.Invoke(); // the second press of a double click must not launch a second time
            return;
        }
        if (pick.overflow) { OverflowClicked?.Invoke(); return; }

        if (pick.building == null)
        {
            if (!ctrl) { ClearExtras(); SetSelected(null); } // Ctrl+click on empty space keeps the selection
            return;
        }

        if (isDouble)
        {
            ActivateRequested?.Invoke(pick.building);
            return;
        }
        if (ctrl) { ToggleInSelection(pick.building); return; }
        ClearExtras();
        SetSelected(pick.building);
        // No camera move on select: a glide would shift the building between the clicks of a double-click.
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

    // ------------------------------------------------------------------ multi-selection

    /// <summary>The building under a screen position (or null). Used by the context menu; does not change hover or selection.</summary>
    public FileBuilding BuildingAt(Vector2 screenPos) => Camera == null || Generator == null ? null : Pick(screenPos).building;

    public bool IsInSelection(FileBuilding b)
        => b != null && (b == Selected || (b.Entry != null && _extraPaths.Contains(b.Entry.Path)));

    /// <summary>Entries of every selected building, primary first.</summary>
    public List<FsEntry> SelectedEntries()
    {
        var list = new List<FsEntry>();
        if (Selected != null && !IsGone(Selected) && Selected.Entry != null) list.Add(Selected.Entry);
        foreach (var p in _extraPaths)
        {
            var b = Generator?.FindBuilding(p);
            if (b != null && !IsGone(b) && b.Entry != null) list.Add(b.Entry);
        }
        return list;
    }

    void ToggleInSelection(FileBuilding b)
    {
        string path = b?.Entry?.Path;
        if (path == null) return;
        if (Selected == null) { SetSelected(b); return; }
        if (Selected == b)
        {
            // drop the primary; promote the first extra (if any) so the selection stays one consistent set
            if (_extraPaths.Count > 0)
            {
                string next = _extraPaths[0];
                _extraPaths.RemoveAt(0);
                SetSelected(Generator?.FindBuilding(next));
            }
            else SetSelected(null);
            return;
        }
        if (_extraPaths.Remove(path)) b.SetSelected(false);
        else { _extraPaths.Add(path); b.SetSelected(true); }
    }

    void ClearExtras()
    {
        foreach (var p in _extraPaths) Generator?.FindBuilding(p)?.SetSelected(false);
        _extraPaths.Clear();
    }

    /// <summary>Selects every building currently shown (Ctrl+A).</summary>
    public void SelectAll()
    {
        if (Generator == null) return;
        var all = new List<FileBuilding>();
        foreach (var b in Generator.Buildings)
            if (b != null && !IsGone(b) && b.Entry != null) all.Add(b);
        ClearExtras();
        SetSelected(all.Count > 0 ? all[0] : null);
        for (int i = 1; i < all.Count; i++) { _extraPaths.Add(all[i].Entry.Path); all[i].SetSelected(true); }
    }

    /// <summary>After a rebuild the buildings are new objects: find the extras again by path and drop the ones that are gone.</summary>
    void ReresolveExtras()
    {
        for (int i = _extraPaths.Count - 1; i >= 0; i--)
        {
            var b = Generator?.FindBuilding(_extraPaths[i]);
            if (b == null || IsGone(b)) _extraPaths.RemoveAt(i);
            else b.SetSelected(true);
        }
    }

    static bool IsGone(Node n) => !IsInstanceValid(n) || n.IsQueuedForDeletion();

    /// <summary>Drops stale references after the world is rebuilt.</summary>
    public void ClearStale()
    {
        ReresolveExtras();
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
