using System;
using Godot;

namespace Atlas;

/// <summary>
/// Hover label: a single frosted-glass mini board with the name of whatever building the cursor is pointing at, floating just
/// above it. The board is screen-space UI anchored to the building's LabelAnchor, so it always faces the camera exactly.
///
/// Cheap by design: it listens to WorldInteraction.HoverChanged (the existing physics ray pick) instead of scanning buildings,
/// owns ONE board (one glass backdrop = one screen read), assigns text only when the hovered building changes, and switches its
/// own _Process off completely while nothing is hovered.
///
/// Created from code by MainWorld. Required: Camera, CameraRig, Interaction, Generator.
/// </summary>
public partial class CursorProximityLabelManager : Node3D
{
    // ---- wired by MainWorld
    public Camera3D Camera { get; set; }
    public WorldCamera CameraRig { get; set; }
    public WorldInteraction Interaction { get; set; }
    public WorldGenerator Generator { get; set; }

    [ExportGroup("Behaviour")]
    [Export] public bool HideWhileOrbiting { get; set; } = true;
    [Export] public float FadeSpeed { get; set; } = 14f;

    [ExportGroup("Float")]
    /// <summary>Extra world height above the building's label anchor.</summary>
    [Export] public float FloatHeight { get; set; } = 0.5f;

    [ExportGroup("Board")]
    /// <summary>Frosted-glass shader backdrop. Turn off for a flat translucent board if you ever need the extra frames.</summary>
    [Export] public bool UseGlassShader { get; set; } = true;
    [Export] public int FontSize { get; set; } = 14;
    [Export] public Color TextColor { get; set; } = new Color("EAF8FF");
    [Export] public Color BorderColor { get; set; } = new Color(0.75f, 0.92f, 1f, 0.55f);
    [Export] public int CornerRadius { get; set; } = 9;
    [Export] public int MaxChars { get; set; } = 40;

    CanvasLayer _canvas;
    PanelContainer _panel;
    Label _label;
    FileBuilding _target;
    Vector2 _lastSize;
    float _alpha;

    public override void _Ready()
    {
        _canvas = new CanvasLayer { Name = "ProximityLabelCanvas", Layer = 30 }; // above everything, incl. the island "Code / N items" cards (HUD is layer 10)
        AddChild(_canvas);
        var root = new Control { Name = "BoardRoot", MouseFilter = Control.MouseFilterEnum.Ignore, Theme = AtlasTheme.Build() };
        root.SetAnchorsPreset(Control.LayoutPreset.FullRect);
        _canvas.AddChild(root);

        BuildBoard(root);

        if (Interaction != null) Interaction.HoverChanged += OnHoverChanged;
        if (Generator != null) Generator.WorldRebuilt += Clear;
        SetProcess(false);
    }

    public override void _ExitTree()
    {
        if (Interaction != null) Interaction.HoverChanged -= OnHoverChanged;
        if (Generator != null) Generator.WorldRebuilt -= Clear;
    }

    void BuildBoard(Control root)
    {
        // Glass backdrop (frosted_glass.gdshader, same as the HUD) + content + hairline border; all mouse-transparent.
        var shader = UseGlassShader ? GD.Load<Shader>("res://Shaders/frosted_glass.gdshader") : null;
        _panel = AtlasTheme.GlassPanel(shader, CornerRadius);
        _panel.Name = "ProximityBoard";
        _panel.MouseFilter = Control.MouseFilterEnum.Ignore;
        _panel.Visible = false;

        var margin = new MarginContainer { MouseFilter = Control.MouseFilterEnum.Ignore };
        margin.AddThemeConstantOverride("margin_left", 12);
        margin.AddThemeConstantOverride("margin_right", 12);
        margin.AddThemeConstantOverride("margin_top", 6);
        margin.AddThemeConstantOverride("margin_bottom", 6);
        _panel.AddChild(margin);

        _label = AtlasTheme.MakeLabel("", FontSize, TextColor);
        margin.AddChild(_label);

        var border = new Panel { MouseFilter = Control.MouseFilterEnum.Ignore };
        border.AddThemeStyleboxOverride("panel", AtlasTheme.Box(Colors.Transparent, BorderColor, CornerRadius, 1));
        _panel.AddChild(border);

        root.AddChild(_panel);
    }

    // ------------------------------------------------------------------ events

    void OnHoverChanged(FileBuilding b)
    {
        _target = b != null && b.Entry != null ? b : null;
        if (_target != null)
        {
            _label.Text = ShortName(_target.Entry.Name); // text is only touched when the hovered building changes
            _lastSize = Vector2.Zero;
        }
        SetProcess(true);
    }

    void Clear()
    {
        _target = null;
        _alpha = 0f;
        _panel.Visible = false;
        SetProcess(false);
    }

    // ------------------------------------------------------------------ per-frame (only runs while a board is showing / fading)

    public override void _Process(double delta)
    {
        if (Camera == null) return;

        bool show = _target != null && GodotObject.IsInstanceValid(_target)
                    && !(HideWhileOrbiting && (CameraRig?.IsInteracting ?? false));
        float goal = show ? 1f : 0f;
        _alpha = Mathf.Lerp(_alpha, goal, 1f - Mathf.Exp(-FadeSpeed * (float)delta));

        if (!show && _alpha < 0.02f)
        {
            _alpha = 0f;
            _panel.Visible = false;
            if (_target == null || !GodotObject.IsInstanceValid(_target)) SetProcess(false);
            return;
        }
        if (!GodotObject.IsInstanceValid(_target)) return;

        // Floating point above the roof (LabelAnchor sits just over the building). Static: no bobbing.
        var anchor = _target.LabelAnchor != null ? _target.LabelAnchor.GlobalPosition : _target.GlobalPosition + Vector3.Up * (_target.Height + 0.8f);
        var world = anchor + Vector3.Up * (FloatHeight);
        if (Camera.IsPositionBehind(world)) { _panel.Visible = false; return; }

        var size = _panel.GetCombinedMinimumSize();
        if (size != _lastSize) { _panel.Size = size; _lastSize = size; }

        // Screen-space board = always faces the camera. Bottom-centre of the board sits on the anchor point.
        var sp = Camera.UnprojectPosition(world);
        _panel.Position = (sp - new Vector2(size.X * 0.5f, size.Y)).Round();
        _panel.Modulate = new Color(1f, 1f, 1f, _alpha);
        _panel.Visible = true;
    }

    string ShortName(string name)
    {
        if (string.IsNullOrEmpty(name)) return "";
        return name.Length > MaxChars ? name[..(MaxChars - 1)] + "…" : name;
    }
}
