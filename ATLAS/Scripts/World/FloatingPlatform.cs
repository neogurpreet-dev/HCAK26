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

    public float Radius { get; private set; }
    public StandardMaterial3D AccentMaterial { get; private set; }
    public StandardMaterial3D StoneMaterial { get; private set; }

    public void Setup(float radius, Color accent, int seed, bool isHub)
    {
        Radius = radius;
        foreach (var c in GetChildren()) { if (c is Node n && n.HasMeta("generated")) n.QueueFree(); }

        AccentMaterial = MeshKit.Glow(accent, isHub ? 2.6f : 2.2f);

        if (ModelOverride != null)
        {
            var model = ModelOverride.Instantiate<Node3D>();
            model.SetMeta("generated", true);
            model.Scale = Vector3.One * radius;
            AddChild(model);
            return;
        }

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

    /// <summary>Recolour only the neon accents (rim + ring).</summary>
    public void SetAccent(Color color, float energy)
    {
        if (AccentMaterial == null) return;
        AccentMaterial.Emission = color;
        AccentMaterial.AlbedoColor = color.Darkened(0.55f);
        AccentMaterial.EmissionEnergyMultiplier = energy;
    }
}
