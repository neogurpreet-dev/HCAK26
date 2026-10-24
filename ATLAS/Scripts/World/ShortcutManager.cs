using System;
using System.Collections.Generic;
using Godot;

namespace Atlas;

/// <summary>
/// Single place for every keyboard shortcut. Each shortcut is an InputMap action ("atlas_*") registered at start-up, so it is
/// visible to Godot's input system and can be rebound in Project Settings (actions already present in project.godot are left
/// exactly as the project defines them). Dispatch happens in _UnhandledInput, i.e. only for keys no UI control consumed, and is
/// additionally refused while a text field has focus or while <see cref="IsBlocked"/> says a menu / dialog owns the keyboard,
/// so typing in the path bar, search box or rename field never triggers a file command.
///
/// Bindings are matched with exactMatch = true: "Delete" does not fire for Shift+Delete, "Ctrl+N"-style actions do not fire
/// for Ctrl+Shift+N, and so on.
/// </summary>
public partial class ShortcutManager : Node
{
    sealed class Binding
    {
        public string Action;
        public Action Run;
    }

    readonly List<Binding> _bindings = new();

    /// <summary>Return true while a context menu or dialog is open (they own the keyboard).</summary>
    public Func<bool> IsBlocked { get; set; }

    /// <summary>Helper that builds a key event for InputMap (modifiers must match exactly).</summary>
    public static InputEventKey K(Key key, bool ctrl = false, bool shift = false, bool alt = false)
        => new InputEventKey { Keycode = key, CtrlPressed = ctrl, ShiftPressed = shift, AltPressed = alt };

    /// <summary>Registers (or adopts) the InputMap action and attaches the handler. Safe to call more than once per action name.</summary>
    public void Bind(string action, Action run, params InputEventKey[] defaults)
    {
        if (!InputMap.HasAction(action))
        {
            InputMap.AddAction(action);
            foreach (var e in defaults) InputMap.ActionAddEvent(action, e);
        }
        _bindings.RemoveAll(b => b.Action == action);
        _bindings.Add(new Binding { Action = action, Run = run });
    }

    public override void _UnhandledInput(InputEvent @event)
    {
        // A click on the 3D world (not consumed by any HUD control) drops keyboard focus from HUD buttons / text fields, so that
        // Enter opens the selected building instead of re-pressing the button that was clicked last.
        if (@event is InputEventMouseButton mb && mb.Pressed && (mb.ButtonIndex == MouseButton.Left || mb.ButtonIndex == MouseButton.Right))
        {
            GetViewport().GuiReleaseFocus();
            return;
        }

        if (@event is not InputEventKey k || !k.Pressed || k.Echo) return;
        if (IsBlocked != null && IsBlocked()) return;

        var focus = GetViewport().GuiGetFocusOwner();
        if (focus is LineEdit || focus is TextEdit) return;   // never act while the user is typing

        foreach (var b in _bindings)
        {
            if (!k.IsActionPressed(b.Action, false, true)) continue;
            GetViewport().SetInputAsHandled();
            b.Run?.Invoke();
            return;
        }
    }
}
