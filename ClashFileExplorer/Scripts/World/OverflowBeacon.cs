using Godot;

namespace ClashExplorer.World;

/// <summary>
/// In-world "+" structure shown on the outer ring when a folder holds more items than
/// MaxVisibleBuildings. It builds its own children, so attaching this script to a plain Node2D
/// (OverflowPlusNode) is all the setup it needs. Clicking it emits Activated; the owner decides
/// what to do (Part 2 opens the folder in Windows Explorer).
/// </summary>
public partial class OverflowBeacon : Node2D
{
    /// <summary>The "+" beacon was clicked with the left mouse button.</summary>
    [Signal] public delegate void ActivatedEventHandler();

    [Export(PropertyHint.Range, "30,200,1")] public float Radius { get; set; } = 70f;
    [Export] public Color BeaconColor { get; set; } = new Color(0.35f, 0.85f, 1f);

    private Label? _label;
    private Area2D? _area;
    private int _count;
    private bool _hover;
    private double _time;

    public override void _Ready()
    {
        ZIndex = 5;

        _label = new Label
        {
            Text = "",
            HorizontalAlignment = HorizontalAlignment.Center,
            MouseFilter = Control.MouseFilterEnum.Ignore,
            Position = new Vector2(-150f, 12f),
            Size = new Vector2(300f, 32f)
        };
        _label.AddThemeFontSizeOverride("font_size", 22);
        _label.AddThemeColorOverride("font_color", Colors.White);
        _label.AddThemeColorOverride("font_outline_color", new Color(0f, 0f, 0f, 0.85f));
        _label.AddThemeConstantOverride("outline_size", 8);
        AddChild(_label);

        _area = new Area2D { Monitoring = false, Monitorable = false, InputPickable = true };
        var shape = new CollisionShape2D
        {
            Shape = new CircleShape2D { Radius = Radius },
            Position = new Vector2(0f, -Radius)
        };
        _area.AddChild(shape);
        AddChild(_area);

        _area.InputEvent += OnAreaInputEvent;
        _area.MouseEntered += OnMouseEntered;
        _area.MouseExited += OnMouseExited;
        VisibilityChanged += OnVisibilityChanged;

        SetProcess(IsVisibleInTree());
        UpdateLabel();
    }

    public override void _ExitTree()
    {
        if (_area != null && IsInstanceValid(_area))
        {
            _area.InputEvent -= OnAreaInputEvent;
            _area.MouseEntered -= OnMouseEntered;
            _area.MouseExited -= OnMouseExited;
        }
        VisibilityChanged -= OnVisibilityChanged;
    }

    /// <summary>Sets the number of hidden items shown under the beacon.</summary>
    public void SetCount(int hiddenCount)
    {
        _count = hiddenCount < 0 ? 0 : hiddenCount;
        UpdateLabel();
        QueueRedraw();
    }

    public override void _Process(double delta)
    {
        _time += delta;
        QueueRedraw(); // gentle pulse; only runs while the beacon is visible
    }

    public override void _Draw()
    {
        float pulse = 0.5f + 0.5f * Mathf.Sin((float)_time * 2.2f);
        Vector2 center = new Vector2(0f, -Radius);
        float r = Radius * (_hover ? 1.08f : 1f);

        Color glow = BeaconColor;
        glow.A = 0.10f + 0.10f * pulse;
        DrawCircle(center, r * 1.25f, glow);

        Color fill = new Color(0.04f, 0.08f, 0.12f, 0.85f);
        DrawCircle(center, r, fill);

        Color ring = BeaconColor;
        ring.A = _hover ? 1f : 0.75f + 0.2f * pulse;
        DrawArc(center, r, 0f, Mathf.Tau, 64, ring, 4f, true);

        float arm = r * 0.45f;
        DrawLine(center + new Vector2(-arm, 0f), center + new Vector2(arm, 0f), ring, 8f, true);
        DrawLine(center + new Vector2(0f, -arm), center + new Vector2(0f, arm), ring, 8f, true);
    }

    private void UpdateLabel()
    {
        if (_label == null) return;
        _label.Text = _count > 0 ? $"+ {_count:N0} more items..." : "";
    }

    private void OnVisibilityChanged() => SetProcess(IsVisibleInTree());

    private void OnMouseEntered()
    {
        _hover = true;
        QueueRedraw();
    }

    private void OnMouseExited()
    {
        _hover = false;
        QueueRedraw();
    }

    private void OnAreaInputEvent(Node viewport, InputEvent @event, long shapeIdx)
    {
        if (@event is InputEventMouseButton mb && mb.Pressed && mb.ButtonIndex == MouseButton.Left)
        {
            EmitSignal(SignalName.Activated);
        }
    }
}
