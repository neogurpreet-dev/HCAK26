using System;
using System.Collections.Generic;
using System.IO;
using Godot;

namespace Atlas;

/// <summary>
/// The ATLAS HUD, built entirely in code (no .tscn needed) so it can match the reference exactly:
///   TopBar (logo, search, window controls) / Sidebar (path + quick access + settings) /
///   PropertyMappingPanel / HoverInfoPanel / ActionDock / IslandLabels / NotificationLayer.
/// Attach to the CanvasLayer named "UI". MainWorld wires the events.
/// </summary>
public partial class AtlasUI : CanvasLayer
{
    // ---- events consumed by MainWorld
    public event Action<string> NavigateRequested;
    public event Action BackRequested, ForwardRequested, UpRequested, HomeRequested, RefreshRequested;
    public event Action ResetCameraRequested, ExplorerRequested, TerminalRequested, VsCodeRequested, OverflowRequested;
    public event Action<FsEntry> OpenRequested, RevealRequested, PropertiesRequested;
    public event Action<string> SearchChanged;
    public event Action<string, bool> SettingToggled;
    /// <summary>Temporary developer switch: Ghost Links test fixtures on/off (never saved).</summary>
    public event Action<bool> TestModeToggled;

    Theme _theme;
    Shader _glass;
    Control _root;
    PropertyMappingConfig _mapping;
    ActivityStore _activity;

    Button _pathButton;
    LineEdit _pathEdit, _search;
    LeftNavigationDock _dock;
    Button _overflowBadge;
    InfoPanelView _info;
    MappingPanelView _mappingView;
    IslandLabelLayer _labels;
    PanelContainer _toast;
    Label _toastLabel;
    Tween _toastTween;
    Button _settingsButton;

    // ---- panels (the eye / gear buttons live in LeftNavigationDock)
    [ExportGroup("Panels")]
    [Export] public bool MappingVisibleAtStart { get; set; } = false;
    [Export] public bool CustomisationVisibleAtStart { get; set; } = false;

    PanelContainer _customPanel;
    ColorPickerButton _accentPicker;
    bool _syncingPicker;
    readonly Dictionary<Control, Tween> _fades = new();
    string _currentPath = "";
    readonly List<(Button button, string path)> _quick = new();

    bool _draggingWindow;
    Vector2I _dragOffset;

    public Camera3D Camera { get; set; }
    /// <summary>Right-click context menu (screen space, on top of the HUD).</summary>
    public ContextMenuView Menu { get; private set; }
    /// <summary>Modal glass dialog for rename / new item / confirmations.</summary>
    public GlassDialog Dialog { get; private set; }

    public void Initialise(PropertyMappingConfig mapping, Camera3D camera, ActivityStore activity = null)
    {
        _mapping = mapping;
        _activity = activity;
        Camera = camera;
        Build();
    }

    // ============================================================== build

    void Build()
    {
        Layer = 10;
        _theme = AtlasTheme.Build();
        _glass = GD.Load<Shader>("res://Shaders/frosted_glass.gdshader");
        if (_glass == null) GD.PushWarning("[ATLAS] frosted_glass.gdshader missing - using flat translucent panels.");

        _root = new Control { Name = "UIRoot", MouseFilter = Control.MouseFilterEnum.Ignore, Theme = _theme };
        _root.SetAnchorsPreset(Control.LayoutPreset.FullRect);
        AddChild(_root);

        _labels = new IslandLabelLayer();
        _root.AddChild(_labels.Root);

        BuildTopBar();
        BuildSidebar();

        _mappingView = new MappingPanelView(_glass, _mapping);
        PlaceTopRight(_mappingView.Root, PanelTop, PanelRight, 268);
        _mappingView.Root.Visible = MappingVisibleAtStart;
        _root.AddChild(_mappingView.Root);

        _info = new InfoPanelView(_glass);
        _info.OpenClicked += e => OpenRequested?.Invoke(e);
        _info.RevealClicked += e => RevealRequested?.Invoke(e);
        _info.PropertiesClicked += e => PropertiesRequested?.Invoke(e);
        PlaceBottomRight(_info.Root, 14, 10, 300);
        _root.AddChild(_info.Root);

        BuildToast();
        BuildOverflowBadge();
        BuildCustomisationPanel();
        BuildLeftDock();

        // added last so they draw above every other HUD element
        Menu = new ContextMenuView { Name = "ContextMenu", Glass = _glass };
        _root.AddChild(Menu);
        Dialog = new GlassDialog { Name = "Dialog", Glass = _glass };
        _root.AddChild(Dialog);
    }

    // the left dock (56px buttons + 16px padding + 10px edge margin + 12px gap) pushes the sidebar to the right
    const float DockReserve = 10 + 56 + 16 + 12;
    const float PanelTop = 92, PanelRight = 14;

    static void PlaceTopRight(Control c, float top, float right, float width)
    {
        c.SetAnchorsPreset(Control.LayoutPreset.TopRight);
        c.GrowHorizontal = Control.GrowDirection.Begin;
        c.GrowVertical = Control.GrowDirection.End;
        c.OffsetLeft = -right - width;
        c.OffsetRight = -right;
        c.OffsetTop = top;
        c.OffsetBottom = top;
    }

    static void PlaceBottomRight(Control c, float bottom, float right, float width)
    {
        c.SetAnchorsPreset(Control.LayoutPreset.BottomRight);
        c.GrowHorizontal = Control.GrowDirection.Begin;
        c.GrowVertical = Control.GrowDirection.Begin;
        c.OffsetLeft = -right - width;
        c.OffsetRight = -right;
        c.OffsetTop = -bottom;
        c.OffsetBottom = -bottom;
    }

    // ---------------------------------------------------------------- top bar

    void BuildTopBar()
    {
        var bar = new Control { Name = "TopBar", MouseFilter = Control.MouseFilterEnum.Stop };
        bar.SetAnchorsPreset(Control.LayoutPreset.TopWide);
        bar.OffsetBottom = 84;
        bar.GuiInput += OnTopBarInput;
        _root.AddChild(bar);

        var logo = new TextureRect
        {
            Name = "Logo", Texture = IconLib.Logo(64), Position = new Vector2(20, 10), Size = new Vector2(64, 64),
            ExpandMode = TextureRect.ExpandModeEnum.IgnoreSize, StretchMode = TextureRect.StretchModeEnum.KeepAspectCentered,
            MouseFilter = Control.MouseFilterEnum.Ignore,
        };
        bar.AddChild(logo);
        var title = AtlasTheme.MakeLabel("ATLAS", 38, Colors.White, bold: true, spacing: 5);
        title.Position = new Vector2(96, 4);
        bar.AddChild(title);
        var tag = AtlasTheme.MakeLabel("EXPLORE YOUR DIGITAL WORLD.", 12, AtlasTheme.TextDim, spacing: 1);
        tag.Position = new Vector2(98, 56);
        bar.AddChild(tag);

        // right cluster: search + window buttons
        var cluster = new HBoxContainer { Name = "TopRightBar", MouseFilter = Control.MouseFilterEnum.Ignore };
        cluster.AddThemeConstantOverride("separation", 14);
        cluster.SetAnchorsPreset(Control.LayoutPreset.TopRight);
        cluster.GrowHorizontal = Control.GrowDirection.Begin;
        cluster.OffsetRight = -18; cluster.OffsetLeft = -18; cluster.OffsetTop = 14; cluster.OffsetBottom = 14;
        _root.AddChild(cluster);

        var pill = new PanelContainer { CustomMinimumSize = new Vector2(330, 42), MouseFilter = Control.MouseFilterEnum.Stop };
        pill.AddThemeStyleboxOverride("panel", AtlasTheme.Box(new Color(0.03f, 0.07f, 0.17f, 0.9f), new Color(0.25f, 0.45f, 0.9f, 0.5f), 21, 1, 14, 4));
        var h = new HBoxContainer();
        h.AddThemeConstantOverride("separation", 8);
        h.AddChild(new TextureRect { Texture = IconLib.Get("search", Colors.White, 22), CustomMinimumSize = new Vector2(22, 22), ExpandMode = TextureRect.ExpandModeEnum.IgnoreSize, StretchMode = TextureRect.StretchModeEnum.KeepAspectCentered, SizeFlagsVertical = Control.SizeFlags.ShrinkCenter, MouseFilter = Control.MouseFilterEnum.Ignore });
        _search = new LineEdit { PlaceholderText = "Search in folder...", SizeFlagsHorizontal = Control.SizeFlags.ExpandFill, ClearButtonEnabled = true };
        _search.AddThemeStyleboxOverride("normal", new StyleBoxEmpty());
        _search.AddThemeStyleboxOverride("focus", new StyleBoxEmpty());
        _search.TextChanged += t => SearchChanged?.Invoke(t);
        h.AddChild(_search);
        pill.AddChild(h);
        cluster.AddChild(pill);

        var winBox = new HBoxContainer();
        winBox.AddThemeConstantOverride("separation", 6);
        winBox.AddChild(WindowButton("minimize", "Minimise", () => DisplayServer.WindowSetMode(DisplayServer.WindowMode.Minimized)));
        winBox.AddChild(WindowButton("maximize", "Maximise / restore", ToggleMaximise));
        winBox.AddChild(WindowButton("close", "Close", () => { UsageTracker.Instance.Flush(); GetTree().Quit(); }));
        cluster.AddChild(winBox);

        try { DisplayServer.WindowSetFlag(DisplayServer.WindowFlags.Borderless, true); } catch { }
    }

    Button WindowButton(string icon, string tip, Action action)
    {
        var b = new Button { Icon = IconLib.Get(icon, Colors.White, 20, 1.8f), TooltipText = tip, CustomMinimumSize = new Vector2(40, 40), Flat = true, ExpandIcon = false, IconAlignment = HorizontalAlignment.Center };
        b.Pressed += action;
        return b;
    }

    static void ToggleMaximise()
    {
        var m = DisplayServer.WindowGetMode();
        DisplayServer.WindowSetMode(m == DisplayServer.WindowMode.Maximized ? DisplayServer.WindowMode.Windowed : DisplayServer.WindowMode.Maximized);
    }

    void OnTopBarInput(InputEvent e)
    {
        if (e is InputEventMouseButton mb && mb.ButtonIndex == MouseButton.Left)
        {
            if (mb.Pressed)
            {
                if (mb.DoubleClick) { ToggleMaximise(); return; }
                if (DisplayServer.WindowGetMode() == DisplayServer.WindowMode.Maximized) return;
                _draggingWindow = true;
                _dragOffset = DisplayServer.MouseGetPosition() - DisplayServer.WindowGetPosition();
            }
            else _draggingWindow = false;
        }
        else if (e is InputEventMouseMotion && _draggingWindow)
        {
            DisplayServer.WindowSetPosition(DisplayServer.MouseGetPosition() - _dragOffset);
        }
    }

    // ---------------------------------------------------------------- sidebar

    void BuildSidebar()
    {
        var side = AtlasTheme.GlassPanel(_glass, 16);
        side.Name = "Sidebar";
        side.SetAnchorsPreset(Control.LayoutPreset.TopLeft);
        side.OffsetLeft = DockReserve; side.OffsetRight = DockReserve + 237; side.OffsetTop = 106; side.OffsetBottom = 106;
        _root.AddChild(side);

        var margin = new MarginContainer();
        foreach (var m in new[] { "margin_left", "margin_right", "margin_top", "margin_bottom" }) margin.AddThemeConstantOverride(m, 8);
        side.AddChild(margin);
        var v = new VBoxContainer();
        v.AddThemeConstantOverride("separation", 3);
        margin.AddChild(v);

        // current path pill (click to type a path)
        _pathButton = new Button { Alignment = HorizontalAlignment.Left, Icon = IconLib.Get("folder", Colors.White, 22), CustomMinimumSize = new Vector2(0, 46), ClipText = true, TextOverrunBehavior = TextServer.OverrunBehavior.TrimEllipsis, TooltipText = "Click to type or paste a path" };
        StyleSelected(_pathButton, true);
        _pathButton.Pressed += ShowPathEditor;
        v.AddChild(_pathButton);

        _pathEdit = new LineEdit { Visible = false, CustomMinimumSize = new Vector2(0, 46), PlaceholderText = "Type a path and press Enter" };
        _pathEdit.TextSubmitted += OnPathSubmitted;
        _pathEdit.FocusExited += () => { _pathEdit.Visible = false; _pathButton.Visible = true; };
        _pathEdit.GuiInput += ev => { if (ev is InputEventKey k && k.Pressed && k.Keycode == Key.Escape) { _pathEdit.ReleaseFocus(); } };
        v.AddChild(_pathEdit);

        string profile = System.Environment.GetFolderPath(System.Environment.SpecialFolder.UserProfile);
        AddQuick(v, "Home", "home", profile);
        AddQuick(v, "Desktop", "desktop", System.Environment.GetFolderPath(System.Environment.SpecialFolder.DesktopDirectory));
        AddQuick(v, "Documents", "document", System.Environment.GetFolderPath(System.Environment.SpecialFolder.MyDocuments));
        AddQuick(v, "Downloads", "download", Path.Combine(profile, "Downloads"));
        AddQuick(v, "Pictures", "image", System.Environment.GetFolderPath(System.Environment.SpecialFolder.MyPictures));
        AddQuick(v, "Music", "music", System.Environment.GetFolderPath(System.Environment.SpecialFolder.MyMusic));
        AddQuick(v, "Videos", "video", System.Environment.GetFolderPath(System.Environment.SpecialFolder.MyVideos));

        v.AddChild(new Control { CustomMinimumSize = new Vector2(0, 10) });
        _settingsButton = QuickButton("Settings", "gear");
        _settingsButton.Pressed += () => SetCustomisationVisible(!(_customPanel?.Visible ?? false));
        v.AddChild(_settingsButton);
    }

    Button QuickButton(string text, string icon)
    {
        var b = new Button { Text = text, Alignment = HorizontalAlignment.Left, Icon = IconLib.Get(icon, Colors.White, 22), CustomMinimumSize = new Vector2(0, 40), ExpandIcon = false };
        b.AddThemeFontSizeOverride("font_size", 15);
        return b;
    }

    void AddQuick(VBoxContainer parent, string text, string icon, string path)
    {
        var b = QuickButton(text, icon);
        b.Pressed += () => NavigateRequested?.Invoke(path);
        parent.AddChild(b);
        _quick.Add((b, path));
    }

    static void StyleSelected(Button b, bool selected)
    {
        if (selected)
        {
            var sb = AtlasTheme.Box(new Color(0.11f, 0.31f, 0.85f, 0.92f), AtlasTheme.SelectedBorder, 10, 1, 12, 8);
            b.AddThemeStyleboxOverride("normal", sb);
            b.AddThemeStyleboxOverride("hover", sb);
            b.AddThemeStyleboxOverride("pressed", sb);
        }
        else
        {
            b.RemoveThemeStyleboxOverride("normal");
            b.RemoveThemeStyleboxOverride("hover");
            b.RemoveThemeStyleboxOverride("pressed");
        }
    }

    /// <summary>Ctrl+F / F3: switches the path bar into edit mode and focuses it with the whole path selected.</summary>
    public void FocusPathBar() => ShowPathEditor();

    void ShowPathEditor()
    {
        _pathButton.Visible = false;
        _pathEdit.Visible = true;
        _pathEdit.Text = _currentPath;
        _pathEdit.GrabFocus();
        _pathEdit.SelectAll();
    }

    void OnPathSubmitted(string text)
    {
        _pathEdit.ReleaseFocus();
        text = text.Trim().Trim('"');
        if (text.Length > 0) NavigateRequested?.Invoke(text);
    }

    // ---------------------------------------------------------------- left dock + overflow badge

    void BuildLeftDock()
    {
        // preferred: the LeftNavigationDock node placed in the scene (so PNGs can be dragged onto it in the Inspector)
        _dock = GetNodeOrNull<LeftNavigationDock>("LeftNavigationDock");
        if (_dock != null) _dock.GetParent()?.RemoveChild(_dock);
        else _dock = new LeftNavigationDock { Name = "LeftNavigationDock" };
        _root.AddChild(_dock);

        _dock.BackRequested += () => BackRequested?.Invoke();
        _dock.ForwardRequested += () => ForwardRequested?.Invoke();
        _dock.UpRequested += () => UpRequested?.Invoke();
        _dock.HomeRequested += () => HomeRequested?.Invoke();
        _dock.ResetCameraRequested += () => ResetCameraRequested?.Invoke();
        _dock.RefreshRequested += () => RefreshRequested?.Invoke();
        _dock.FilterToggled += on => SetMappingVisible(on);
        _dock.SettingsToggled += on => SetCustomisationVisible(on);

        // only one panel may be open at a time; the filter panel wins if both are requested at start
        bool mapStart = MappingVisibleAtStart;
        bool customStart = CustomisationVisibleAtStart && !mapStart;
        _dock.SetFilterActive(mapStart);
        _dock.SetSettingsActive(customStart);
    }

    /// <summary>Small "+N items" pill (bottom centre) that only appears when a folder has more items than the world can show.</summary>
    void BuildOverflowBadge()
    {
        _overflowBadge = new Button { Name = "OverflowBadge", Visible = false, Icon = IconLib.Get("plus", new Color("BFF6FF"), 20, 2.2f), CustomMinimumSize = new Vector2(0, 40), TooltipText = "Too many items to show - open this folder in Windows Explorer" };
        var glow = AtlasTheme.Box(new Color(0.05f, 0.30f, 0.38f, 0.95f), AtlasTheme.Cyan, 20, 2, 14, 6);
        _overflowBadge.AddThemeStyleboxOverride("normal", glow);
        _overflowBadge.AddThemeStyleboxOverride("hover", AtlasTheme.Box(new Color(0.08f, 0.42f, 0.52f, 1f), Colors.White, 20, 2, 14, 6));
        _overflowBadge.AddThemeStyleboxOverride("pressed", glow);
        _overflowBadge.AddThemeColorOverride("font_color", new Color("BFF6FF"));
        _overflowBadge.Pressed += () => OverflowRequested?.Invoke();
        _overflowBadge.SetAnchorsPreset(Control.LayoutPreset.CenterBottom);
        _overflowBadge.GrowHorizontal = Control.GrowDirection.Both;
        _overflowBadge.GrowVertical = Control.GrowDirection.Begin;
        _overflowBadge.OffsetTop = -18; _overflowBadge.OffsetBottom = -18;
        _root.AddChild(_overflowBadge);
    }

    // ---------------------------------------------------------------- toast + settings

    void BuildToast()
    {
        _toast = new PanelContainer { Name = "NotificationLayer", MouseFilter = Control.MouseFilterEnum.Ignore, Visible = false };
        _toast.SetAnchorsPreset(Control.LayoutPreset.CenterBottom);
        _toast.GrowHorizontal = Control.GrowDirection.Both;
        _toast.GrowVertical = Control.GrowDirection.Begin;
        _toast.OffsetTop = -84; _toast.OffsetBottom = -84;
        _toastLabel = AtlasTheme.MakeLabel("", 14, AtlasTheme.Text);
        _toast.AddChild(_toastLabel);
        _root.AddChild(_toast);
    }

    // ---------------------------------------------------------------- toolbar toggles + customisation panel
    //
    //  UIRoot (Control)
    //   |- LeftNavigationDock (left edge; nav buttons + eye -> PropertyMappingPanel + gear -> CustomisationPanel, see LeftNavigationDock.cs)
    //   |- PropertyMappingPanel   (MappingPanelView.Root)
    //   '- CustomisationPanel (PanelContainer)
    //        '- MarginContainer
    //             '- VBoxContainer
    //                  |- Label "Customisation"
    //                  |- HBoxContainer [Label "Accent colour" + ColorPickerButton] + "Use file-type colours"
    //                  |- HSeparator, "Display" VBoxContainer of CheckBoxes (glow, shadows, MSAA, hidden files)
    //                  '- HSeparator, "Actions" HBoxContainer (Reset camera / Refresh / Reset mapping)

    public void SetMappingVisible(bool on)
    {
        _dock?.SetFilterActive(on);
        ShowPanel(_mappingView?.Root, on);
        if (on && (_customPanel?.Visible ?? false)) SetCustomisationVisible(false); // opening one hides the other
    }

    public void SetCustomisationVisible(bool on)
    {
        _dock?.SetSettingsActive(on);
        ShowPanel(_customPanel, on);
        if (on && (_mappingView?.Root.Visible ?? false)) SetMappingVisible(false); // opening one hides the other
    }

    void ShowPanel(Control c, bool show)
    {
        if (c == null) return;
        if (_fades.TryGetValue(c, out var old) && old != null && old.IsValid()) old.Kill();
        if (show) { c.Visible = true; c.Modulate = new Color(1, 1, 1, c.Modulate.A); }
        var t = CreateTween();
        _fades[c] = t;
        t.TweenProperty(c, "modulate:a", show ? 1f : 0f, 0.14);
        if (!show) t.TweenCallback(Callable.From(() => c.Visible = false));
    }

    void BuildCustomisationPanel()
    {
        _customPanel = AtlasTheme.GlassPanel(_glass, 16);
        _customPanel.Name = "CustomisationPanel";
        _customPanel.CustomMinimumSize = new Vector2(280, 0);
        _customPanel.Visible = CustomisationVisibleAtStart && !MappingVisibleAtStart;

        var margin = new MarginContainer();
        foreach (var m in new[] { "margin_left", "margin_right" }) margin.AddThemeConstantOverride(m, 16);
        foreach (var m in new[] { "margin_top", "margin_bottom" }) margin.AddThemeConstantOverride(m, 14);
        _customPanel.AddChild(margin);

        var v = new VBoxContainer();
        v.AddThemeConstantOverride("separation", 10);
        // the panel now holds the Activity & Ghost Links section too, so it scrolls instead of growing past the window
        var scroll = new ScrollContainer { HorizontalScrollMode = ScrollContainer.ScrollMode.Disabled, CustomMinimumSize = new Vector2(0, 520) };
        v.SizeFlagsHorizontal = Control.SizeFlags.ExpandFill;
        scroll.AddChild(v);
        margin.AddChild(scroll);
        v.AddChild(AtlasTheme.MakeLabel("Customisation", 18, Colors.White, bold: true));

        // -- appearance
        v.AddChild(AtlasTheme.MakeLabel("Appearance", 13, AtlasTheme.Text));
        var accentRow = new HBoxContainer();
        accentRow.AddThemeConstantOverride("separation", 10);
        var accentLabel = AtlasTheme.MakeLabel("Accent colour", 14, AtlasTheme.Text);
        accentLabel.SizeFlagsHorizontal = Control.SizeFlags.ExpandFill;
        accentRow.AddChild(accentLabel);
        _accentPicker = new ColorPickerButton { Color = _mapping?.AccentOverride ?? new Color("3B82F6"), EditAlpha = false, CustomMinimumSize = new Vector2(64, 30) };
        _accentPicker.ColorChanged += c =>
        {
            if (_syncingPicker || _mapping == null) return;
            _mapping.AccentOverrideEnabled = true;
            _mapping.AccentOverride = c;
            _mapping.NotifyChanged();
            _mappingView?.Sync();
        };
        _accentPicker.PickerCreated += SyncAccentPicker;
        accentRow.AddChild(_accentPicker);
        v.AddChild(accentRow);

        var auto = new Button { Text = "Use file-type colours" };
        auto.Pressed += () => { if (_mapping == null) return; _mapping.AccentOverrideEnabled = false; _mapping.NotifyChanged(); _mappingView?.Sync(); };
        v.AddChild(auto);

        // -- display toggles
        v.AddChild(new HSeparator());
        v.AddChild(AtlasTheme.MakeLabel("Display", 13, AtlasTheme.Text));
        var display = new VBoxContainer();
        display.AddThemeConstantOverride("separation", 6);
        AddSetting(display, "Bloom / glow", "glow", true);
        AddSetting(display, "Shadows", "shadows", true);
        AddSetting(display, "Anti-aliasing (MSAA)", "msaa", true);
        AddSetting(display, "Show hidden files", "hidden", false);
        v.AddChild(display);

        AddActivitySection(v);

        // -- actions
        v.AddChild(new HSeparator());
        v.AddChild(AtlasTheme.MakeLabel("Actions", 13, AtlasTheme.Text));
        var actions = new HBoxContainer();
        actions.AddThemeConstantOverride("separation", 8);
        var cam = new Button { Text = "Reset camera", SizeFlagsHorizontal = Control.SizeFlags.ExpandFill };
        cam.Pressed += () => ResetCameraRequested?.Invoke();
        var refresh = new Button { Text = "Refresh", SizeFlagsHorizontal = Control.SizeFlags.ExpandFill };
        refresh.Pressed += () => RefreshRequested?.Invoke();
        actions.AddChild(cam);
        actions.AddChild(refresh);
        v.AddChild(actions);
        var resetMap = new Button { Text = "Reset property mapping" };
        resetMap.Pressed += () => { if (_mapping == null) return; _mapping.ResetToDefaults(); _mapping.NotifyChanged(); _mappingView?.Sync(); SyncAccentPicker(); };
        v.AddChild(resetMap);

        PlaceTopRight(_customPanel, PanelTop, PanelRight, 280); // same slot as the filter panel (never open together)
        _root.AddChild(_customPanel);
    }

    void SyncAccentPicker()
    {
        if (_accentPicker == null || _mapping == null) return;
        _syncingPicker = true;
        _accentPicker.Color = _mapping.AccentOverride;
        _syncingPicker = false;
    }

    /// <summary>Ghost Link Sensitivity, tracking switches, the privacy disclosure and Clear history.</summary>
    void AddActivitySection(VBoxContainer v)
    {
        if (_activity == null) return;

        // -- hover preview switch (persisted in the activity settings file)
        v.AddChild(new HSeparator());
        v.AddChild(AtlasTheme.MakeLabel("File Preview", 13, AtlasTheme.Text));
        var preview = new CheckBox
        {
            Text = "Show file preview on hover",
            ButtonPressed = _activity.Settings.FilePreview,
            TooltipText = "Text, code and images only. Never shown for folders or unsupported files. Does not affect Ghost Links, the camera or opening files.",
        };
        preview.Toggled += on => _activity.UpdateSettings(x => x.FilePreview = on);
        v.AddChild(preview);

        v.AddChild(new HSeparator());
        v.AddChild(AtlasTheme.MakeLabel("Activity & Ghost Links", 13, AtlasTheme.Text));

        var s = _activity.Settings;

        var row = new HBoxContainer();
        row.AddThemeConstantOverride("separation", 10);
        var rowLabel = AtlasTheme.MakeLabel("Ghost Link Sensitivity", 14, AtlasTheme.Text);
        rowLabel.SizeFlagsHorizontal = Control.SizeFlags.ExpandFill;
        row.AddChild(rowLabel);
        var sens = new OptionButton
        {
            CustomMinimumSize = new Vector2(112, 30),
            TooltipText = "Strict: only strongly associated items.\nBalanced: items repeatedly used together.\nExploratory: weaker but plausible associations.\nYour history is never changed by this setting.",
        };
        sens.AddItem("Strict", (int)GhostSensitivity.Strict);
        sens.AddItem("Balanced", (int)GhostSensitivity.Balanced);
        sens.AddItem("Exploratory", (int)GhostSensitivity.Exploratory);
        sens.Select(sens.GetItemIndex((int)s.Sensitivity));
        sens.ItemSelected += i =>
        {
            var chosen = (GhostSensitivity)sens.GetItemId((int)i);
            _activity.UpdateSettings(x => x.Sensitivity = chosen);
        };
        row.AddChild(sens);
        v.AddChild(row);

        var track = new CheckBox { Text = "Track files & apps opened in ATLAS", ButtonPressed = s.TrackActivity };
        var external = new CheckBox
        {
            Text = "Observe app activation outside ATLAS",
            ButtonPressed = s.ObserveExternalApps,
            Disabled = !s.TrackActivity,
            TooltipText = "Off by default. Notes which program is in the foreground (program path only, never window titles) so applications can take part in Ghost Links.",
        };
        track.Toggled += on => { _activity.UpdateSettings(x => x.TrackActivity = on); external.Disabled = !on; };
        external.Toggled += on => _activity.UpdateSettings(x => x.ObserveExternalApps = on);
        v.AddChild(track);
        v.AddChild(external);

        var note = AtlasTheme.MakeLabel("Kept only on this computer. ATLAS stores file and program paths with timestamps; never file contents, window titles or keystrokes, and nothing is sent anywhere. Observing other programs is opt-in and only notes which program is in front.", 11, AtlasTheme.TextDim);
        note.AutowrapMode = TextServer.AutowrapMode.WordSmart;
        note.SizeFlagsHorizontal = Control.SizeFlags.ExpandFill;
        note.CustomMinimumSize = new Vector2(240, 0);
        v.AddChild(note);

        var clear = new Button { Text = "Clear activity history" };
        bool armed = false;
        clear.Pressed += () =>
        {
            if (!armed)
            {
                armed = true;
                clear.Text = "Click again to confirm";
                GetTree().CreateTimer(4.0).Timeout += () => { armed = false; clear.Text = "Clear activity history"; };
                return;
            }
            armed = false;
            clear.Text = "Clear activity history";
            _activity.ClearHistory();
        };
        v.AddChild(clear);

        // -- temporary developer switch
        v.AddChild(new HSeparator());
        v.AddChild(AtlasTheme.MakeLabel("Developer (temporary)", 13, AtlasTheme.Text));
        var test = new CheckBox
        {
            Text = "Ghost Links test mode (not saved)",
            ButtonPressed = _activity.TestModeActive,
            TooltipText = "Injects fixed test relationships so Ghost Links can be tried without usage history.\nWhile on, nothing is recorded. Turns off when ATLAS closes.",
        };
        test.Toggled += on => TestModeToggled?.Invoke(on);
        v.AddChild(test);
    }

    void AddSetting(VBoxContainer parent, string label, string key, bool initial)
    {
        var cb = new CheckBox { Text = label, ButtonPressed = initial };
        cb.Toggled += on => SettingToggled?.Invoke(key, on);
        parent.AddChild(cb);
    }

    // ============================================================== public API

    public void SetCurrentPath(string path)
    {
        _currentPath = path;
        string shown = path.Length > 24 ? "..." + path.Substring(path.Length - 22) : path;
        _pathButton.Text = shown;
        _pathButton.TooltipText = path + "\n(click to type or paste a path)";
        _search.PlaceholderText = "Search in " + (Path.GetFileName(path.TrimEnd('\\')) is { Length: > 0 } n ? n : path) + "...";
        foreach (var (b, p) in _quick)
        {
            bool same = string.Equals(TrimSlash(p), TrimSlash(path), StringComparison.OrdinalIgnoreCase);
            StyleSelected(b, same);
        }
    }

    static string TrimSlash(string s) => s.Length > 3 ? s.TrimEnd('\\', '/') : s;

    public void SetNavigationState(bool canBack, bool canForward, bool canUp) => _dock?.SetNavigationState(canBack, canForward, canUp);

    public void SetOverflow(int count)
    {
        _overflowBadge.Visible = count > 0;
        _overflowBadge.Text = count > 0 ? $"+{count} items" : "";
    }

    public void ShowEntry(FsEntry e, string usageTier) => _info.Show(e, usageTier);
    public void HideEntry() => _info.Hide();
    public void SetIslands(IReadOnlyList<IslandInfo> islands) => _labels.SetIslands(islands);
    public void SyncMappingPanel() { _mappingView?.Sync(); SyncAccentPicker(); }

    public void Notify(string message, bool error = false)
    {
        if (_toast == null) return;
        _toastLabel.Text = message;
        _toast.AddThemeStyleboxOverride("panel", AtlasTheme.Box(new Color(0.03f, 0.07f, 0.17f, 0.95f), error ? new Color("EF4444") : AtlasTheme.Cyan, 10, 1, 16, 9));
        _toast.Modulate = Colors.White;
        _toast.Visible = true;
        _toastTween?.Kill();
        _toastTween = CreateTween();
        _toastTween.TweenInterval(error ? 5.0 : 3.0);
        _toastTween.TweenProperty(_toast, "modulate:a", 0.0f, 0.5f);
        _toastTween.TweenCallback(Callable.From(() => _toast.Visible = false));
    }

    public override void _Process(double delta)
    {
        _labels?.Update(Camera);
    }

    public override void _ExitTree()
    {
        _toastTween?.Kill();
    }
}
