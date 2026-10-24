using System;
using System.Collections.Generic;
using Godot;

namespace Atlas;

/// <summary>One row of a context menu. Plain data: the menu never looks at files, FileCommands decides what to show.</summary>
public sealed class GlassMenuItem
{
    public string Text = "";
    public string Icon;
    public string Shortcut;
    public Action OnClick;
    public bool Disabled, IsSeparator, IsHeader, Checked, Danger;
    public List<GlassMenuItem> Children;

    public bool HasChildren => Children != null && Children.Count > 0;
    public bool Selectable => !IsSeparator && !IsHeader && !Disabled;

    public static GlassMenuItem Separator() => new GlassMenuItem { IsSeparator = true };
    public static GlassMenuItem Header(string text) => new GlassMenuItem { Text = text, IsHeader = true };
}

/// <summary>
/// ATLAS glass context menu: a screen-space popup (lives in the HUD CanvasLayer, so it never moves with the 3D camera and has
/// the same size at every zoom level). Panels use the shared frosted-glass shader via AtlasTheme.GlassPanel, theme colours and
/// IconLib icons. One level of submenu. Closed = invisible, nothing left behind.
///
/// Dismissal is centralised in _Input (which runs before any GUI or world handler, so it cannot be bypassed by event
/// propagation): any press of the left, right or middle button OUTSIDE the menu and its submenu closes it. A left click is
/// swallowed (like Windows); a right click is passed on so it can reopen elsewhere. The menu also closes when the window loses
/// focus or is resized, and MainWorld closes it on navigation and when a dialog opens. Clicks inside the menu never reach the
/// outside-click logic. A submenu stays open while the pointer travels from its parent row (short grace period).
/// Up/Down/Left/Right/Enter/Space/Escape work from the keyboard. Wheel and mouse motion keep reaching the world.
/// </summary>
public partial class ContextMenuView : Control
{
    sealed class Row
    {
        public GlassMenuItem Item;
        public PanelContainer Ctl;           // null for separators / headers
        public StyleBoxFlat Normal, Hover;
    }

    sealed class MenuPanel
    {
        public PanelContainer Holder;
        public readonly List<Row> Rows = new();
        public int Index = -1;
    }

    public Shader Glass { get; set; }
    public bool IsOpen => _main != null;
    /// <summary>Raised when the menu opens (true) or closes (false). MainWorld uses it to pause camera keyboard control.</summary>
    public event Action<bool> OpenChanged;

    const float MinWidth = 236f;
    const float Edge = 6f;

    MenuPanel _main, _sub, _active;
    int _subOwner = -1;
    double _closeSubAt = -1;   // seconds; > 0 while a submenu is waiting to close after the pointer left its parent row

    public override void _Ready()
    {
        // This control no longer takes part in mouse handling at all (it only parents the panels); dismissal lives in _Input.
        SetAnchorsPreset(Control.LayoutPreset.FullRect);
        MouseFilter = MouseFilterEnum.Ignore;
        Visible = false;
        GetViewport().SizeChanged += () => { if (IsOpen) Close(); };
    }

    public override void _Notification(int what)
    {
        if ((what == NotificationApplicationFocusOut || what == NotificationWMWindowFocusOut) && IsOpen) Close();
    }

    public override void _Process(double delta)
    {
        if (_closeSubAt < 0) return;
        double now = Time.GetTicksMsec() / 1000.0;
        if (now < _closeSubAt) return;
        _closeSubAt = -1;
        if (_sub != null && !_sub.Holder.GetGlobalRect().HasPoint(GetGlobalMousePosition())) CloseSub();
    }

    // ------------------------------------------------------------------ open / close

    public void Open(Vector2 screenPos, List<GlassMenuItem> items, bool selectFirst = false)
    {
        Close();
        if (items == null || items.Count == 0) return;

        _main = BuildPanel(items);
        AddChild(_main.Holder);
        Visible = true;
        var size = _main.Holder.GetCombinedMinimumSize();
        _main.Holder.Size = size;
        _main.Holder.Position = Place(screenPos, size);
        _active = _main;
        FadeIn(_main.Holder);
        if (selectFirst) Move(+1);
        OpenChanged?.Invoke(true);
    }

    public void Close()
    {
        if (_main == null) return;
        CloseSub();
        _main.Holder.Visible = false;
        _main.Holder.QueueFree();
        _main = null;
        _active = null;
        Visible = false;
        OpenChanged?.Invoke(false);
    }

    void CloseSub()
    {
        if (_sub != null)
        {
            _sub.Holder.Visible = false;
            _sub.Holder.QueueFree();
            _sub = null;
        }
        _subOwner = -1;
        _closeSubAt = -1;
        if (_main != null) _active = _main;
    }

    bool InsideMenu(Vector2 p)
        => (_main != null && _main.Holder.GetGlobalRect().HasPoint(p)) || (_sub != null && _sub.Holder.GetGlobalRect().HasPoint(p));

    /// <summary>Keeps a popup of the given size fully inside the viewport; flips to the left of / above the cursor when needed.</summary>
    Vector2 Place(Vector2 want, Vector2 size)
    {
        var vp = GetViewportRect().Size;
        float x = want.X, y = want.Y;
        if (x + size.X > vp.X - Edge) x = want.X - size.X;
        if (x < Edge) x = Mathf.Max(Edge, vp.X - size.X - Edge);
        if (y + size.Y > vp.Y - Edge) y = vp.Y - size.Y - Edge;
        if (y < Edge) y = Edge;
        return new Vector2(x, y);
    }

    void FadeIn(Control c)
    {
        c.Modulate = new Color(1, 1, 1, 0);
        var t = CreateTween();
        t.TweenProperty(c, "modulate:a", 1f, 0.09f);
    }

    // ------------------------------------------------------------------ building

    MenuPanel BuildPanel(List<GlassMenuItem> items)
    {
        var mp = new MenuPanel();
        var holder = new PanelContainer { CustomMinimumSize = new Vector2(MinWidth, 0), MouseFilter = MouseFilterEnum.Stop };
        var shadow = new StyleBoxFlat { BgColor = new Color(0, 0, 0, 0), ShadowColor = new Color(0, 0, 0, 0.40f), ShadowSize = 18, ShadowOffset = new Vector2(0, 6) };
        shadow.SetCornerRadiusAll(12);
        holder.AddThemeStyleboxOverride("panel", shadow);

        var glass = AtlasTheme.GlassPanel(Glass, 12);
        holder.AddChild(glass);
        var margin = new MarginContainer();
        foreach (var side in new[] { "margin_left", "margin_right", "margin_top", "margin_bottom" }) margin.AddThemeConstantOverride(side, 6);
        glass.AddChild(margin);
        var v = new VBoxContainer();
        v.AddThemeConstantOverride("separation", 1);
        margin.AddChild(v);
        mp.Holder = holder;

        for (int i = 0; i < items.Count; i++)
        {
            int idx = i;
            var it = items[i];

            if (it.IsSeparator)
            {
                var wrap = new MarginContainer { MouseFilter = MouseFilterEnum.Ignore };
                wrap.AddThemeConstantOverride("margin_left", 8);
                wrap.AddThemeConstantOverride("margin_right", 8);
                wrap.AddThemeConstantOverride("margin_top", 4);
                wrap.AddThemeConstantOverride("margin_bottom", 4);
                wrap.AddChild(new ColorRect { Color = new Color(1, 1, 1, 0.10f), CustomMinimumSize = new Vector2(0, 1), MouseFilter = MouseFilterEnum.Ignore });
                v.AddChild(wrap);
                mp.Rows.Add(new Row { Item = it });
                continue;
            }

            if (it.IsHeader)
            {
                var wrap = new MarginContainer { MouseFilter = MouseFilterEnum.Ignore };
                wrap.AddThemeConstantOverride("margin_left", 10);
                wrap.AddThemeConstantOverride("margin_right", 10);
                wrap.AddThemeConstantOverride("margin_top", 5);
                wrap.AddThemeConstantOverride("margin_bottom", 5);
                wrap.AddChild(AtlasTheme.MakeLabel(it.Text, 12, AtlasTheme.TextDim));
                v.AddChild(wrap);
                mp.Rows.Add(new Row { Item = it });
                continue;
            }

            var normal = AtlasTheme.Box(new Color(0, 0, 0, 0), new Color(0, 0, 0, 0), 8, 0, 10, 6);
            var hover = AtlasTheme.Box(new Color(0.13f, 0.60f, 0.85f, 0.22f), new Color(0.13f, 0.83f, 0.93f, 0.28f), 8, 1, 10, 6);
            var ctl = new PanelContainer { MouseFilter = MouseFilterEnum.Stop, CustomMinimumSize = new Vector2(0, 32) };
            ctl.AddThemeStyleboxOverride("panel", normal);

            Color fg = it.Disabled ? new Color(AtlasTheme.TextDim, 0.5f) : it.Danger ? new Color("F87171") : AtlasTheme.Text;
            var h = new HBoxContainer { MouseFilter = MouseFilterEnum.Ignore };
            h.AddThemeConstantOverride("separation", 10);

            var icon = new TextureRect
            {
                CustomMinimumSize = new Vector2(18, 18),
                ExpandMode = TextureRect.ExpandModeEnum.IgnoreSize,
                StretchMode = TextureRect.StretchModeEnum.KeepAspectCentered,
                SizeFlagsVertical = Control.SizeFlags.ShrinkCenter,
                MouseFilter = MouseFilterEnum.Ignore,
            };
            string iconName = it.Checked ? "check" : it.Icon;
            if (iconName != null) icon.Texture = IconLib.Get(iconName, it.Checked ? AtlasTheme.Cyan : fg, 18, 1.8f);
            h.AddChild(icon);

            var label = AtlasTheme.MakeLabel(it.Text, 14, fg);
            label.SizeFlagsHorizontal = Control.SizeFlags.ExpandFill;
            h.AddChild(label);

            if (it.HasChildren)
            {
                h.AddChild(new TextureRect
                {
                    Texture = IconLib.Get("forward", AtlasTheme.TextDim, 14, 2f),
                    CustomMinimumSize = new Vector2(14, 14),
                    ExpandMode = TextureRect.ExpandModeEnum.IgnoreSize,
                    StretchMode = TextureRect.StretchModeEnum.KeepAspectCentered,
                    SizeFlagsVertical = Control.SizeFlags.ShrinkCenter,
                    MouseFilter = MouseFilterEnum.Ignore,
                });
            }
            else if (!string.IsNullOrEmpty(it.Shortcut))
            {
                h.AddChild(AtlasTheme.MakeLabel(it.Shortcut, 12, AtlasTheme.TextDim));
            }
            ctl.AddChild(h);
            v.AddChild(ctl);

            mp.Rows.Add(new Row { Item = it, Ctl = ctl, Normal = normal, Hover = hover });

            if (!it.Disabled)
            {
                ctl.MouseEntered += () => OnHover(mp, idx);
                ctl.GuiInput += ev =>
                {
                    // activate on release, and only if the release is still over this row
                    if (ev is InputEventMouseButton mb && mb.ButtonIndex == MouseButton.Left && !mb.Pressed && ctl.GetGlobalRect().HasPoint(mb.GlobalPosition))
                        Activate(mp, idx);
                };
            }
        }
        return mp;
    }

    // ------------------------------------------------------------------ highlight / submenu / activation

    void SetIndex(MenuPanel p, int i)
    {
        p.Index = i;
        for (int j = 0; j < p.Rows.Count; j++)
        {
            var r = p.Rows[j];
            if (r.Ctl == null) continue;
            r.Ctl.AddThemeStyleboxOverride("panel", j == i ? r.Hover : r.Normal);
        }
    }

    void OnHover(MenuPanel p, int idx)
    {
        if (p != _main && p != _sub) return;
        _active = p;
        SetIndex(p, idx);
        if (p == _sub) { _closeSubAt = -1; return; }   // pointer is in the submenu: keep it open
        var item = p.Rows[idx].Item;
        if (item.HasChildren) { _closeSubAt = -1; OpenSub(idx, false); }
        else if (_sub != null) _closeSubAt = Time.GetTicksMsec() / 1000.0 + 0.35;   // pointer left the parent row: close after a short grace period
    }

    void OpenSub(int idx, bool focusFirst)
    {
        if (_main == null) return;
        var item = _main.Rows[idx].Item;
        if (!item.HasChildren || item.Disabled) return;
        if (_sub != null && _subOwner == idx) { if (focusFirst) { _active = _sub; if (_sub.Index < 0) Move(+1); } return; }

        CloseSub();
        _sub = BuildPanel(item.Children);
        AddChild(_sub.Holder);
        var size = _sub.Holder.GetCombinedMinimumSize();
        _sub.Holder.Size = size;
        var vp = GetViewportRect().Size;
        var mainRect = _main.Holder.GetGlobalRect();
        float rowY = _main.Rows[idx].Ctl != null ? _main.Rows[idx].Ctl.GetGlobalRect().Position.Y : mainRect.Position.Y;
        float x = mainRect.End.X - 4f;
        if (x + size.X > vp.X - Edge) x = mainRect.Position.X - size.X + 4f;
        float y = rowY - 6f;
        if (y + size.Y > vp.Y - Edge) y = vp.Y - size.Y - Edge;
        if (y < Edge) y = Edge;
        _sub.Holder.Position = new Vector2(x, y);
        _subOwner = idx;
        FadeIn(_sub.Holder);
        if (focusFirst) { _active = _sub; Move(+1); }
    }

    void Activate(MenuPanel p, int idx)
    {
        if (p == null || idx < 0 || idx >= p.Rows.Count) return;
        var item = p.Rows[idx].Item;
        if (!item.Selectable) return;
        if (item.HasChildren) { if (p == _main) OpenSub(idx, true); return; }
        var act = item.OnClick;
        Close();             // close first: the action may open a dialog or change the world
        act?.Invoke();
    }

    void Move(int dir)
    {
        var p = _active ?? _main;
        if (p == null) return;
        int n = p.Rows.Count;
        if (n == 0) return;
        int i = p.Index;
        for (int step = 0; step < n; step++)
        {
            i = i < 0 ? (dir > 0 ? 0 : n - 1) : (i + dir + n) % n;
            if (p.Rows[i].Item.Selectable)
            {
                SetIndex(p, i);
                if (p == _main && _sub != null && i != _subOwner) CloseSub();
                return;
            }
        }
    }

    // ------------------------------------------------------------------ input

    public override void _Input(InputEvent @event)
    {
        if (!IsOpen) return;

        if (@event is InputEventMouseButton mb)
        {
            bool wheel = mb.ButtonIndex == MouseButton.WheelUp || mb.ButtonIndex == MouseButton.WheelDown
                      || mb.ButtonIndex == MouseButton.WheelLeft || mb.ButtonIndex == MouseButton.WheelRight;
            if (!wheel && mb.Pressed && !InsideMenu(mb.Position))
            {
                Close();
                // a left click only dismisses the menu (it must not also select / open something); a right click goes on so it can reopen
                if (mb.ButtonIndex == MouseButton.Left) GetViewport().SetInputAsHandled();
            }
            return;
        }
        if (@event is not InputEventKey k || !k.Pressed) return;

        bool handled = true;
        switch (k.Keycode)
        {
            case Key.Escape: if (_sub != null) CloseSub(); else Close(); break;
            case Key.Down: Move(+1); break;
            case Key.Up: Move(-1); break;
            case Key.Right:
                if (_active == _main && _main.Index >= 0) OpenSub(_main.Index, true);
                break;
            case Key.Left: if (_sub != null) CloseSub(); break;
            case Key.Enter:
            case Key.KpEnter:
            case Key.Space:
                {
                    var p = _active ?? _main;
                    if (p != null && p.Index >= 0) Activate(p, p.Index);
                    break;
                }
            default: handled = false; break;
        }
        if (handled) GetViewport().SetInputAsHandled();
    }
}
