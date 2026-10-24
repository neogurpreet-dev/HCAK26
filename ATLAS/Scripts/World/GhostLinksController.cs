using System;
using System.Collections.Generic;
using Godot;

namespace Atlas;

/// <summary>
/// Ghost Links: a translucent platform above the SELECTED building showing the files and applications that the learned
/// relationship model (see <see cref="RelationshipIndex"/>) says are used together with it.
///
/// Static by construction. The platform root is placed ONCE, at
///     building world position + (0, building height + PlatformClearance, 0)
/// and every representation sits at a fixed local offset computed by a deterministic layout (row-major in score order, rows
/// centred). No tween, no _Process write, and no input handler ever changes a position. The only per-frame work is uniform
/// scaling of the name labels so they stay legible (billboards face the camera; their world positions are untouched).
///
/// Nothing is shown when no related entity qualifies: no empty platform, placeholder or warning.
/// </summary>
public partial class GhostLinksController : Node3D
{
    public Camera3D Camera { get; set; }
    public WorldInteraction Interaction { get; set; }
    public WorldGenerator Generator { get; set; }
    public ActivityStore Store { get; set; }

    [ExportGroup("Placement")]
    [Export] public float PlatformClearance { get; set; } = 4.2f;
    [Export] public float CellSize { get; set; } = 2.3f;
    [Export] public int MaxItems { get; set; } = 8;

    [ExportGroup("Labels")]
    [Export] public float LabelScreenPx { get; set; } = 15f;
    [Export] public float MaxLabelScale { get; set; } = 1.4f;

    const int LabelFontSize = 32;
    const float LabelPixelSize = 0.0042f;
    const float PadEnergy = 2.2f, PadEnergyHover = 4.4f;
    const float Margin = 0.5f, StripDepth = 1.7f;

    Node3D _root;
    FileBuilding _building;
    readonly List<GhostEntity> _items = new List<GhostEntity>();
    readonly List<Label3D> _labels = new List<Label3D>();
    Label3D _padLabel;
    StandardMaterial3D _padMat;

    /// <summary>The items currently on the platform, in display order (what Open All launches).</summary>
    public IReadOnlyList<GhostEntity> CurrentItems => _items;
    public bool HasPlatform => _root != null;
    public bool IsShowingFor(FileBuilding b) => _root != null && b != null && b == _building;

    public override void _Ready()
    {
        if (Interaction != null) Interaction.SelectionChanged += OnSelectionChanged;
        if (Store != null) Store.SettingsChanged += Refresh;
        SetProcess(false);
    }

    public override void _ExitTree()
    {
        if (Interaction != null) Interaction.SelectionChanged -= OnSelectionChanged;
        if (Store != null) Store.SettingsChanged -= Refresh;
    }

    void OnSelectionChanged(FileBuilding b) { Refresh(); }

    // ------------------------------------------------------------------ refresh

    /// <summary>Rebuilds (or removes) the platform for the current selection. Called on selection and sensitivity changes.</summary>
    public void Refresh()
    {
        Clear();
        var b = Interaction != null ? Interaction.Selected : null;
        if (b == null || !IsInstanceValid(b) || b.Entry == null || b.Entry.IsDirectory || Store == null) return;

        var items = new List<GhostEntity>();
        foreach (var r in Store.GetRelated(ActivityIds.File(b.Entry.Path)))
        {
            var info = Store.GetEntity(r.Id);
            if (info == null) continue;
            var g = new GhostEntity
            {
                Id = r.Id, Type = r.Type, Path = info.Path, Score = r.Score, Sessions = r.Sessions,
                Name = string.IsNullOrEmpty(info.Name) ? System.IO.Path.GetFileName(info.Path) : info.Name,
            };
            if (!QuickLaunch.IsAvailable(g)) continue; // deleted / moved / unavailable entities are never displayed
            items.Add(g);
            if (items.Count >= MaxItems) break;
        }
        if (items.Count == 0) return; // nothing qualifies: show nothing at all
        Build(b, items);
    }

    void Clear()
    {
        if (_root != null)
        {
            RemoveChild(_root); // leaves the tree immediately, so its pick area cannot be hit again
            _root.QueueFree();
        }
        _root = null;
        _building = null;
        _items.Clear();
        _labels.Clear();
        _padLabel = null;
        _padMat = null;
        SetProcess(false);
    }

    // ------------------------------------------------------------------ construction

    void Build(FileBuilding b, List<GhostEntity> items)
    {
        _building = b;
        _items.AddRange(items);

        _root = new Node3D { Name = "GhostPlatform" };
        AddChild(_root);
        // The one and only placement of the platform. Nothing below ever changes it.
        _root.GlobalPosition = b.GlobalPosition + Vector3.Up * (b.Height + PlatformClearance);

        int n = items.Count;
        int cols = (int)Math.Ceiling(Math.Sqrt(n));
        int rows = (int)Math.Ceiling(n / (double)cols);
        float itemsW = Math.Max(cols * CellSize, 2.9f);
        float itemsD = rows * CellSize;
        float w = itemsW + Margin * 2f;
        float d = itemsD + StripDepth + Margin * 2f;

        // ---- slab + glowing rim + tether down to the roof
        var slabMat = MeshKit.Solid(new Color(0.03f, 0.09f, 0.2f), 0.2f, 0.5f);
        MeshKit.SetAlpha(slabMat, 0.88f);
        _root.AddChild(new MeshInstance3D
        {
            Name = "Slab", Mesh = MeshKit.BeveledBox(new Vector3(w, 0.22f, d), 0.07f), MaterialOverride = slabMat,
            Position = new Vector3(0, -0.11f, 0), CastShadow = GeometryInstance3D.ShadowCastingSetting.Off,
        });
        _root.AddChild(new MeshInstance3D
        {
            Name = "Rim", Mesh = MeshKit.BeveledBox(new Vector3(w + 0.14f, 0.05f, d + 0.14f), 0.02f), MaterialOverride = MeshKit.Glow(AtlasTheme.Cyan, 1.8f),
            Position = new Vector3(0, -0.2f, 0), CastShadow = GeometryInstance3D.ShadowCastingSetting.Off,
        });

        float anchorY = _root.GlobalPosition.Y;
        float roofY = b.GlobalPosition.Y + b.Height + 0.12f;
        float bottomY = anchorY - 0.22f;
        float tetherH = Math.Max(0.1f, bottomY - roofY);
        var tetherMat = MeshKit.Glow(AtlasTheme.Cyan, 1.2f);
        MeshKit.SetAlpha(tetherMat, 0.5f);
        _root.AddChild(new MeshInstance3D
        {
            Name = "Tether", Mesh = new CylinderMesh { TopRadius = 0.035f, BottomRadius = 0.035f, Height = tetherH, RadialSegments = 6, Rings = 1 },
            MaterialOverride = tetherMat, Position = new Vector3(0, bottomY - tetherH * 0.5f - anchorY, 0),
            CastShadow = GeometryInstance3D.ShadowCastingSetting.Off,
        });

        // ---- representations: row-major in score order, each row centred (deterministic)
        for (int i = 0; i < n; i++)
        {
            int row = i / cols, col = i % cols;
            int inRow = Math.Min(cols, n - row * cols);
            float x = (col - (inRow - 1) * 0.5f) * CellSize;
            float z = -d * 0.5f + Margin + CellSize * (row + 0.5f);
            AddRepresentation(items[i], i, n, new Vector3(x, 0f, z));
        }

        // ---- Open All control on its own strip at the front edge: never overlaps a representation
        float padZ = d * 0.5f - Margin - StripDepth * 0.5f + 0.1f;
        BuildOpenAllPad(new Vector3(0f, 0f, padZ));

        SetProcess(true);
    }

    void AddRepresentation(GhostEntity g, int index, int total, Vector3 local)
    {
        bool app = g.Type == EntityType.Application;
        Color col = app ? new Color("8E8BFF") : AtlasTheme.Cyan;       // apps violet-blue, files cyan
        float norm = total <= 1 ? 1f : 1f - index / (float)(total - 1);  // rank based, so it is stable
        float h = 0.9f + 0.9f * norm;

        var body = MeshKit.Glow(col, 0.9f);
        MeshKit.SetAlpha(body, 0.30f);
        var rim = MeshKit.Glow(col, 2.0f);
        MeshKit.SetAlpha(rim, 0.85f);

        Mesh bodyMesh, capMesh;
        if (app)
        {
            bodyMesh = new CylinderMesh { TopRadius = 0.52f, BottomRadius = 0.52f, Height = h, RadialSegments = 20, Rings = 1 };
            capMesh = new CylinderMesh { TopRadius = 0.55f, BottomRadius = 0.55f, Height = 0.06f, RadialSegments = 20, Rings = 1 };
        }
        else
        {
            bodyMesh = MeshKit.BeveledBox(new Vector3(0.95f, h, 0.95f), 0.07f);
            capMesh = MeshKit.BeveledBox(new Vector3(1.0f, 0.06f, 1.0f), 0.025f);
        }

        _root.AddChild(new MeshInstance3D
        {
            Name = "Ghost" + index, Mesh = bodyMesh, MaterialOverride = body,
            Position = local + new Vector3(0, h * 0.5f, 0), CastShadow = GeometryInstance3D.ShadowCastingSetting.Off,
        });
        _root.AddChild(new MeshInstance3D
        {
            Name = "GhostCap" + index, Mesh = capMesh, MaterialOverride = rim,
            Position = local + new Vector3(0, h + 0.03f, 0), CastShadow = GeometryInstance3D.ShadowCastingSetting.Off,
        });

        string iconName = app ? "app" : IconLib.IconNameFor(new FsEntry { Category = FileCategory.Classify(System.IO.Path.GetExtension(g.Path)), IsDirectory = false });
        _root.AddChild(new Sprite3D
        {
            Name = "GhostIcon" + index,
            Texture = IconLib.Get(iconName, col.Lerp(Colors.White, 0.55f), 64, 1.8f),
            PixelSize = 0.012f,
            Billboard = BaseMaterial3D.BillboardModeEnum.Enabled,
            Shaded = false,
            Transparent = true,
            TextureFilter = BaseMaterial3D.TextureFilterEnum.LinearWithMipmaps,
            Position = local + new Vector3(0, h + 0.62f, 0),
        });

        var label = NewLabel("GhostLabel" + index, InfoPanelView.Elide(g.Name, 22), local + new Vector3(0, 0.05f, 0.82f));
        _root.AddChild(label);
        _labels.Add(label);
    }

    void BuildOpenAllPad(Vector3 local)
    {
        _padMat = MeshKit.Glow(AtlasTheme.Cyan, PadEnergy);

        var pad = new Node3D { Name = "OpenAllPad", Position = local };
        _root.AddChild(pad);
        pad.AddChild(new MeshInstance3D
        {
            Name = "PadMesh", Mesh = MeshKit.BeveledBox(new Vector3(2.9f, 0.18f, 0.95f), 0.06f), MaterialOverride = _padMat,
            Position = new Vector3(0, 0.09f, 0), CastShadow = GeometryInstance3D.ShadowCastingSetting.Off,
        });

        var area = new Area3D { Name = "OpenAllArea", CollisionLayer = 8, CollisionMask = 0, Monitoring = false, Monitorable = true };
        area.AddChild(new CollisionShape3D { Shape = new BoxShape3D { Size = new Vector3(3.1f, 1.9f, 1.2f) }, Position = new Vector3(0, 0.8f, 0) });
        area.SetMeta("ghost_open_all", true);
        pad.AddChild(area);

        pad.AddChild(new Sprite3D
        {
            Name = "PadIcon", Texture = IconLib.Get("external", Colors.White, 64, 2.0f), PixelSize = 0.011f,
            Billboard = BaseMaterial3D.BillboardModeEnum.Enabled, Shaded = false, Transparent = true,
            TextureFilter = BaseMaterial3D.TextureFilterEnum.LinearWithMipmaps, Position = new Vector3(0, 1.0f, 0),
        });

        _padLabel = NewLabel("PadLabel", "Open All", local + new Vector3(0, 0.45f, 0));
        _root.AddChild(_padLabel);
        _labels.Add(_padLabel);
    }

    static Label3D NewLabel(string name, string text, Vector3 position)
    {
        return new Label3D
        {
            Name = name, Text = text,
            Billboard = BaseMaterial3D.BillboardModeEnum.Enabled,
            NoDepthTest = true,
            FontSize = LabelFontSize,
            PixelSize = LabelPixelSize,
            OutlineSize = 10,
            Modulate = new Color("EAF8FF"),
            OutlineModulate = new Color(0.02f, 0.05f, 0.12f, 1f),
            Position = position,
        };
    }

    // ------------------------------------------------------------------ hover feedback for the Open All pad

    public void SetPadHover(bool on)
    {
        if (_padMat != null) _padMat.EmissionEnergyMultiplier = on ? PadEnergyHover : PadEnergy;
        if (_padLabel != null && IsInstanceValid(_padLabel)) _padLabel.Text = on ? "Open All Related Items" : "Open All";
    }

    // ------------------------------------------------------------------ per frame: label legibility only

    public override void _Process(double delta)
    {
        if (_root == null || Camera == null) return;
        foreach (var l in _labels)
        {
            if (!IsInstanceValid(l)) continue;
            float s = LabelScreenPx * WorldScale.PerPixel(Camera, l.GlobalPosition) / (LabelFontSize * LabelPixelSize);
            s = Mathf.Clamp(s, 0.05f, MaxLabelScale);
            l.Scale = new Vector3(s, s, s);
        }
    }
}
