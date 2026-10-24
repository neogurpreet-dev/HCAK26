using System;
using Godot;

namespace Atlas;

/// <summary>
/// Reusable bridge between two islands: stone deck with glowing side strips and light posts.
/// If <see cref="ModelOverride"/> is set the model is instanced instead and stretched along its local -Z.
/// </summary>
public partial class Bridge : Node3D
{
    [Export] public PackedScene ModelOverride { get; set; }
    [Export] public float Width { get; set; } = 1.9f;

    public StandardMaterial3D AccentMaterial { get; private set; }

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

        if (ModelOverride != null)
        {
            var m = ModelOverride.Instantiate<Node3D>();
            m.SetMeta("generated", true);
            m.Scale = new Vector3(Width, 1f, length);
            AddChild(m);
            return;
        }

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
        if (AccentMaterial == null) return;
        AccentMaterial.Emission = color;
        AccentMaterial.AlbedoColor = color.Darkened(0.55f);
        AccentMaterial.EmissionEnergyMultiplier = energy;
    }
}
