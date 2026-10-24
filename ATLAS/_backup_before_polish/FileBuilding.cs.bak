using System;
using Godot;

namespace Atlas;

public enum BuildingShape { Box = 0, Cylinder = 1, Tiered = 2 }

/// <summary>Everything the generator decides about a building's look (from the property mapping).</summary>
public struct BuildingStyle
{
    public float HalfExtent;
    public float Height;
    public BuildingShape Shape;
    public Color BaseColor;
    public Color AccentColor;
    public float Glow;
    public float Opacity;
    public Texture2D Icon;
    public Texture2D AlbedoTexture;
    public Material MaterialOverride;
}

/// <summary>
/// One real file/folder, generated at runtime from bevelled boxes / cylinders.
/// Hierarchy (FileBuilding.tscn): Body, AccentCap, AccentStripA, AccentStripB, IconPlate,
/// SelectionRing, HitArea/HitShape, LabelAnchor. Base and accent use separate materials.
/// </summary>
public partial class FileBuilding : Node3D
{
    public FsEntry Entry { get; private set; }
    public BuildingStyle Style { get; private set; }
    public float Height => Style.Height;
    public StandardMaterial3D BaseMaterial { get; private set; }
    public StandardMaterial3D AccentMaterial { get; private set; }
    public bool IsHovered { get; private set; }
    public bool IsSelected { get; private set; }
    public Marker3D LabelAnchor { get; private set; }

    MeshInstance3D _body, _cap, _stripA, _stripB, _ring;
    Sprite3D _icon;
    Area3D _area;
    CollisionShape3D _shape;
    Tween _scaleTween;
    float _baseGlow = 2f;
    bool _dimmed;

    public override void _Ready()
    {
        EnsureNodes();
    }

    T Ensure<T>(string name) where T : Node, new()
    {
        var n = GetNodeOrNull<T>(name);
        if (n == null) { n = new T { Name = name }; AddChild(n); }
        return n;
    }

    void EnsureNodes()
    {
        if (_body != null) return;
        _body = Ensure<MeshInstance3D>("Body");
        _cap = Ensure<MeshInstance3D>("AccentCap");
        _stripA = Ensure<MeshInstance3D>("AccentStripA");
        _stripB = Ensure<MeshInstance3D>("AccentStripB");
        _ring = Ensure<MeshInstance3D>("SelectionRing");
        _icon = Ensure<Sprite3D>("IconPlate");
        _area = Ensure<Area3D>("HitArea");
        _shape = _area.GetNodeOrNull<CollisionShape3D>("HitShape");
        if (_shape == null) { _shape = new CollisionShape3D { Name = "HitShape" }; _area.AddChild(_shape); }
        LabelAnchor = Ensure<Marker3D>("LabelAnchor");
        _area.CollisionLayer = 2; // "Buildings"
        _area.CollisionMask = 0;
        _area.Monitoring = false;
        _area.Monitorable = true;
        _body.CastShadow = GeometryInstance3D.ShadowCastingSetting.On;
        _cap.CastShadow = _stripA.CastShadow = _stripB.CastShadow = _ring.CastShadow = GeometryInstance3D.ShadowCastingSetting.Off;
    }

    public void Setup(FsEntry entry, BuildingStyle style, bool animateIn)
    {
        EnsureNodes();
        Entry = entry;
        Style = style;
        Name = SanitiseNodeName(entry.Name);

        float he = style.HalfExtent, h = style.Height, w = he * 2f;
        float bevel = Mathf.Min(0.2f, w * 0.16f);

        // ---- body (base material is separate from the accent material)
        BaseMaterial = null;
        if (style.MaterialOverride is StandardMaterial3D userStd)
        {
            BaseMaterial = (StandardMaterial3D)userStd.Duplicate();
            _body.MaterialOverride = BaseMaterial;
        }
        else if (style.MaterialOverride != null)
        {
            _body.MaterialOverride = style.MaterialOverride;
        }
        else
        {
            BaseMaterial = MeshKit.Solid(style.BaseColor);
            if (style.AlbedoTexture != null) BaseMaterial.AlbedoTexture = style.AlbedoTexture;
            MeshKit.SetAlpha(BaseMaterial, style.Opacity);
            _body.MaterialOverride = BaseMaterial;
        }

        switch (style.Shape)
        {
            case BuildingShape.Cylinder:
                _body.Mesh = new CylinderMesh { TopRadius = he, BottomRadius = he, Height = h, RadialSegments = 20, Rings = 1 };
                break;
            case BuildingShape.Tiered:
                _body.Mesh = MeshKit.TieredTower(w, h, bevel);
                break;
            default:
                _body.Mesh = MeshKit.BeveledBox(new Vector3(w, h, w), bevel);
                break;
        }
        _body.Position = new Vector3(0, h * 0.5f, 0);

        // ---- accents (one shared emissive material, independent of the base)
        AccentMaterial = MeshKit.Glow(style.AccentColor, style.Glow);
        _baseGlow = style.Glow;
        _cap.MaterialOverride = AccentMaterial;
        _stripA.MaterialOverride = AccentMaterial;
        _stripB.MaterialOverride = AccentMaterial;

        float capW = style.Shape == BuildingShape.Tiered ? w * 0.62f + 0.12f : w + 0.12f;
        if (style.Shape == BuildingShape.Cylinder)
            _cap.Mesh = new CylinderMesh { TopRadius = he + 0.06f, BottomRadius = he + 0.06f, Height = 0.12f, RadialSegments = 20, Rings = 1 };
        else
            _cap.Mesh = MeshKit.BeveledBox(new Vector3(capW, 0.12f, capW), 0.04f);
        _cap.Position = new Vector3(0, h + 0.06f, 0);

        float stripH = Mathf.Max(0.4f, h * 0.78f);
        float faceOffset = style.Shape == BuildingShape.Tiered ? he : he;
        _stripA.Mesh = new BoxMesh { Size = new Vector3(Mathf.Min(0.16f, w * 0.12f), stripH, 0.05f) };
        _stripA.Position = new Vector3(-he * 0.55f, h * 0.5f, faceOffset + 0.02f);
        _stripB.Mesh = new BoxMesh { Size = new Vector3(0.05f, stripH * 0.8f, Mathf.Min(0.16f, w * 0.12f)) };
        _stripB.Position = new Vector3(faceOffset + 0.02f, h * 0.46f, he * 0.5f);
        if (style.Shape == BuildingShape.Cylinder) { _stripA.Visible = false; _stripB.Visible = false; }

        // ---- decal
        if (style.Icon != null && w >= 1.3f)
        {
            _icon.Visible = true;
            _icon.Texture = style.Icon;
            _icon.Shaded = false;
            _icon.DoubleSided = false;
            _icon.TextureFilter = BaseMaterial3D.TextureFilterEnum.LinearWithMipmaps;
            _icon.Transparent = true;
            float target = Mathf.Min(w * 0.55f, 1.6f);
            _icon.PixelSize = target / Mathf.Max(1, style.Icon.GetWidth());
            float iy = Mathf.Clamp(h * 0.55f, target * 0.6f, Mathf.Max(target * 0.6f, h - target * 0.7f));
            _icon.Position = new Vector3(0, iy, he + 0.07f);
            if (style.Shape == BuildingShape.Cylinder) _icon.Position = new Vector3(0, iy, he + 0.04f);
        }
        else _icon.Visible = false;

        // ---- selection ring (hidden until selected)
        var ringMat = MeshKit.Glow(new Color("FFFFFF"), 2.5f);
        _ring.MaterialOverride = ringMat;
        _ring.Mesh = new TorusMesh { InnerRadius = he * 1.45f, OuterRadius = he * 1.45f + 0.09f, Rings = 28, RingSegments = 6 };
        _ring.Position = new Vector3(0, 0.05f, 0);
        _ring.Visible = false;

        // ---- hit area
        _shape.Shape = new BoxShape3D { Size = new Vector3(w, h, w) };
        _shape.Position = new Vector3(0, h * 0.5f, 0);
        LabelAnchor.Position = new Vector3(0, h + 0.8f, 0);

        if (animateIn)
        {
            Scale = new Vector3(1f, 0.001f, 1f);
            var t = CreateTween().SetTrans(Tween.TransitionType.Back).SetEase(Tween.EaseType.Out);
            t.TweenProperty(this, "scale", Vector3.One, 0.45f + GD.Randf() * 0.25f);
        }
        else Scale = Vector3.One;
    }

    /// <summary>Re-applies colours/glow/opacity only (cheap restyle when geometry is unchanged).</summary>
    public void Restyle(Color baseColor, Color accent, float glow, float opacity)
    {
        if (BaseMaterial != null && Style.MaterialOverride == null) { BaseMaterial.AlbedoColor = new Color(baseColor, BaseMaterial.AlbedoColor.A); MeshKit.SetAlpha(BaseMaterial, opacity); }
        if (AccentMaterial != null)
        {
            AccentMaterial.Emission = accent;
            AccentMaterial.AlbedoColor = accent.Darkened(0.55f);
            AccentMaterial.EmissionEnergyMultiplier = glow;
            _baseGlow = glow;
        }
    }

    public void SetHover(bool on)
    {
        if (IsHovered == on) return;
        IsHovered = on;
        if (AccentMaterial != null) AccentMaterial.EmissionEnergyMultiplier = on ? _baseGlow * 1.8f : (_dimmed ? _baseGlow * 0.25f : _baseGlow);
        AnimateScale(on || IsSelected ? 1.06f : 1f);
    }

    public void SetSelected(bool on)
    {
        IsSelected = on;
        if (_ring != null) _ring.Visible = on;
        AnimateScale(on || IsHovered ? 1.06f : 1f);
    }

    /// <summary>Used by search: dims non-matching buildings without hiding them.</summary>
    public void SetDimmed(bool dimmed)
    {
        _dimmed = dimmed;
        if (BaseMaterial != null && Style.MaterialOverride == null) MeshKit.SetAlpha(BaseMaterial, dimmed ? 0.18f : Style.Opacity);
        if (AccentMaterial != null) AccentMaterial.EmissionEnergyMultiplier = dimmed ? _baseGlow * 0.25f : _baseGlow;
        if (_icon != null) _icon.Modulate = new Color(1, 1, 1, dimmed ? 0.2f : 1f);
    }

    void AnimateScale(float s)
    {
        _scaleTween?.Kill();
        _scaleTween = CreateTween().SetTrans(Tween.TransitionType.Cubic).SetEase(Tween.EaseType.Out);
        _scaleTween.TweenProperty(this, "scale", new Vector3(s, 1f, s), 0.15f);
    }

    static string SanitiseNodeName(string n)
    {
        // Godot node names cannot contain . : @ / " %
        var chars = n.ToCharArray();
        for (int i = 0; i < chars.Length; i++)
            if (chars[i] == '.' || chars[i] == ':' || chars[i] == '@' || chars[i] == '/' || chars[i] == '"' || chars[i] == '%' || chars[i] == '\\') chars[i] = '_';
        var s = new string(chars);
        return s.Length == 0 ? "Item" : (s.Length > 48 ? s.Substring(0, 48) : s);
    }

    public override void _ExitTree()
    {
        _scaleTween?.Kill();
    }
}
