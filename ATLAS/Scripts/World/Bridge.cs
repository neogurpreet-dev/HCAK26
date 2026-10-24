using System;
using System.Collections.Generic;
using Godot;

namespace Atlas;

/// <summary>
/// Reusable bridge between two islands.
/// Model order: <see cref="ModelOverride"/> if set, else the Neon Skybridge glb (Assets/Environment) if it is imported,
/// else the procedural stone deck with glowing strips and light posts.
/// The skybridge model is tiled end to end (uniform scale, so its textures are never smeared) and each tile is stretched
/// only slightly so the run fits the gap exactly. Its materials get the same polished-metal tuning as the islands.
/// </summary>
public partial class Bridge : Node3D
{
    [Export] public PackedScene ModelOverride { get; set; }
    [Export] public float Width { get; set; } = 1.9f;

    /// <summary>Off = the original procedural bridge (stone deck, glowing strips, lantern posts). On = the Neon Skybridge model.</summary>
    [Export] public bool UseSkybridge { get; set; } = false;

    /// <summary>Uniform scale of the skybridge model (native deck is ~0.45 wide, 1.9 long). 3.3 gives a ~1.5 wide deck.</summary>
    [Export(PropertyHint.Range, "0.5,8,0.05")] public float ModelScale { get; set; } = 3.3f;
    /// <summary>Metal look for the skybridge: at least this metallic, at most this rough (same idea as FloatingPlatform).</summary>
    [Export(PropertyHint.Range, "0,1,0.01")] public float MetalAmount { get; set; } = 0.92f;
    [Export(PropertyHint.Range, "0,1,0.01")] public float MetalRoughness { get; set; } = 0.3f;
    /// <summary>The model has ~45k vertices per tile; shadows from it are not worth the cost.</summary>
    [Export] public bool ModelCastsShadows { get; set; } = false;

    /// <summary>Neon glow of the skybridge, like the islands' strips: only the neon lines glow (a mask baked from the model's texture),
    /// in the island's accent colour. The dark metal stays dark.</summary>
    [Export] public bool ModelGlow { get; set; } = false;
    [Export(PropertyHint.Range, "0,6,0.05")] public float ModelGlowEnergy { get; set; } = 2.2f;
    public const string EmissionMaskPath = "res://Assets/Environment/Neon_Skybridge_emission.png";
    static Texture2D _mask;
    static bool _maskProbed;
    static Texture2D EmissionMask
    {
        get
        {
            if (!_maskProbed)
            {
                _maskProbed = true;
                if (ResourceLoader.Exists(EmissionMaskPath)) _mask = ResourceLoader.Load<Texture2D>(EmissionMaskPath);
            }
            return _mask;
        }
    }

    public const string DefaultModelPath = "res://Assets/Environment/Neon_Skybridge.glb";
    // Measured from the glb: length along X, deck walking surface at y = 0.065.
    const float NativeLength = 1.9f;
    const float NativeDeckTop = 0.065f;

    static PackedScene _defaultModel;
    static bool _modelProbed;
    static PackedScene DefaultModel
    {
        get
        {
            if (!_modelProbed)
            {
                _modelProbed = true;
                if (ResourceLoader.Exists(DefaultModelPath)) _defaultModel = ResourceLoader.Load<PackedScene>(DefaultModelPath);
            }
            return _defaultModel;
        }
    }

    public StandardMaterial3D AccentMaterial { get; private set; }

    readonly List<BaseMaterial3D> _glowMats = new List<BaseMaterial3D>();
    Color _accent = Colors.Cyan;

    public void Setup(Vector3 from, Vector3 to, Color accent)
    {
        foreach (var c in GetChildren()) { if (c is Node n && n.HasMeta("generated")) n.QueueFree(); }

        var delta = to - from;
        float length = delta.Length();
        if (length < 0.5f) { Visible = false; return; }
        Visible = true;

        Position = (from + to) * 0.5f;
        // Local -Z points from -> to
        Rotation = new Vector3(0, Mathf.Atan2(-delta.X, -delta.Z), 0);

        AccentMaterial = MeshKit.Glow(accent, 2.4f);
        _accent = accent;
        _glowMats.Clear();

        if (ModelOverride != null)
        {
            var m = ModelOverride.Instantiate<Node3D>();
            m.SetMeta("generated", true);
            m.Scale = new Vector3(Width, 1f, length);
            AddChild(m);
            return;
        }

        var sky = UseSkybridge ? DefaultModel : null;
        if (sky != null && BuildSkybridge(sky, length)) return;

        BuildProcedural(length);
    }

    bool BuildSkybridge(PackedScene model, float length)
    {
        float s = Mathf.Max(0.05f, ModelScale);
        float tile = NativeLength * s;
        int count = Mathf.Max(1, Mathf.RoundToInt(length / tile));
        float stretch = length / (count * tile);          // close to 1 for normal gaps
        float seg = length / count;

        var tuned = new Dictionary<BaseMaterial3D, BaseMaterial3D>();
        for (int i = 0; i < count; i++)
        {
            var m = model.Instantiate<Node3D>();
            if (m == null) return false;
            m.SetMeta("generated", true);
            // The model's length is its local X. Turn it onto -Z; scale is applied before the turn, so X carries the stretch.
            m.Rotation = new Vector3(0, Mathf.Pi * 0.5f, 0);
            m.Scale = new Vector3(s * stretch, s, s);
            m.Position = new Vector3(0, -NativeDeckTop * s, -length * 0.5f + seg * (i + 0.5f));
            AddChild(m);
            TuneMetal(m, tuned);
        }
        foreach (var t in tuned.Values) _glowMats.Add(t);
        ApplyModelGlow(_accent, ModelGlowEnergy);
        return true;
    }

    /// <summary>Same treatment as the islands: keep the textures, push metallic up and roughness down (copies, never the shared import).</summary>
    void TuneMetal(Node root, Dictionary<BaseMaterial3D, BaseMaterial3D> tuned)
    {
        var stack = new Stack<Node>();
        stack.Push(root);
        while (stack.Count > 0)
        {
            var n = stack.Pop();
            foreach (var c in n.GetChildren()) stack.Push(c);
            if (n is not MeshInstance3D mi || mi.Mesh == null) continue;
            if (!ModelCastsShadows) mi.CastShadow = GeometryInstance3D.ShadowCastingSetting.Off;
            for (int i = 0; i < mi.Mesh.GetSurfaceCount(); i++)
            {
                if (mi.Mesh.SurfaceGetMaterial(i) is not BaseMaterial3D mat) continue;
                if (!tuned.TryGetValue(mat, out var t))
                {
                    t = (BaseMaterial3D)mat.Duplicate();
                    t.Metallic = Mathf.Max(t.Metallic, MetalAmount);
                    t.Roughness = Mathf.Min(t.Roughness, MetalRoughness);
                    t.MetallicSpecular = 0.7f;
                    tuned[mat] = t;
                }
                mi.SetSurfaceOverrideMaterial(i, t);
            }
        }
    }

    void ApplyModelGlow(Color accent, float energy)
    {
        foreach (var t in _glowMats)
        {
            var mask = EmissionMask;
            if (!ModelGlow || mask == null) { t.EmissionEnabled = false; continue; }
            t.EmissionEnabled = true;
            t.EmissionOperator = BaseMaterial3D.EmissionOperatorEnum.Add;
            t.EmissionTexture = mask;
            t.Emission = accent;
            t.EmissionEnergyMultiplier = energy;
        }
    }

    void BuildProcedural(float length)
    {
        var deck = new MeshInstance3D
        {
            Name = "Deck",
            Mesh = MeshKit.BeveledBox(new Vector3(Width, 0.3f, length), 0.07f),
            MaterialOverride = MeshKit.Solid(new Color("3A4766"), 0.3f, 0.6f),
            Position = new Vector3(0, -0.15f, 0),
        };
        deck.SetMeta("generated", true);
        AddChild(deck);

        foreach (float side in new[] { -1f, 1f })
        {
            var strip = new MeshInstance3D
            {
                Name = side < 0 ? "StripLeft" : "StripRight",
                Mesh = new BoxMesh { Size = new Vector3(0.1f, 0.1f, length * 0.98f) },
                MaterialOverride = AccentMaterial,
                Position = new Vector3(side * (Width * 0.5f - 0.05f), 0.04f, 0),
                CastShadow = GeometryInstance3D.ShadowCastingSetting.Off,
            };
            strip.SetMeta("generated", true);
            AddChild(strip);
        }

        // Lantern posts (accent-coloured so they recolour with the strips).
        int posts = Mathf.Max(2, (int)(length / 6f));
        for (int i = 0; i <= posts; i++)
        {
            float z = -length * 0.5f + length * i / posts;
            foreach (float side in new[] { -1f, 1f })
            {
                var post = new MeshInstance3D
                {
                    Name = $"Post_{i}_{(side < 0 ? "L" : "R")}",
                    Mesh = new BoxMesh { Size = new Vector3(0.14f, 0.34f, 0.14f) },
                    MaterialOverride = AccentMaterial,
                    Position = new Vector3(side * (Width * 0.5f - 0.05f), 0.17f, z),
                    CastShadow = GeometryInstance3D.ShadowCastingSetting.Off,
                };
                post.SetMeta("generated", true);
                AddChild(post);
            }
        }
    }

    public void SetAccent(Color color, float energy)
    {
        _accent = color;
        if (_glowMats.Count > 0) ApplyModelGlow(color, ModelGlowEnergy * energy / 2.4f);
        if (AccentMaterial == null) return;
        AccentMaterial.Emission = color;
        AccentMaterial.AlbedoColor = color.Darkened(0.55f);
        AccentMaterial.EmissionEnergyMultiplier = energy;
    }
}
