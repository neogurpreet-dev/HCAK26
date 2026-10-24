using System;
using Godot;

namespace ClashExplorer.World;

/// <summary>
/// One building in the world = one real file or folder. The node origin is the building's ground
/// contact point (bottom-center of the sprite), so a Y-sorted parent orders buildings correctly.
///
/// Stage 4 (Part 1) scope: data, visuals, tweened move/scale, hover and left-click signals.
/// Part 2 extends this class with selection visuals, context-menu invocation and richer animation.
///
/// Scene: Scenes/FileBuilding.tscn
///   FileBuilding (Node2D, this script)
///     Sprite2D
///     Area2D
///       CollisionShape2D
///     OptionalVisualEffects (Node2D)
///     OptionalBuildingLabelAnchor (Marker2D)
/// </summary>
public partial class FileBuilding : Node2D
{
    /// <summary>Mouse entered the building's area.</summary>
    [Signal] public delegate void HoverStartedEventHandler(FileBuilding building);

    /// <summary>Mouse left the building's area.</summary>
    [Signal] public delegate void HoverEndedEventHandler(FileBuilding building);

    /// <summary>Left mouse button pressed on the building (single click).</summary>
    [Signal] public delegate void ClickedEventHandler(FileBuilding building);

    /// <summary>Left mouse button double-clicked on the building (open file / enter folder).</summary>
    [Signal] public delegate void DoubleClickedEventHandler(FileBuilding building);

    private const double DefaultTweenSeconds = 0.45;

    /// <summary>Data this building represents. Null until Setup() is called.</summary>
    public EntryMetrics? Metrics { get; private set; }

    /// <summary>Absolute path of the real file or folder (the building's identity).</summary>
    public string Key => Metrics?.Path ?? "";

    public bool IsFolder => Metrics?.IsFolder ?? false;

    /// <summary>Scale currently targeted by the building (before any hover/selection effects).</summary>
    public float VisualScale { get; private set; } = 1f;

    /// <summary>Radius of the ground footprint used by the layout. Set by the WorldGenerator.</summary>
    public float FootprintRadius { get; set; } = 60f;

    /// <summary>0..1 value from the Glow mapping.</summary>
    public float GlowIntensity { get; private set; }

    public Color Tint { get; private set; } = Colors.White;

    public Vector2 TextureSize { get; private set; } = new Vector2(128f, 128f);

    private Sprite2D? _sprite;
    private Area2D? _area;
    private CollisionShape2D? _collision;
    private Node2D? _effects;
    private Marker2D? _labelAnchor;
    private RectangleShape2D? _shape;
    private Tween? _positionTween;
    private Tween? _scaleTween;
    private bool _ready;

    public override void _Ready()
    {
        _sprite = GetNode<Sprite2D>("Sprite2D");
        _area = GetNode<Area2D>("Area2D");
        _collision = GetNode<CollisionShape2D>("Area2D/CollisionShape2D");
        _effects = GetNode<Node2D>("OptionalVisualEffects");
        _labelAnchor = GetNode<Marker2D>("OptionalBuildingLabelAnchor");

        _shape = new RectangleShape2D { Size = new Vector2(96f, 96f) };
        _collision.Shape = _shape;

        _area.MouseEntered += OnMouseEntered;
        _area.MouseExited += OnMouseExited;
        _area.InputEvent += OnAreaInputEvent;

        _ready = true;
        ApplyVisuals();
    }

    public override void _ExitTree()
    {
        KillTweens();
        if (_area != null && IsInstanceValid(_area))
        {
            _area.MouseEntered -= OnMouseEntered;
            _area.MouseExited -= OnMouseExited;
            _area.InputEvent -= OnAreaInputEvent;
        }
    }

    // ------------------------------------------------------------------ setup / update

    /// <summary>
    /// Assigns the file/folder data and look. May be called before or after the node enters the tree.
    /// The scale is applied immediately; use ScaleTo() afterwards for animated changes.
    /// </summary>
    public void Setup(EntryMetrics metrics, Texture2D texture, float scale, Color tint, float glow)
    {
        Metrics = metrics;
        TextureSize = texture.GetSize();
        VisualScale = SanitizeScale(scale);
        Tint = tint;
        GlowIntensity = Mathf.Clamp(glow, 0f, 1f);
        Scale = new Vector2(VisualScale, VisualScale);
        Name = MakeNodeName(metrics.Name);

        _texture = texture;
        if (_ready) ApplyVisuals();
    }

    /// <summary>Refreshes data after a filesystem change (size, timestamps, usage) without recreating the node.</summary>
    public void UpdateMetrics(EntryMetrics metrics)
    {
        Metrics = metrics;
    }

    /// <summary>Updates tint/glow (for example after a mapping edit) and optionally the sprite.</summary>
    public void UpdateLook(Color tint, float glow, Texture2D? texture = null)
    {
        Tint = tint;
        GlowIntensity = Mathf.Clamp(glow, 0f, 1f);
        if (texture != null)
        {
            _texture = texture;
            TextureSize = texture.GetSize();
        }
        if (_ready) ApplyVisuals();
    }

    private Texture2D? _texture;

    private void ApplyVisuals()
    {
        if (_sprite == null || _collision == null || _labelAnchor == null || _shape == null) return;

        Vector2 size = TextureSize;
        _sprite.Texture = _texture;
        _sprite.Centered = false;
        _sprite.Offset = new Vector2(-size.X * 0.5f, -size.Y); // pivot at bottom-center

        float boost = 1f + 0.35f * GlowIntensity;
        _sprite.SelfModulate = new Color(Tint.R * boost, Tint.G * boost, Tint.B * boost, Tint.A);

        _shape.Size = new Vector2(size.X * 0.7f, size.Y * 0.8f);
        _collision.Position = new Vector2(0f, -size.Y * 0.5f);

        _labelAnchor.Position = new Vector2(0f, -size.Y - 8f);
    }

    /// <summary>Global position above the sprite where a name label should be drawn.</summary>
    public Vector2 GetLabelAnchorGlobalPosition()
    {
        return _labelAnchor != null ? _labelAnchor.GlobalPosition : GlobalPosition;
    }

    /// <summary>Container for optional per-building effects (particles, glow sprites) added in Part 2.</summary>
    public Node2D? EffectsRoot => _effects;

    // ------------------------------------------------------------------ animation

    /// <summary>Moves to a new position. Replaces any move already in progress.</summary>
    public void MoveTo(Vector2 position, double duration = DefaultTweenSeconds)
    {
        _positionTween?.Kill();
        _positionTween = null;

        if (duration <= 0.0 || !IsInsideTree())
        {
            Position = position;
            return;
        }

        _positionTween = CreateTween();
        _positionTween.SetTrans(Tween.TransitionType.Cubic).SetEase(Tween.EaseType.Out);
        _positionTween.TweenProperty(this, "position", position, duration);
    }

    /// <summary>Changes the building scale. Replaces any scale animation already in progress.</summary>
    public void ScaleTo(float scale, double duration = DefaultTweenSeconds)
    {
        VisualScale = SanitizeScale(scale);
        _scaleTween?.Kill();
        _scaleTween = null;

        var target = new Vector2(VisualScale, VisualScale);
        if (duration <= 0.0 || !IsInsideTree())
        {
            Scale = target;
            return;
        }

        _scaleTween = CreateTween();
        _scaleTween.SetTrans(Tween.TransitionType.Cubic).SetEase(Tween.EaseType.Out);
        _scaleTween.TweenProperty(this, "scale", target, duration);
    }

    /// <summary>Grows the building from nothing to its target scale (used when it first appears).</summary>
    public void PopIn(double duration = 0.4)
    {
        float target = VisualScale;
        Scale = new Vector2(0.01f, 0.01f);
        ScaleTo(target, duration);
    }

    private void KillTweens()
    {
        _positionTween?.Kill();
        _scaleTween?.Kill();
        _positionTween = null;
        _scaleTween = null;
    }

    // ------------------------------------------------------------------ input

    private void OnMouseEntered() => EmitSignal(SignalName.HoverStarted, this);

    private void OnMouseExited() => EmitSignal(SignalName.HoverEnded, this);

    private void OnAreaInputEvent(Node viewport, InputEvent @event, long shapeIdx)
    {
        if (@event is not InputEventMouseButton mb) return;
        if (!mb.Pressed || mb.ButtonIndex != MouseButton.Left) return;

        if (mb.DoubleClick) EmitSignal(SignalName.DoubleClicked, this);
        else EmitSignal(SignalName.Clicked, this);
    }

    // ------------------------------------------------------------------ helpers

    private static float SanitizeScale(float scale)
    {
        return float.IsFinite(scale) && scale > 0.05f ? scale : 1f;
    }

    /// <summary>Node names cannot contain certain characters; keep it readable and safe.</summary>
    private static string MakeNodeName(string fileName)
    {
        if (string.IsNullOrWhiteSpace(fileName)) return "Building";
        var chars = fileName.ToCharArray();
        for (int i = 0; i < chars.Length; i++)
        {
            char c = chars[i];
            if (c == '.' || c == ':' || c == '@' || c == '/' || c == '"' || c == '%') chars[i] = '_';
        }
        return new string(chars);
    }
}
