using System;
using Godot;

namespace Atlas;

/// <summary>
/// Reusable floating island. If <see cref="ModelOverride"/> is assigned (e.g. an imported .glb scene)
/// it is instanced and scaled to the requested radius; otherwise a polished procedural island is built:
/// faceted stone slab, neon rim band, inner guide ring and a tapering rock underside.
/// Rim and ring are separate accent meshes with their own emissive material (recolourable).
/// </summary>
public partial class FloatingPlatform : Node3D
{
    /// <summary>Optional real 3D model for the island (assumed ~1 unit radius, top surface at y = 0).</summary>
    [Export] public PackedScene ModelOverride { get; set; }
    [Export] public float SlabThickness { get; set; } = 0.8f;

    /// <summary>Recolour the model's cyan emissive strips / lights with each island's accent colour.</summary>
    [Export] public bool RecolorEmissive { get; set; } = true;
    /// <summary>Extra neon rings inside the deck. Off by default: they cut through the buildings.</summary>
    [Export] public bool InnerRings { get; set; } = false;
    /// <summary>Glow strength of the model's accents relative to the procedural islands (lower = less bloom).</summary>
    [Export] public float ModelGlowScale { get; set; } = 1.0f;
    /// <summary>Metal look for the model's armour/deck (non-emissive) materials: at least this metallic, at most this rough.</summary>
    [Export(PropertyHint.Range, "0,1,0.01")] public float MetalAmount { get; set; } = 0.92f;
    [Export(PropertyHint.Range, "0,1,0.01")] public float MetalRoughness { get; set; } = 0.3f;
    /// <summary>Hide the model's emissive diamond frames in the middle of the deck (they sit under the buildings).</summary>
    [Export] public bool HideDeckDiamonds { get; set; } = true;
    /// <summary>The island model has ~145 small meshes; letting them all cast shadows is very expensive. Buildings still cast onto the deck.</summary>
    [Export] public bool IslandCastsShadows { get; set; } = false;

    // ---- Default island model (sci_fi_floating_platform_no_spikes.glb) ----------------------------------
    // Measured from the glb (glTF units, Z-up): inset deck edge r=2.1, deck surface z=0.64, armour/slits out to r~2.94.
    public const string DefaultModelPath = "res://Assets/Environment/sci_fi_floating_platform_no_spikes.glb";
    const float NativeDeckRadius = 2.1f;
    const float NativeOuterRadius = 2.94f;
    const float NativeDeckTop = 0.64f;

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

    /// <summary>
    /// Visual outer radius of an island divided by its layout radius. The model's deck matches the layout radius,
    /// but its armour rim sticks out further, so spacing/bridges must use Radius * FootprintFactor.
    /// </summary>
    public static float FootprintFactor => DefaultModel != null ? NativeOuterRadius / NativeDeckRadius : 1f;

    public float Radius { get; private set; }
    public StandardMaterial3D AccentMaterial { get; private set; }
    BaseMaterial3D _stripMat, _lightMat;
    Color _accent = Colors.White;
    OmniLight3D _accentLight;
    public StandardMaterial3D StoneMaterial { get; private set; }

    public void Setup(float radius, Color accent, int seed, bool isHub)
    {
        Radius = radius;
        foreach (var c in GetChildren()) { if (c is Node n && n.HasMeta("generated")) n.QueueFree(); }

        AccentMaterial = MeshKit.Glow(accent, isHub ? 2.6f : 2.2f);
        _accent = accent;

        if (ModelOverride != null)
        {
            var model = ModelOverride.Instantiate<Node3D>();
            model.SetMeta("generated", true);
            model.Scale = Vector3.One * radius;
            AddChild(model);
            return;
        }

        if (DefaultModel != null) { BuildModelIsland(radius); return; }

        StoneMaterial = MeshKit.Solid(new Color("2B3550"), 0.2f, 0.78f);
        var rockMat = MeshKit.Solid(new Color("1B2338"), 0.1f, 0.9f);

        // Top slab (12-sided so it reads as a faceted stone plate).
        var slab = new MeshInstance3D
        {
            Name = "Slab",
            Mesh = new CylinderMesh { TopRadius = radius, BottomRadius = radius * 0.97f, Height = SlabThickness, RadialSegments = 12, Rings = 1 },
            MaterialOverride = StoneMaterial,
            Position = new Vector3(0, -SlabThickness * 0.5f, 0),
        };
        slab.SetMeta("generated", true);
        AddChild(slab);

        // Dark top plate slightly inset so the neon ring pops.
        var top = new MeshInstance3D
        {
            Name = "TopPlate",
            Mesh = new CylinderMesh { TopRadius = radius * 0.93f, BottomRadius = radius * 0.93f, Height = 0.06f, RadialSegments = 12, Rings = 1 },
            MaterialOverride = MeshKit.Solid(new Color("1A2440"), 0.5f, 0.55f),
            Position = new Vector3(0, 0.01f, 0),
        };
        top.SetMeta("generated", true);
        AddChild(top);

        // Neon rim band around the slab side.
        var rim = new MeshInstance3D
        {
            Name = "RimLight",
            Mesh = new CylinderMesh { TopRadius = radius * 1.006f, BottomRadius = radius * 1.0f, Height = 0.16f, RadialSegments = 12, Rings = 1 },
            MaterialOverride = AccentMaterial,
            Position = new Vector3(0, -SlabThickness * 0.62f, 0),
            CastShadow = GeometryInstance3D.ShadowCastingSetting.Off,
        };
        rim.SetMeta("generated", true);
        AddChild(rim);

        // Inner guide ring on the surface.
        var ring = new MeshInstance3D
        {
            Name = "InnerRing",
            Mesh = new TorusMesh { InnerRadius = radius * 0.80f, OuterRadius = radius * 0.80f + 0.12f, Rings = 48, RingSegments = 6 },
            MaterialOverride = AccentMaterial,
            Position = new Vector3(0, 0.06f, 0),
            CastShadow = GeometryInstance3D.ShadowCastingSetting.Off,
        };
        ring.SetMeta("generated", true);
        AddChild(ring);

        // Rock underside.
        var under = new MeshInstance3D
        {
            Name = "Underside",
            Mesh = MeshKit.RockUnderside(radius * 0.97f, radius * 0.95f + 2.0f, 12, seed),
            MaterialOverride = rockMat,
            Position = new Vector3(0, -SlabThickness, 0),
        };
        under.SetMeta("generated", true);
        AddChild(under);
    }

    /// <summary>
    /// Instances the sci-fi island glb, scaled so its deck radius equals the layout radius and its deck surface sits at y = 0
    /// (where buildings stand), then adds three neon guide rings that scale with it.
    /// </summary>
    void BuildModelIsland(float radius)
    {
        float s = radius / NativeDeckRadius;
        var holder = new Node3D { Name = "IslandModel", Scale = Vector3.One * s, Position = new Vector3(0, -NativeDeckTop * s, 0) };
        holder.SetMeta("generated", true);

        // The glb is Z-up; rotate -90 degrees about X so its deck faces +Y.
        var model = DefaultModel.Instantiate<Node3D>();
        model.Basis = new Basis(Vector3.Right, -Mathf.Pi / 2f);
        holder.AddChild(model);

        // Neon rings (native units, heights measured in the model's own frame; tube radius 0.05).
        // Only the outer halo is kept by default; the inner two sat above the deck and overlapped the buildings.
        (float r, float y)[] rings = InnerRings
            ? new[] { (2.95f, 0.26f), (1.95f, 0.60f), (0.95f, 0.741f) }
            : new[] { (2.95f, 0.26f) };
        AccentMaterial.EmissionEnergyMultiplier *= ModelGlowScale;
        for (int i = 0; i < rings.Length; i++)
        {
            holder.AddChild(new MeshInstance3D
            {
                Name = "NeonRing" + (i + 1),
                Mesh = new TorusMesh { InnerRadius = rings[i].r - 0.05f, OuterRadius = rings[i].r + 0.05f, Rings = 96, RingSegments = 8 },
                MaterialOverride = AccentMaterial,
                Position = new Vector3(0, rings[i].y, 0),
                CastShadow = GeometryInstance3D.ShadowCastingSetting.Off,
            });
        }
        AddChild(holder);

        // Coloured pool of light in the folder's accent colour so each island has its own mood.
        _accentLight = new OmniLight3D
        {
            Name = "AccentLight", LightColor = _accent, LightEnergy = 0.9f, LightSpecular = 0.3f,
            OmniRange = radius * 2.6f, Position = new Vector3(0, radius * 0.45f + 2f, 0), ShadowEnabled = false,
        };
        _accentLight.SetMeta("generated", true);
        AddChild(_accentLight);

        ProcessModelMaterials(model);
    }

    static readonly System.Collections.Generic.Dictionary<Mesh, Mesh> _flatCache = new();

    /// <summary>
    /// The glb ships with positions only (no normals), so Godot averages normals across shared vertices and the flat deck
    /// shades unevenly (one bright, blown-out side). Rebuild each surface un-indexed with proper flat face normals.
    /// Cached per source mesh, so every island shares the result.
    /// </summary>
    static Mesh FlatShaded(Mesh src)
    {
        if (src == null) return null;
        if (_flatCache.TryGetValue(src, out var cached) && GodotObject.IsInstanceValid(cached)) return cached;
        var am = new ArrayMesh { ResourceName = src.ResourceName };
        var st = new SurfaceTool();
        for (int i = 0; i < src.GetSurfaceCount(); i++)
        {
            st.Clear();
            st.CreateFrom(src, i);
            st.Deindex();
            st.GenerateNormals();
            st.SetMaterial(src.SurfaceGetMaterial(i));
            st.Commit(am);
        }
        _flatCache[src] = am;
        return am;
    }

    /// <summary>
    /// Walks the model's meshes: flat-shades them, sets the armour/deck materials to a polished-metal look and, optionally, swaps the cyan emissives for per-island accent copies.
    /// </summary>
    void ProcessModelMaterials(Node root)
    {
        _stripMat = null; _lightMat = null;
        var tuned = new System.Collections.Generic.Dictionary<BaseMaterial3D, BaseMaterial3D>();
        var stack = new System.Collections.Generic.Stack<Node>();
        stack.Push(root);
        while (stack.Count > 0)
        {
            var n = stack.Pop();
            foreach (var c in n.GetChildren()) stack.Push(c);
            if (n is not MeshInstance3D mi || mi.Mesh == null) continue;
            if (HideDeckDiamonds && n.Name.ToString().Contains("diamond light", StringComparison.OrdinalIgnoreCase)) { mi.Visible = false; continue; }
            mi.Mesh = FlatShaded(mi.Mesh);
            if (!IslandCastsShadows) mi.CastShadow = GeometryInstance3D.ShadowCastingSetting.Off;
            for (int i = 0; i < mi.Mesh.GetSurfaceCount(); i++)
            {
                if (mi.Mesh.SurfaceGetMaterial(i) is not BaseMaterial3D mat) continue;
                string name = mat.ResourceName ?? "";
                if (name.Contains("cyan emissive", StringComparison.OrdinalIgnoreCase))
                {
                    if (RecolorEmissive) mi.SetSurfaceOverrideMaterial(i, _stripMat ??= (BaseMaterial3D)mat.Duplicate());
                }
                else if (name.Contains("Pale cyan", StringComparison.OrdinalIgnoreCase))
                {
                    if (RecolorEmissive) mi.SetSurfaceOverrideMaterial(i, _lightMat ??= (BaseMaterial3D)mat.Duplicate());
                }
                else
                {
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
        SetAccent(_accent, (AccentMaterial?.EmissionEnergyMultiplier ?? 2.2f * ModelGlowScale) / Mathf.Max(ModelGlowScale, 0.01f)); // SetAccent re-applies the glow scale
    }

    /// <summary>Recolour only the neon accents (rim + ring).</summary>
    public void SetAccent(Color color, float energy)
    {
        _accent = color;
        if (_accentLight != null) _accentLight.LightColor = color;
        if (_stripMat != null || _lightMat != null || DefaultModel != null) energy *= ModelGlowScale;
        if (_stripMat != null)
        {
            _stripMat.EmissionEnabled = true;
            _stripMat.Emission = color;
            _stripMat.AlbedoColor = color.Darkened(0.55f);
            _stripMat.EmissionEnergyMultiplier = energy;
        }
        if (_lightMat != null)
        {
            var pale = color.Lerp(Colors.White, 0.6f);
            _lightMat.EmissionEnabled = true;
            _lightMat.Emission = pale;
            _lightMat.AlbedoColor = pale;
            _lightMat.EmissionEnergyMultiplier = energy;
        }
        if (AccentMaterial == null) return;
        AccentMaterial.Emission = color;
        AccentMaterial.AlbedoColor = color.Darkened(0.55f);
        AccentMaterial.EmissionEnergyMultiplier = energy;
    }
}
