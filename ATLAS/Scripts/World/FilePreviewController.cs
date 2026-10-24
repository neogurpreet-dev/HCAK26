using System;
using System.Collections.Generic;
using System.Threading.Tasks;
using Godot;

namespace Atlas;

/// <summary>Helpers for keeping 3D objects readable without moving them.</summary>
public static class WorldScale
{
    /// <summary>World units covered by one screen pixel at the depth of <paramref name="worldPos"/>.</summary>
    public static float PerPixel(Camera3D cam, Vector3 worldPos)
    {
        var vp = cam.GetViewport();
        float h = vp != null ? vp.GetVisibleRect().Size.Y : 900f;
        if (h < 1f) h = 900f;
        float depth = Mathf.Abs((worldPos - cam.GlobalPosition).Dot(-cam.GlobalTransform.Basis.Z));
        depth = Mathf.Max(depth, 0.5f);
        return 2f * depth * Mathf.Tan(Mathf.DegToRad(cam.Fov) * 0.5f) / h;
    }
}

/// <summary>
/// Hover preview. ONE 3D quad (a SubViewport rendered onto a billboarded mesh) that is placed at
///     building world position + (0, building height + 0.8 + ClearanceAboveLabel, 0)
/// the moment a building is hovered, and then never moves: not with the mouse, not with the camera, no bobbing or easing.
///
/// What may change while it is visible, and what may not:
///  * position (world space) : fixed for as long as the same building stays hovered. Nothing writes it afterwards.
///  * orientation            : billboard, so the panel always faces the camera (allowed: it does not move the panel).
///  * uniform scale          : with ConstantScreenSize the quad is scaled about its fixed anchor so its text stays legible when
///                             zooming; set ConstantScreenSize = false for a pure fixed world size.
/// The hover never launches anything. The panel is hidden for the selected building while its Ghost Links platform is shown.
/// </summary>
public partial class FilePreviewController : Node3D
{
    public Camera3D Camera { get; set; }
    public WorldInteraction Interaction { get; set; }
    public WorldGenerator Generator { get; set; }
    /// <summary>Return true to keep the panel hidden for this building (set by MainWorld: the Ghost Links platform is up).</summary>
    public Func<FileBuilding, bool> SuppressFor { get; set; }

    [ExportGroup("Placement")]
    [Export] public float ClearanceAboveLabel { get; set; } = 2.4f;
    [Export] public bool ConstantScreenSize { get; set; } = true;
    /// <summary>1 = the panel texture is shown pixel-for-pixel on screen.</summary>
    [Export] public float ScreenScale { get; set; } = 1.0f;
    [Export] public float HideDelaySeconds { get; set; } = 0.15f;

    const int PanelW = 480, PanelH = 300;
    const float BaseWorldW = 4.8f, BaseWorldH = 3.0f; // 100 texture pixels per world unit at scale 1
    const int CacheLimit = 48;

    SubViewport _vp;
    TextureRect _icon, _thumb;
    Label _title, _sub, _note;
    RichTextLabel _rtl;
    MeshInstance3D _quad;

    FileBuilding _target;
    Vector3 _anchor;
    int _version;
    double _clock;
    double _hideAt = -1;
    readonly Dictionary<string, PreviewData> _cache = new Dictionary<string, PreviewData>();
    readonly Queue<string> _cacheOrder = new Queue<string>();

    public override void _Ready()
    {
        BuildViewport();
        BuildQuad();
        if (Interaction != null) Interaction.HoverChanged += OnHoverChanged;
        if (Generator != null) Generator.WorldRebuilt += HideNow;
        SetProcess(false);
    }

    public override void _ExitTree()
    {
        if (Interaction != null) Interaction.HoverChanged -= OnHoverChanged;
        if (Generator != null) Generator.WorldRebuilt -= HideNow;
    }

    // ------------------------------------------------------------------ construction

    void BuildViewport()
    {
        _vp = new SubViewport
        {
            Name = "PreviewViewport",
            Size = new Vector2I(PanelW, PanelH),
            TransparentBg = true,
            Disable3D = true,
            GuiDisableInput = true,
            RenderTargetUpdateMode = SubViewport.UpdateMode.Disabled,
        };
        AddChild(_vp);

        var root = new Control { Name = "PreviewRoot", Theme = AtlasTheme.Build(), MouseFilter = Control.MouseFilterEnum.Ignore };
        root.SetAnchorsPreset(Control.LayoutPreset.FullRect);
        _vp.AddChild(root);

        var panel = new PanelContainer { MouseFilter = Control.MouseFilterEnum.Ignore };
        panel.AddThemeStyleboxOverride("panel", AtlasTheme.Box(new Color(0.03f, 0.07f, 0.17f, 0.95f), new Color(0.2f, 0.78f, 0.93f, 0.75f), 14, 2, 14, 12));
        panel.SetAnchorsPreset(Control.LayoutPreset.FullRect);
        root.AddChild(panel);

        var v = new VBoxContainer { MouseFilter = Control.MouseFilterEnum.Ignore };
        v.AddThemeConstantOverride("separation", 8);
        panel.AddChild(v);

        var head = new HBoxContainer { MouseFilter = Control.MouseFilterEnum.Ignore };
        head.AddThemeConstantOverride("separation", 12);
        v.AddChild(head);

        _icon = new TextureRect
        {
            CustomMinimumSize = new Vector2(44, 44),
            ExpandMode = TextureRect.ExpandModeEnum.IgnoreSize,
            StretchMode = TextureRect.StretchModeEnum.KeepAspectCentered,
            MouseFilter = Control.MouseFilterEnum.Ignore,
        };
        head.AddChild(_icon);

        var names = new VBoxContainer { SizeFlagsHorizontal = Control.SizeFlags.ExpandFill, MouseFilter = Control.MouseFilterEnum.Ignore };
        names.AddThemeConstantOverride("separation", 2);
        head.AddChild(names);

        _title = AtlasTheme.MakeLabel("", 20, Colors.White, bold: true);
        _title.ClipText = true;
        _title.TextOverrunBehavior = TextServer.OverrunBehavior.TrimEllipsis;
        names.AddChild(_title);
        _sub = AtlasTheme.MakeLabel("", 13, AtlasTheme.TextDim);
        _sub.ClipText = true;
        _sub.TextOverrunBehavior = TextServer.OverrunBehavior.TrimEllipsis;
        names.AddChild(_sub);

        v.AddChild(new HSeparator());

        var body = new Control { SizeFlagsVertical = Control.SizeFlags.ExpandFill, ClipContents = true, MouseFilter = Control.MouseFilterEnum.Ignore, CustomMinimumSize = new Vector2(0, 150) };
        v.AddChild(body);

        _rtl = new RichTextLabel { BbcodeEnabled = true, ScrollActive = false, AutowrapMode = TextServer.AutowrapMode.Off, MouseFilter = Control.MouseFilterEnum.Ignore };
        _rtl.SetAnchorsPreset(Control.LayoutPreset.FullRect);
        _rtl.AddThemeFontSizeOverride("normal_font_size", 13);
        _rtl.AddThemeColorOverride("default_color", AtlasTheme.Text);
        _rtl.AddThemeFontOverride("normal_font", new SystemFont { FontNames = new[] { "Consolas", "Cascadia Mono", "Courier New", "monospace" } });
        body.AddChild(_rtl);

        _thumb = new TextureRect
        {
            ExpandMode = TextureRect.ExpandModeEnum.IgnoreSize,
            StretchMode = TextureRect.StretchModeEnum.KeepAspectCentered,
            MouseFilter = Control.MouseFilterEnum.Ignore,
            Visible = false,
        };
        _thumb.SetAnchorsPreset(Control.LayoutPreset.FullRect);
        body.AddChild(_thumb);

        _note = AtlasTheme.MakeLabel("", 14, AtlasTheme.TextDim);
        _note.AutowrapMode = TextServer.AutowrapMode.WordSmart;
        _note.SetAnchorsPreset(Control.LayoutPreset.FullRect);
        body.AddChild(_note);
    }

    void BuildQuad()
    {
        var mat = new StandardMaterial3D
        {
            ShadingMode = BaseMaterial3D.ShadingModeEnum.Unshaded,
            Transparency = BaseMaterial3D.TransparencyEnum.Alpha,
            AlbedoTexture = _vp.GetTexture(),
            BillboardMode = BaseMaterial3D.BillboardModeEnum.Enabled,
            BillboardKeepScale = true,
            CullMode = BaseMaterial3D.CullModeEnum.Disabled,
            DisableReceiveShadows = true,
            TextureFilter = BaseMaterial3D.TextureFilterEnum.Linear,
            RenderPriority = 5,
        };
        _quad = new MeshInstance3D
        {
            Name = "PreviewQuad",
            Mesh = new QuadMesh { Size = new Vector2(BaseWorldW, BaseWorldH), CenterOffset = new Vector3(0, BaseWorldH * 0.5f, 0) }, // bottom edge sits on the anchor
            MaterialOverride = mat,
            CastShadow = GeometryInstance3D.ShadowCastingSetting.Off,
            Visible = false,
        };
        AddChild(_quad);
    }

    // ------------------------------------------------------------------ hover handling

    void OnHoverChanged(FileBuilding b)
    {
        if (b == null || !IsInstanceValid(b) || b.Entry == null)
        {
            if (_target != null) _hideAt = _clock + HideDelaySeconds; // brief anti-flicker delay
            return;
        }
        if (SuppressFor != null && SuppressFor(b)) { HideNow(); return; }
        if (_target == b && _quad.Visible) { _hideAt = -1; return; }
        ShowFor(b);
    }

    void ShowFor(FileBuilding b)
    {
        _target = b;
        _hideAt = -1;

        // Fixed anchor: building base + building height + fixed offsets. Written once here, never again while hovered.
        _anchor = new Vector3(b.GlobalPosition.X, b.GlobalPosition.Y + b.Height + 0.8f + ClearanceAboveLabel, b.GlobalPosition.Z);
        _quad.GlobalPosition = _anchor;
        _quad.Scale = Vector3.One;

        var e = b.Entry;
        _version++;
        int myVersion = _version;

        var cat = FileCategory.CategoryColor(e.Category);
        _icon.Texture = IconLib.ForEntry(e, cat.Lerp(Colors.White, 0.35f), 64);
        _title.Text = e.Name;
        _sub.Text = FilePreviewService.Subtitle(e);

        string key = e.Path + "|" + e.Modified.Ticks + "|" + e.Size;
        PreviewData cached;
        if (_cache.TryGetValue(key, out cached)) ApplyData(cached);
        else
        {
            ApplyNote("Loading preview...");
            Task.Run(() =>
            {
                PreviewData d;
                try { d = FilePreviewService.Build(e); }
                catch (Exception ex) { d = FilePreviewService.Failed(e, ex.Message); }
                Callable.From(() => OnLoaded(myVersion, key, d)).CallDeferred();
            });
        }

        _quad.Visible = true;
        _vp.RenderTargetUpdateMode = SubViewport.UpdateMode.Always;
        SetProcess(true);
    }

    void OnLoaded(int version, string key, PreviewData d)
    {
        if (d.Thumb != null)
        {
            d.ThumbTexture = ImageTexture.CreateFromImage(d.Thumb);
            d.Thumb = null;
        }
        if (!_cache.ContainsKey(key))
        {
            _cache[key] = d;
            _cacheOrder.Enqueue(key);
            while (_cacheOrder.Count > CacheLimit) _cache.Remove(_cacheOrder.Dequeue());
        }
        if (version == _version && _target != null) ApplyData(d);
    }

    void ApplyData(PreviewData d)
    {
        _rtl.Visible = false;
        _thumb.Visible = false;
        _note.Visible = false;
        switch (d.Kind)
        {
            case PreviewKind.Text:
            case PreviewKind.Folder:
                _rtl.Text = d.Body;
                _rtl.Visible = true;
                break;
            case PreviewKind.Image:
                if (d.ThumbTexture != null) { _thumb.Texture = d.ThumbTexture; _thumb.Visible = true; }
                else ApplyNote("Image preview unavailable.");
                break;
            default:
                ApplyNote(d.Note);
                break;
        }
    }

    void ApplyNote(string text)
    {
        _rtl.Visible = false;
        _thumb.Visible = false;
        _note.Text = text;
        _note.Visible = true;
    }

    void HideNow()
    {
        if (_quad != null) _quad.Visible = false;
        if (_vp != null) _vp.RenderTargetUpdateMode = SubViewport.UpdateMode.Disabled;
        _target = null;
        _hideAt = -1;
        SetProcess(false);
    }

    public override void _Process(double delta)
    {
        _clock += delta;
        if (_target == null || !IsInstanceValid(_target)) { HideNow(); return; }
        if (SuppressFor != null && SuppressFor(_target)) { HideNow(); return; }
        if (_hideAt >= 0 && _clock >= _hideAt) { HideNow(); return; }

        if (ConstantScreenSize && Camera != null)
        {
            // uniform scale about the fixed anchor so the text keeps a constant on-screen size; position is not touched
            float s = Mathf.Clamp(WorldScale.PerPixel(Camera, _anchor) * 100f * ScreenScale, 0.05f, 40f);
            _quad.Scale = new Vector3(s, s, s);
        }
    }
}
