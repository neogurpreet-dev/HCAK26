using System;
using Godot;

namespace Atlas;

/// <summary>
/// Vertical navigation dock on the left edge of the HUD. Replaces the old floating bottom bar and the right-hand eye/gear rail.
///
///   LeftNavigationDock (Control, full rect, ignores the mouse)
///    |- Dock (PanelContainer, frosted glass, pinned to the left edge)
///    |    '- MarginContainer
///    |         '- VBoxContainer  (TextureButtons strictly stacked vertically)
///    |              Backward / Forward / Parent / Home | Reset camera / Refresh | View filter / Settings
///    '- Tooltip (PanelContainer + Label, floats to the right of the hovered button)
///
/// Add this script to a Control node named "LeftNavigationDock" under the UI CanvasLayer, then drag your PNGs onto the
/// exported slots in the Inspector. AtlasUI picks the node up, wires the events and (if the node is missing) creates one in code.
/// </summary>
public partial class LeftNavigationDock : Control
{
    public event Action BackRequested, ForwardRequested, UpRequested, HomeRequested, ResetCameraRequested, RefreshRequested;
    /// <summary>Eye button toggled (true = view filter panel should be shown).</summary>
    public event Action<bool> FilterToggled;
    /// <summary>Gear button toggled (true = customisation panel should be shown).</summary>
    public event Action<bool> SettingsToggled;

    [ExportGroup("Button icons")]
    [Export] public Texture2D BackwardIcon { get; set; }
    [Export] public Texture2D ForwardIcon { get; set; }
    [Export] public Texture2D ParentDirectoryIcon { get; set; }
    [Export] public Texture2D HomeIcon { get; set; }
    [Export] public Texture2D ResetCameraIcon { get; set; }
    [Export] public Texture2D RefreshIcon { get; set; }
    /// <summary>The eye. Defaults to res://Assets/UI/eye.png when left empty.</summary>
    [Export] public Texture2D ViewFilterIcon { get; set; }
    /// <summary>The gear. Defaults to res://Assets/UI/gear.png when left empty.</summary>
    [Export] public Texture2D SettingsIcon { get; set; }

    [ExportGroup("Layout")]
    [Export] public Vector2 ButtonSize { get; set; } = new Vector2(56, 56);
    [Export] public int ButtonSpacing { get; set; } = 8;
    /// <summary>X = gap between the dock and the RIGHT edge of the window, Y = distance from the top (below the title bar).</summary>
    [Export] public Vector2 DockOffset { get; set; } = new Vector2(10, 106);

    /// <summary>Width the dock occupies, so other left-side HUD can sit beside it.</summary>
    public float DockWidth => ButtonSize.X + 16;

    PanelContainer _tip;
    Label _tipLabel;
    TextureButton _back, _forward, _up, _filter, _settings;

    public override void _Ready()
    {
        Name = "LeftNavigationDock";
        MouseFilter = MouseFilterEnum.Ignore;
        SetAnchorsPreset(LayoutPreset.FullRect);

        // defaults: the PNGs in Assets/UI (anything dragged onto the Inspector slots wins)
        BackwardIcon ??= LoadIcon("res://Assets/UI/nav_backward.png");
        ForwardIcon ??= LoadIcon("res://Assets/UI/nav_forward.png");
        ParentDirectoryIcon ??= LoadIcon("res://Assets/UI/nav_parent.png");
        HomeIcon ??= LoadIcon("res://Assets/UI/nav_home.png");
        ResetCameraIcon ??= LoadIcon("res://Assets/UI/nav_reset_camera.png");
        RefreshIcon ??= LoadIcon("res://Assets/UI/nav_refresh.png");
        ViewFilterIcon ??= LoadIcon("res://Assets/UI/eye.png");
        SettingsIcon ??= LoadIcon("res://Assets/UI/gear.png");

        var glass = GD.Load<Shader>("res://Shaders/frosted_glass.gdshader");
        var dock = AtlasTheme.GlassPanel(glass, 22);
        dock.Name = "Dock";
        // top-right corner: both anchors on the right edge, explicit offsets (dock width = DockWidth), so it stays attached on every
        // resize and never depends on grow-direction behaviour while the node is created and re-parented
        dock.SetAnchorsPreset(LayoutPreset.TopRight);
        dock.AnchorLeft = 1f; dock.AnchorRight = 1f;
        dock.OffsetLeft = -DockOffset.X - DockWidth; dock.OffsetRight = -DockOffset.X;
        dock.OffsetTop = DockOffset.Y; dock.OffsetBottom = DockOffset.Y;
        AddChild(dock);

        var margin = new MarginContainer { Name = "Padding" };
        foreach (var m in new[] { "margin_left", "margin_right", "margin_top", "margin_bottom" })
            margin.AddThemeConstantOverride(m, 8);
        dock.AddChild(margin);

        var v = new VBoxContainer { Name = "Buttons" };
        v.AddThemeConstantOverride("separation", ButtonSpacing);
        margin.AddChild(v);

        _back = AddButton(v, "BackwardButton", BackwardIcon, "Backward", "Back", false, () => BackRequested?.Invoke());
        _forward = AddButton(v, "ForwardButton", ForwardIcon, "Forward", "Fwd", false, () => ForwardRequested?.Invoke());
        _up = AddButton(v, "ParentButton", ParentDirectoryIcon, "Parent Directory", "Up", false, () => UpRequested?.Invoke());
        AddButton(v, "HomeButton", HomeIcon, "Home / District View", "Home", false, () => HomeRequested?.Invoke());
        v.AddChild(Gap());
        AddButton(v, "ResetCameraButton", ResetCameraIcon, "Reset Camera", "Cam", false, () => ResetCameraRequested?.Invoke());
        AddButton(v, "RefreshButton", RefreshIcon, "Refresh", "Refresh", false, () => RefreshRequested?.Invoke());
        v.AddChild(Gap());
        _filter = AddButton(v, "ViewFilterButton", ViewFilterIcon, "View Filter", "Filter", true, null);
        _filter.Toggled += on => FilterToggled?.Invoke(on);
        _settings = AddButton(v, "SettingsButton", SettingsIcon, "Customisation / Settings", "Settings", true, null);
        _settings.Toggled += on => SettingsToggled?.Invoke(on);

        BuildTooltip();
    }

    static Texture2D LoadIcon(string path) => ResourceLoader.Exists(path) ? ResourceLoader.Load<Texture2D>(path) : null;

    Control Gap() => new Control { CustomMinimumSize = new Vector2(0, 4), MouseFilter = MouseFilterEnum.Ignore };

    // ------------------------------------------------------------ buttons

    TextureButton AddButton(VBoxContainer parent, string name, Texture2D icon, string tip, string fallbackText, bool toggle, Action onPressed)
    {
        var b = new TextureButton
        {
            Name = name,
            ToggleMode = toggle,
            TooltipText = "",                                            // the OS-style tooltip stays off; the custom label below replaces it
            CustomMinimumSize = ButtonSize,
            IgnoreTextureSize = true,                                    // scale the PNG to the button, not the other way round
            StretchMode = TextureButton.StretchModeEnum.KeepAspectCentered,
            TextureNormal = icon, TexturePressed = icon, TextureHover = icon,
            FocusMode = FocusModeEnum.None,
            TextureFilter = TextureFilterEnum.LinearWithMipmaps,
            MouseDefaultCursorShape = CursorShape.PointingHand,
        };
        if (icon == null) // no PNG assigned yet: show a text stand-in so the slot is still visible and clickable
        {
            var l = AtlasTheme.MakeLabel(fallbackText, 12, Colors.White);
            l.SetAnchorsPreset(LayoutPreset.FullRect);
            l.HorizontalAlignment = HorizontalAlignment.Center;
            l.VerticalAlignment = VerticalAlignment.Center;
            b.AddChild(l);
        }
        if (onPressed != null) b.Pressed += onPressed;

        b.MouseEntered += () => { Tint(b, true); ShowTip(b, tip); };
        b.MouseExited += () => { Tint(b, false); HideTip(); };
        b.Toggled += _ => Tint(b, b.IsHovered());
        b.ButtonDown += HideTip;   // the label would sit under the cursor while a camera/panel change is happening
        Tint(b, false);
        parent.AddChild(b);
        return b;
    }

    /// <summary>Idle = slightly dim, hover = full brightness, active toggle = full brightness, disabled = faint.</summary>
    static void Tint(TextureButton b, bool hover)
    {
        float a = b.Disabled ? 0.35f : (hover || b.ButtonPressed) ? 1f : 0.78f;
        b.Modulate = new Color(1, 1, 1, a);
    }

    // ------------------------------------------------------------ tooltip

    void BuildTooltip()
    {
        _tip = new PanelContainer { Name = "Tooltip", Visible = false, MouseFilter = MouseFilterEnum.Ignore, ZIndex = 100 };
        _tip.AddThemeStyleboxOverride("panel", AtlasTheme.Box(new Color(0.03f, 0.07f, 0.17f, 0.88f), AtlasTheme.PanelBorder, 8, 1, 10, 5));
        _tipLabel = AtlasTheme.MakeLabel("", 14, AtlasTheme.Text);
        _tip.AddChild(_tipLabel);
        AddChild(_tip);
    }

    void ShowTip(Control button, string text)
    {
        if (_tip == null) return;
        _tipLabel.Text = text;
        _tip.Visible = true;
        _tip.ResetSize();
        var min = _tip.GetCombinedMinimumSize();
        // left of the button (the dock is on the right edge), centred vertically, kept fully inside the viewport
        var view = GetViewportRect().Size;
        var target = button.GlobalPosition;
        float x = Mathf.Clamp(target.X - min.X - 12, 4f, Mathf.Max(4f, view.X - min.X - 4f));
        float y = Mathf.Clamp(target.Y + (button.Size.Y - min.Y) * 0.5f, 4f, Mathf.Max(4f, view.Y - min.Y - 4f));
        _tip.Position = new Vector2(x, y) - GlobalPosition;
    }

    void HideTip()
    {
        if (_tip != null) _tip.Visible = false;
    }

    // ------------------------------------------------------------ public API

    public void SetNavigationState(bool canBack, bool canForward, bool canUp)
    {
        SetEnabled(_back, canBack);
        SetEnabled(_forward, canForward);
        SetEnabled(_up, canUp);
    }

    static void SetEnabled(TextureButton b, bool on)
    {
        if (b == null) return;
        b.Disabled = !on;
        Tint(b, b.IsHovered());
    }

    /// <summary>Sets the eye's state without raising FilterToggled.</summary>
    public void SetFilterActive(bool on)
    {
        if (_filter == null) return;
        _filter.SetPressedNoSignal(on);
        Tint(_filter, _filter.IsHovered());
    }

    /// <summary>Sets the gear's state without raising SettingsToggled.</summary>
    public void SetSettingsActive(bool on)
    {
        if (_settings == null) return;
        _settings.SetPressedNoSignal(on);
        Tint(_settings, _settings.IsHovered());
    }
}
