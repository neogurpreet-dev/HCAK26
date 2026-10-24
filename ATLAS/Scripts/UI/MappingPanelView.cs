using System;
using System.Collections.Generic;
using Godot;

namespace Atlas;

/// <summary>
/// "Property Mapping" card (top-right in the reference): Group / Height / Colour / Transparency dropdowns,
/// accent colour presets and Reset, plus an Advanced section (range, log, invert per rule).
/// Edits go straight to <see cref="PropertyMappingConfig"/>, which raises MappingChanged so the world updates live.
/// </summary>
public sealed class MappingPanelView
{
    public PanelContainer Root { get; }

    readonly PropertyMappingConfig _map;
    readonly OptionButton _group, _height, _color, _opacity;
    readonly VBoxContainer _advanced;
    readonly Button _advancedToggle;
    readonly OptionButton _ruleTarget;
    readonly SpinBox _outMin, _outMax;
    readonly CheckBox _log, _invert;
    readonly List<Button> _presets = new();
    int _activePreset = -1;
    bool _loading;

    static readonly string[] PresetHex = { "3B82F6", "A855F7", "22C55E", "FACC15", "F97316", "EF4444" };
    static readonly VisualProperty[] AdvancedTargets = { VisualProperty.Scale, VisualProperty.Height, VisualProperty.GlowIntensity, VisualProperty.Opacity };

    public MappingPanelView(Shader glass, PropertyMappingConfig map)
    {
        _map = map;
        Root = AtlasTheme.GlassPanel(glass, 16);
        Root.Name = "PropertyMappingPanel";
        Root.CustomMinimumSize = new Vector2(268, 0);

        var margin = new MarginContainer();
        margin.AddThemeConstantOverride("margin_left", 16);
        margin.AddThemeConstantOverride("margin_right", 16);
        margin.AddThemeConstantOverride("margin_top", 14);
        margin.AddThemeConstantOverride("margin_bottom", 14);
        Root.AddChild(margin);

        var v = new VBoxContainer();
        v.AddThemeConstantOverride("separation", 10);
        margin.AddChild(v);
        v.AddChild(AtlasTheme.MakeLabel("Property Mapping", 18, Colors.White, bold: true));

        var grid = new GridContainer { Columns = 2 };
        grid.AddThemeConstantOverride("h_separation", 10);
        grid.AddThemeConstantOverride("v_separation", 9);
        v.AddChild(grid);

        _group = Dropdown(grid, "Group by");
        foreach (var g in new[] { GroupMode.FileType, GroupMode.Extension, GroupMode.LastModified, GroupMode.SizeBucket, GroupMode.None })
            _group.AddItem(PropertyMappingConfig.Label(g), (int)g);

        _height = Dropdown(grid, "Height by");
        FillSources(_height, SourceProperty.None, SourceProperty.FileSize, SourceProperty.LastModified, SourceProperty.UsageFrequency, SourceProperty.NameLength);

        _color = Dropdown(grid, "Color by");
        _color.AddItem("File Type", (int)SourceProperty.None);
        foreach (var s in new[] { SourceProperty.LastModified, SourceProperty.FileSize, SourceProperty.UsageFrequency })
            _color.AddItem(PropertyMappingConfig.Label(s), (int)s);

        _opacity = Dropdown(grid, "Transparency by");
        FillSources(_opacity, SourceProperty.None, SourceProperty.UsageFrequency, SourceProperty.LastModified, SourceProperty.FileSize);

        _group.ItemSelected += i => { if (_loading) return; _map.GroupBy = (GroupMode)_group.GetItemId((int)i); _map.NotifyChanged(); };
        _height.ItemSelected += i => { if (_loading) return; _map.SetSource(VisualProperty.Height, (SourceProperty)_height.GetItemId((int)i)); LoadAdvanced(); };
        _color.ItemSelected += i => { if (_loading) return; _map.SetSource(VisualProperty.BaseColor, (SourceProperty)_color.GetItemId((int)i)); };
        _opacity.ItemSelected += i => { if (_loading) return; _map.SetSource(VisualProperty.Opacity, (SourceProperty)_opacity.GetItemId((int)i)); LoadAdvanced(); };

        // presets + reset
        v.AddChild(AtlasTheme.MakeLabel("Color Presets", 14, AtlasTheme.Text));
        var presetRow = new HBoxContainer();
        presetRow.AddThemeConstantOverride("separation", 9);
        for (int i = 0; i < PresetHex.Length; i++)
        {
            int idx = i;
            var b = new Button { CustomMinimumSize = new Vector2(30, 30), TooltipText = "Recolour all accents (click again to clear)" };
            StylePreset(b, new Color(PresetHex[i]), false);
            b.Pressed += () => OnPreset(idx);
            _presets.Add(b);
            presetRow.AddChild(b);
        }
        var spacer = new Control { SizeFlagsHorizontal = Control.SizeFlags.ExpandFill };
        presetRow.AddChild(spacer);
        var reset = new Button { Text = "Reset", CustomMinimumSize = new Vector2(70, 32) };
        reset.AddThemeStyleboxOverride("normal", AtlasTheme.Box(new Color(0.08f, 0.14f, 0.3f, 0.9f), new Color(0.3f, 0.5f, 0.95f, 0.55f), 8, 1, 10, 4));
        reset.AddThemeStyleboxOverride("hover", AtlasTheme.Box(new Color(0.12f, 0.22f, 0.45f, 0.95f), new Color(0.4f, 0.62f, 1f, 0.9f), 8, 1, 10, 4));
        reset.Pressed += () => { _map.ResetToDefaults(); _activePreset = -1; RefreshPresetStyles(); _map.NotifyChanged(); Sync(); };
        presetRow.AddChild(reset);
        v.AddChild(presetRow);

        // advanced
        _advancedToggle = new Button { Text = "+ Advanced", Flat = true, Alignment = HorizontalAlignment.Left };
        _advancedToggle.AddThemeColorOverride("font_color", AtlasTheme.TextDim);
        v.AddChild(_advancedToggle);

        _advanced = new VBoxContainer { Visible = false };
        _advanced.AddThemeConstantOverride("separation", 8);
        v.AddChild(_advanced);

        _ruleTarget = new OptionButton();
        foreach (var t in AdvancedTargets) _ruleTarget.AddItem(PropertyMappingConfig.Label(t), (int)t);
        _advanced.AddChild(_ruleTarget);

        var range = new HBoxContainer();
        range.AddThemeConstantOverride("separation", 8);
        _outMin = MakeSpin(); _outMax = MakeSpin();
        range.AddChild(AtlasTheme.MakeLabel("Out", 13, AtlasTheme.TextDim));
        range.AddChild(_outMin);
        range.AddChild(AtlasTheme.MakeLabel("to", 13, AtlasTheme.TextDim));
        range.AddChild(_outMax);
        _advanced.AddChild(range);

        _log = new CheckBox { Text = "Logarithmic" };
        _invert = new CheckBox { Text = "Invert" };
        var flags = new HBoxContainer();
        flags.AddThemeConstantOverride("separation", 12);
        flags.AddChild(_log); flags.AddChild(_invert);
        _advanced.AddChild(flags);

        _advancedToggle.Pressed += () =>
        {
            _advanced.Visible = !_advanced.Visible;
            _advancedToggle.Text = _advanced.Visible ? "- Advanced" : "+ Advanced";
            LoadAdvanced();
        };
        _ruleTarget.ItemSelected += _ => LoadAdvanced();
        _outMin.ValueChanged += _ => ApplyAdvanced();
        _outMax.ValueChanged += _ => ApplyAdvanced();
        _log.Toggled += _ => ApplyAdvanced();
        _invert.Toggled += _ => ApplyAdvanced();

        Sync();
    }

    static SpinBox MakeSpin()
    {
        var s = new SpinBox { MinValue = 0, MaxValue = 20, Step = 0.05, CustomMinimumSize = new Vector2(78, 0), SizeFlagsHorizontal = Control.SizeFlags.ExpandFill };
        return s;
    }

    static OptionButton Dropdown(GridContainer grid, string caption)
    {
        var l = AtlasTheme.MakeLabel(caption, 14, AtlasTheme.Text);
        l.CustomMinimumSize = new Vector2(96, 0);
        l.VerticalAlignment = VerticalAlignment.Center;
        var d = new OptionButton { SizeFlagsHorizontal = Control.SizeFlags.ExpandFill, CustomMinimumSize = new Vector2(132, 34), FitToLongestItem = false };
        d.AddThemeFontSizeOverride("font_size", 13);
        grid.AddChild(l);
        grid.AddChild(d);
        return d;
    }

    static void FillSources(OptionButton d, params SourceProperty[] sources)
    {
        foreach (var s in sources) d.AddItem(PropertyMappingConfig.Label(s), (int)s);
    }

    /// <summary>Re-reads the config into the controls (after Reset or an external change).</summary>
    public void Sync()
    {
        _loading = true;
        _group.Select(_group.GetItemIndex((int)_map.GroupBy));
        SelectId(_height, (int)_map.GetSource(VisualProperty.Height));
        SelectId(_color, (int)_map.GetSource(VisualProperty.BaseColor));
        SelectId(_opacity, (int)_map.GetSource(VisualProperty.Opacity));
        _loading = false;
        LoadAdvanced();
    }

    static void SelectId(OptionButton d, int id)
    {
        int idx = d.GetItemIndex(id);
        d.Select(idx >= 0 ? idx : 0);
    }

    void LoadAdvanced()
    {
        if (!_advanced.Visible) return;
        _loading = true;
        var target = (VisualProperty)_ruleTarget.GetItemId(Math.Max(0, _ruleTarget.Selected));
        var rule = _map.FindRule(target);
        bool has = rule != null;
        _outMin.Editable = _outMax.Editable = _log.Disabled == false && has;
        if (has)
        {
            _outMin.Value = rule.OutMin;
            _outMax.Value = rule.OutMax;
            _log.ButtonPressed = rule.Transform == TransformKind.Log;
            _invert.ButtonPressed = rule.Invert;
        }
        _loading = false;
    }

    void ApplyAdvanced()
    {
        if (_loading) return;
        var target = (VisualProperty)_ruleTarget.GetItemId(Math.Max(0, _ruleTarget.Selected));
        var rule = _map.FindRule(target);
        if (rule == null) return;
        rule.OutMin = (float)_outMin.Value;
        rule.OutMax = (float)_outMax.Value;
        rule.Transform = _log.ButtonPressed ? TransformKind.Log : TransformKind.Linear;
        rule.Invert = _invert.ButtonPressed;
        _map.NotifyChanged();
    }

    void OnPreset(int idx)
    {
        if (_activePreset == idx)
        {
            _activePreset = -1;
            _map.AccentOverrideEnabled = false;
        }
        else
        {
            _activePreset = idx;
            _map.AccentOverrideEnabled = true;
            _map.AccentOverride = new Color(PresetHex[idx]);
        }
        RefreshPresetStyles();
        _map.NotifyChanged();
    }

    void RefreshPresetStyles()
    {
        for (int i = 0; i < _presets.Count; i++) StylePreset(_presets[i], new Color(PresetHex[i]), i == _activePreset);
    }

    static void StylePreset(Button b, Color c, bool active)
    {
        var sb = AtlasTheme.Box(c, active ? Colors.White : new Color(1, 1, 1, 0.15f), 15, active ? 3 : 1);
        b.AddThemeStyleboxOverride("normal", sb);
        b.AddThemeStyleboxOverride("hover", sb);
        b.AddThemeStyleboxOverride("pressed", sb);
        b.AddThemeStyleboxOverride("focus", new StyleBoxEmpty());
    }
}
