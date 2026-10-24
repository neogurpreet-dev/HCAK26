using System;
using Godot;

namespace Atlas;

/// <summary>Procedural meshes + materials. Everything in the world is built from these primitives.</summary>
public static class MeshKit
{
    // ------------------------------------------------------------------ materials

    public static StandardMaterial3D Solid(Color color, float metallic = 0.35f, float roughness = 0.38f)
    {
        return new StandardMaterial3D { AlbedoColor = color, Metallic = metallic, Roughness = roughness };
    }

    /// <summary>Emissive "neon" material used for all accent elements (rims, strips, caps, markers).</summary>
    public static StandardMaterial3D Glow(Color color, float energy = 2.2f)
    {
        return new StandardMaterial3D
        {
            AlbedoColor = color.Darkened(0.55f),
            EmissionEnabled = true,
            Emission = color,
            EmissionEnergyMultiplier = energy,
            Roughness = 0.3f,
        };
    }

    public static void SetAlpha(StandardMaterial3D m, float alpha)
    {
        if (m == null) return;
        var c = m.AlbedoColor; c.A = alpha; m.AlbedoColor = c;
        m.Transparency = alpha < 0.995f ? BaseMaterial3D.TransparencyEnum.Alpha : BaseMaterial3D.TransparencyEnum.Disabled;
    }

    // ------------------------------------------------------------------ bevelled box

    /// <summary>Chamfered box (flat shaded). Origin at its centre.</summary>
    public static ArrayMesh BeveledBox(Vector3 size, float bevel)
    {
        var st = new SurfaceTool();
        st.Begin(Mesh.PrimitiveType.Triangles);
        AddBeveledBox(st, Vector3.Zero, size, bevel);
        st.GenerateNormals();
        return st.Commit();
    }

    /// <summary>Two stacked boxes (a "tiered tower"), origin at the centre of the whole height.</summary>
    public static ArrayMesh TieredTower(float width, float height, float bevel)
    {
        var st = new SurfaceTool();
        st.Begin(Mesh.PrimitiveType.Triangles);
        float h1 = height * 0.68f;
        float h2 = height - h1;
        AddBeveledBox(st, new Vector3(0, -height * 0.5f + h1 * 0.5f, 0), new Vector3(width, h1, width), bevel);
        AddBeveledBox(st, new Vector3(0, -height * 0.5f + h1 + h2 * 0.5f, 0), new Vector3(width * 0.62f, h2, width * 0.62f), bevel * 0.8f);
        st.GenerateNormals();
        return st.Commit();
    }

    static void AddBeveledBox(SurfaceTool st, Vector3 center, Vector3 size, float bevel)
    {
        var h = size * 0.5f;
        float b = Mathf.Clamp(bevel, 0.001f, Mathf.Min(h.X, Mathf.Min(h.Y, h.Z)) * 0.9f);
        var inner = new Vector3(h.X - b, h.Y - b, h.Z - b);
        var signs = new[] { -1f, 1f };

        // 6 faces
        for (int a = 0; a < 3; a++)
        {
            int ub = (a + 1) % 3, vc = (a + 2) % 3;
            foreach (var s in signs)
            {
                Vector3 P(float u, float v)
                {
                    var p = new Vector3();
                    p[a] = s * h[a];
                    p[ub] = u * inner[ub];
                    p[vc] = v * inner[vc];
                    return p + center;
                }
                Quad(st, P(-1, -1), P(1, -1), P(1, 1), P(-1, 1), center);
            }
        }

        // 12 edges
        for (int a = 0; a < 3; a++)
        {
            int bAxis = (a + 1) % 3, c = (a + 2) % 3;
            foreach (var sa in signs)
            foreach (var sb in signs)
            {
                Vector3 E(bool outerA, float along)
                {
                    var p = new Vector3();
                    p[a] = sa * (outerA ? h[a] : inner[a]);
                    p[bAxis] = sb * (outerA ? inner[bAxis] : h[bAxis]);
                    p[c] = along * inner[c];
                    return p + center;
                }
                Quad(st, E(true, -1), E(true, 1), E(false, 1), E(false, -1), center);
            }
        }

        // 8 corners
        foreach (var sx in signs)
        foreach (var sy in signs)
        foreach (var sz in signs)
        {
            var A = new Vector3(sx * h.X, sy * inner.Y, sz * inner.Z) + center;
            var B = new Vector3(sx * inner.X, sy * h.Y, sz * inner.Z) + center;
            var C = new Vector3(sx * inner.X, sy * inner.Y, sz * h.Z) + center;
            Tri(st, A, B, C, center);
        }
    }

    static void Quad(SurfaceTool st, Vector3 a, Vector3 b, Vector3 c, Vector3 d, Vector3 solidCenter)
    {
        Tri(st, a, b, c, solidCenter);
        Tri(st, a, c, d, solidCenter);
    }

    /// <summary>Adds a triangle wound clockwise as seen from outside (Godot's front face).</summary>
    static void Tri(SurfaceTool st, Vector3 a, Vector3 b, Vector3 c, Vector3 solidCenter)
    {
        var normal = (b - a).Cross(c - a);
        var centroid = (a + b + c) / 3f - solidCenter;
        if (normal.Dot(centroid) > 0f) (b, c) = (c, b);
        st.SetSmoothGroup(uint.MaxValue); // flat shading
        st.AddVertex(a);
        st.SetSmoothGroup(uint.MaxValue);
        st.AddVertex(b);
        st.SetSmoothGroup(uint.MaxValue);
        st.AddVertex(c);
    }

    // ------------------------------------------------------------------ island underside

    /// <summary>Faceted, tapering rock underside for floating islands. Top rim at y = 0, apex at y = -depth.</summary>
    public static ArrayMesh RockUnderside(float radius, float depth, int segments, int seed)
    {
        var rng = new RandomNumberGenerator { Seed = (ulong)Mathf.Abs(seed) + 1 };
        float[] ringRadius = { 0.97f, 0.80f, 0.50f };
        float[] ringY = { 0f, -0.34f, -0.70f };
        var rings = new Vector3[ringRadius.Length][];
        for (int r = 0; r < rings.Length; r++)
        {
            rings[r] = new Vector3[segments];
            for (int s = 0; s < segments; s++)
            {
                float ang = Mathf.Tau * s / segments;
                float jitter = r == 0 ? 1f : (0.82f + rng.Randf() * 0.34f);
                float rad = radius * ringRadius[r] * jitter;
                float y = depth * ringY[r] + (r == 0 ? 0f : (rng.Randf() - 0.5f) * depth * 0.07f);
                rings[r][s] = new Vector3(Mathf.Cos(ang) * rad, y, Mathf.Sin(ang) * rad);
            }
        }
        var apex = new Vector3((rng.Randf() - 0.5f) * radius * 0.12f, -depth, (rng.Randf() - 0.5f) * radius * 0.12f);

        var st = new SurfaceTool();
        st.Begin(Mesh.PrimitiveType.Triangles);
        for (int s = 0; s < segments; s++)
        {
            int n = (s + 1) % segments;
            for (int r = 0; r < rings.Length - 1; r++)
            {
                RockTri(st, rings[r][s], rings[r + 1][s], rings[r + 1][n]);
                RockTri(st, rings[r][s], rings[r + 1][n], rings[r][n]);
            }
            RockTri(st, rings[rings.Length - 1][s], apex, rings[rings.Length - 1][n]);
        }
        st.GenerateNormals();
        return st.Commit();
    }

    static void RockTri(SurfaceTool st, Vector3 a, Vector3 b, Vector3 c)
    {
        var normal = (b - a).Cross(c - a);
        var centroid = (a + b + c) / 3f;
        // outward reference: horizontally away from the axis, and downwards
        var outward = new Vector3(centroid.X, -0.35f * Mathf.Abs(centroid.Y) - 0.2f, centroid.Z);
        if (normal.Dot(outward) > 0f) (b, c) = (c, b);
        st.SetSmoothGroup(uint.MaxValue); st.AddVertex(a);
        st.SetSmoothGroup(uint.MaxValue); st.AddVertex(b);
        st.SetSmoothGroup(uint.MaxValue); st.AddVertex(c);
    }
}
