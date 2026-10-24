using System;
using System.Collections.Generic;
using System.Linq;
using Godot;

namespace ClashExplorer.World;

/// <summary>
/// Draws the floating-island world behind the buildings (attach to Environment/Terrain):
///   - a glowing central hub,
///   - one stone platform "arc" per group of buildings on each ring, separated by gaps,
///   - narrow bridges linking the hub to ring 0 and every ring to the next,
///   - dark void glow, cliff undersides and shadows fading into the background.
///
/// Everything is derived from the LayoutResult of SpatialRingLayout, so platforms always sit under
/// the buildings and follow the same Y-squash. Rebuild() is called after each layout change. The
/// drawing is plain Godot 2D drawing (no extra art needed); swap HubTexture for your own hub sprite.
/// </summary>
public partial class WorldTerrain : Node2D
{
    private const double TwoPi = Math.PI * 2.0;

    [ExportGroup("Colors")]
    [Export] public Color StoneColor { get; set; } = new Color(0.17f, 0.185f, 0.225f);
    [Export] public Color CliffColor { get; set; } = new Color(0.085f, 0.095f, 0.12f);
    /// <summary>Warm outline around platforms.</summary>
    [Export] public Color EdgeGlowColor { get; set; } = new Color(1f, 0.88f, 0.62f);
    /// <summary>Cyan accent lines on platforms and bridges.</summary>
    [Export] public Color TechGlowColor { get; set; } = new Color(0.25f, 0.85f, 1f);
    [Export] public Color VoidGlowColor { get; set; } = new Color(0.1f, 0.3f, 0.4f);

    [ExportGroup("Shape")]
    [Export(PropertyHint.Range, "20,140,1")] public float PlatformDepth { get; set; } = 60f;
    /// <summary>Extra platform width around the largest building of a ring.</summary>
    [Export(PropertyHint.Range, "0,120,1")] public float BandPadding { get; set; } = 26f;
    [Export(PropertyHint.Range, "20,140,1")] public float BridgeWidth { get; set; } = 58f;

    [ExportGroup("Hub")]
    [Export] public bool ShowHub { get; set; } = true;
    /// <summary>Hub sprite. Empty = res://Assets/Buildings/drive_special_location.png.</summary>
    [Export] public Texture2D? HubTexture { get; set; }

    private sealed class Slab
    {
        public int Ring;
        public double Start;
        public double End;
        public double Radius;
        public double HalfWidth;
        public double Length => End - Start;
        public double Center => (Start + End) * 0.5;
    }

    private sealed class Bridge
    {
        public double Angle;
        public double InnerRadius;
        public double OuterRadius;
    }

    private readonly List<Slab> _slabs = new();
    private readonly List<Bridge> _bridges = new();
    private double _squash = 0.75;
    private double _hubRadius = 140.0;
    private double _outerExtent = 600.0;
    private bool _hasLayout;
    private Sprite2D? _hubSprite;
    private Tween? _fade;

    // ------------------------------------------------------------------ public API

    /// <summary>Rebuilds all platforms and bridges for a new layout. fade = soft fade-in (use on navigation).</summary>
    public void Rebuild(LayoutResult? layout, bool fade)
    {
        if (layout == null) return;

        BuildGeometry(layout);
        UpdateHubSprite();
        _hasLayout = true;
        QueueRedraw();

        _fade?.Kill();
        _fade = null;
        if (fade && IsInsideTree())
        {
            Modulate = new Color(1f, 1f, 1f, 0f);
            _fade = CreateTween();
            _fade.SetTrans(Tween.TransitionType.Cubic).SetEase(Tween.EaseType.Out);
            _fade.TweenProperty(this, "modulate:a", 1.0f, 0.5);
        }
        else
        {
            Modulate = Colors.White;
        }
    }

    public override void _ExitTree()
    {
        _fade?.Kill();
        _fade = null;
    }

    // ------------------------------------------------------------------ geometry

    private void BuildGeometry(LayoutResult layout)
    {
        _slabs.Clear();
        _bridges.Clear();
        _squash = Math.Clamp(layout.VerticalSquash, 0.4, 1.0);

        var ringSlabs = new Dictionary<int, List<Slab>>();
        foreach (IGrouping<int, PlacedItem> group in layout.Placed.GroupBy(p => p.Ring).OrderBy(g => g.Key))
        {
            int ring = group.Key;
            if (ring < 0 || ring >= layout.RingRadii.Count) continue;

            double radius = layout.RingRadii[ring];
            List<(double Angle, double Radius)> items = group
                .Select(p => (Angle: NormalizeAngle(p.AngleRadians), Radius: (double)p.Radius))
                .OrderBy(i => i.Angle)
                .ToList();

            double half = items.Max(i => i.Radius) * 0.9 + BandPadding;
            List<Slab> slabs = SegmentRing(ring, radius, half, items);
            ringSlabs[ring] = slabs;
            _slabs.AddRange(slabs);
        }

        List<int> rings = ringSlabs.Keys.OrderBy(k => k).ToList();
        if (rings.Count == 0)
        {
            _hubRadius = 150.0;
            _outerExtent = 500.0;
            return;
        }

        Slab firstSlab = ringSlabs[rings[0]][0];
        double ring0Inner = firstSlab.Radius - firstSlab.HalfWidth;
        _hubRadius = Math.Clamp(ring0Inner - 110.0, 90.0, 230.0);

        BuildBridges(ringSlabs, rings);

        Slab lastSlab = ringSlabs[rings[^1]][0];
        _outerExtent = lastSlab.Radius + lastSlab.HalfWidth + 200.0;
    }

    /// <summary>Splits a ring into platform arcs separated by gaps that never cut through a building.</summary>
    private static List<Slab> SegmentRing(int ring, double radius, double half, List<(double Angle, double Radius)> items)
    {
        var result = new List<Slab>();
        int n = items.Count;

        if (n == 1)
        {
            double pad = Math.Min(Math.PI * 0.45, (items[0].Radius + 34.0) / radius);
            result.Add(new Slab
            {
                Ring = ring, Start = items[0].Angle - pad, End = items[0].Angle + pad,
                Radius = radius, HalfWidth = half
            });
            return result;
        }

        // Gap i lies between item i and item i+1 (wrapping around).
        var gapAngle = new double[n];
        var freePixels = new double[n];
        for (int i = 0; i < n; i++)
        {
            int next = (i + 1) % n;
            double d = items[next].Angle - items[i].Angle;
            if (d <= 1e-9) d += TwoPi;
            gapAngle[i] = d;
            freePixels[i] = d * radius - items[i].Radius - items[next].Radius;
        }

        int cuts = n >= 4 ? Math.Clamp(n / 5, 2, 4) : 1;
        List<int> cutIndices = Enumerable.Range(0, n)
            .OrderByDescending(i => freePixels[i])
            .Take(cuts)
            .OrderBy(i => i)
            .ToList();

        for (int c = 0; c < cutIndices.Count; c++)
        {
            int cutA = cutIndices[c];
            int cutB = cutIndices[(c + 1) % cutIndices.Count];

            double start = items[cutA].Angle + gapAngle[cutA] * 0.5 + CutHalfAngle(freePixels[cutA], radius);
            double end = items[cutB].Angle + gapAngle[cutB] * 0.5 - CutHalfAngle(freePixels[cutB], radius);
            while (end <= start) end += TwoPi;

            result.Add(new Slab { Ring = ring, Start = start, End = end, Radius = radius, HalfWidth = half });
        }
        return result;
    }

    private static double CutHalfAngle(double freePixels, double radius)
    {
        double halfPixels = Math.Min(Math.Max(freePixels * 0.35, 8.0), 44.0);
        return halfPixels / radius;
    }

    private void BuildBridges(Dictionary<int, List<Slab>> ringSlabs, List<int> rings)
    {
        // Hub -> ring 0: up to three bridges, on the longest platforms.
        List<Slab> first = ringSlabs[rings[0]];
        double ring0Inner = first[0].Radius - first[0].HalfWidth;
        foreach (Slab slab in first.OrderByDescending(s => s.Length).Take(3))
        {
            AddBridge(slab.Center, _hubRadius - 6.0, ring0Inner + 6.0);
        }

        // Ring k -> ring k+1: up to two bridges where platforms of both rings overlap in angle.
        for (int k = 0; k + 1 < rings.Count; k++)
        {
            List<Slab> inner = ringSlabs[rings[k]];
            List<Slab> outer = ringSlabs[rings[k + 1]];
            double r1 = inner[0].Radius + inner[0].HalfWidth - 6.0;
            double r2 = outer[0].Radius - outer[0].HalfWidth + 6.0;
            double minOverlap = BridgeWidth * 1.5 / inner[0].Radius;

            var candidates = new List<(double Center, double Length)>();
            foreach (Slab a in inner)
            {
                foreach (Slab b in outer)
                {
                    if (TryOverlap(a, b, out double center, out double length) && length >= minOverlap)
                    {
                        candidates.Add((center, length));
                    }
                }
            }

            var chosen = new List<double>();
            foreach ((double center, double length) in candidates.OrderByDescending(c => c.Length))
            {
                if (chosen.Count >= 2) break;
                if (chosen.All(existing => AngularDistance(existing, center) > 1.0)) chosen.Add(center);
            }

            if (chosen.Count == 0)
            {
                // No shared angle: land the bridge on a small extra platform of the outer ring.
                Slab anchor = inner.OrderByDescending(s => s.Length).First();
                double landing = (BridgeWidth * 1.4) / outer[0].Radius;
                var pad = new Slab
                {
                    Ring = outer[0].Ring, Start = anchor.Center - landing, End = anchor.Center + landing,
                    Radius = outer[0].Radius, HalfWidth = outer[0].HalfWidth
                };
                outer.Add(pad);
                _slabs.Add(pad);
                chosen.Add(anchor.Center);
            }

            foreach (double angle in chosen) AddBridge(angle, r1, r2);
        }
    }

    private void AddBridge(double angle, double innerRadius, double outerRadius)
    {
        if (outerRadius - innerRadius < 24.0) return;
        _bridges.Add(new Bridge { Angle = angle, InnerRadius = innerRadius, OuterRadius = outerRadius });
    }

    private static bool TryOverlap(Slab a, Slab b, out double center, out double length)
    {
        double a0 = NormalizeAngle(a.Start);
        double a1 = a0 + a.Length;
        double b0 = NormalizeAngle(b.Start);
        double b1 = b0 + b.Length;

        center = 0.0;
        length = 0.0;
        foreach (double shift in new[] { -TwoPi, 0.0, TwoPi })
        {
            double lo = Math.Max(a0, b0 + shift);
            double hi = Math.Min(a1, b1 + shift);
            if (hi - lo > length)
            {
                length = hi - lo;
                center = (lo + hi) * 0.5;
            }
        }
        return length > 0.0;
    }

    private static double NormalizeAngle(double angle)
    {
        double a = angle % TwoPi;
        return a < 0.0 ? a + TwoPi : a;
    }

    private static double AngularDistance(double a, double b)
    {
        double d = Math.Abs(NormalizeAngle(a) - NormalizeAngle(b));
        return d > Math.PI ? TwoPi - d : d;
    }

    private Vector2 Point(double radius, double angle)
    {
        return new Vector2((float)(radius * Math.Cos(angle)), (float)(_squash * radius * Math.Sin(angle)));
    }

    // ------------------------------------------------------------------ hub sprite

    private void UpdateHubSprite()
    {
        if (!ShowHub)
        {
            if (_hubSprite != null) _hubSprite.Visible = false;
            return;
        }

        Texture2D? texture = HubTexture;
        if (texture == null && ResourceLoader.Exists("res://Assets/Buildings/drive_special_location.png"))
        {
            texture = GD.Load<Texture2D>("res://Assets/Buildings/drive_special_location.png");
        }
        if (texture == null) return;

        if (_hubSprite == null)
        {
            _hubSprite = new Sprite2D { Name = "HubSprite", Centered = false };
            AddChild(_hubSprite);
        }

        Vector2 size = texture.GetSize();
        float scale = Mathf.Clamp((float)_hubRadius * 1.9f / Mathf.Max(size.X, 1f), 0.3f, 1.4f);
        _hubSprite.Visible = true;
        _hubSprite.Texture = texture;
        _hubSprite.Scale = new Vector2(scale, scale);
        _hubSprite.Offset = new Vector2(-size.X * 0.5f, -size.Y);
        _hubSprite.Position = new Vector2(0f, (float)(_hubRadius * _squash * 0.35));
    }

    // ------------------------------------------------------------------ drawing

    public override void _Draw()
    {
        if (!_hasLayout) return;

        DrawVoidGlow();

        foreach (Slab slab in _slabs) DrawBody(SlabOutline(slab), PlatformDepth);
        foreach (Bridge bridge in _bridges) DrawBody(BridgeOutline(bridge), PlatformDepth * 0.28f);
        if (ShowHub) DrawBody(CircleOutline(_hubRadius), PlatformDepth * 1.3f);

        foreach (Slab slab in _slabs) DrawSlabTop(slab);
        foreach (Bridge bridge in _bridges) DrawBridgeTop(bridge);
        if (ShowHub) DrawHubTop();
    }

    private void DrawVoidGlow()
    {
        DrawSetTransform(Vector2.Zero, 0f, new Vector2(1f, (float)_squash));
        for (int i = 0; i < 7; i++)
        {
            float t = i / 6f;
            Color c = VoidGlowColor;
            c.A = 0.05f * (1f - t * 0.6f);
            DrawCircle(Vector2.Zero, (float)(_outerExtent * (1.0 - 0.13 * i)), c);
        }
        DrawSetTransform(Vector2.Zero);
    }

    /// <summary>Cliff walls and the shadow that fades into the void under a platform.</summary>
    private void DrawBody(Vector2[] outline, float depth)
    {
        for (int j = 6; j >= 1; j--)
        {
            Color shadow = new Color(0.01f, 0.03f, 0.06f, 0.2f * (1f - j / 7f));
            DrawColoredPolygon(Offset(outline, depth + j * 20f), shadow);
        }

        int layers = Mathf.Max(2, Mathf.CeilToInt(depth / 4f));
        for (int i = layers; i >= 1; i--)
        {
            float t = i / (float)layers;
            Color wall = CliffColor.Darkened(0.55f * t);
            wall = wall.Lerp(StoneColor, (1f - t) * 0.25f);
            DrawColoredPolygon(Offset(outline, i * depth / layers), wall);
        }
    }

    private void DrawSlabTop(Slab slab)
    {
        Vector2[] outline = SlabOutline(slab);
        DrawColoredPolygon(outline, StoneColor);

        // Paving lines across the slab.
        double step = 20.0 / slab.Radius;
        double rin = Math.Max(10.0, slab.Radius - slab.HalfWidth);
        double rout = slab.Radius + slab.HalfWidth;
        Color seam = new Color(0f, 0f, 0f, 0.2f);
        for (double a = slab.Start + step; a < slab.End - step * 0.5; a += step)
        {
            DrawLine(Point(rin, a), Point(rout, a), seam, 1.2f);
        }

        // Dashed tech accent along the middle of the platform.
        Color tech = TechGlowColor;
        tech.A = 0.35f;
        double dash = 16.0 / slab.Radius;
        for (double a = slab.Start + dash; a < slab.End - dash * 2.0; a += dash * 3.0)
        {
            DrawLine(Point(slab.Radius, a), Point(slab.Radius, a + dash), tech, 2.5f);
        }

        DrawGlowOutline(outline);
    }

    private void DrawBridgeTop(Bridge bridge)
    {
        Vector2[] outline = BridgeOutline(bridge);
        DrawColoredPolygon(outline, StoneColor.Lightened(0.04f));

        Vector2 a = Point(bridge.InnerRadius, bridge.Angle);
        Vector2 b = Point(bridge.OuterRadius, bridge.Angle);
        Vector2 dir = (b - a);
        float length = dir.Length();
        if (length < 1f) return;
        dir /= length;
        Vector2 normal = new Vector2(-dir.Y, dir.X) * (BridgeWidth * 0.5f);

        Color rim = TechGlowColor;
        rim.A = 0.9f;
        DrawLine(a + normal, b + normal, rim, 2.5f, true);
        DrawLine(a - normal, b - normal, rim, 2.5f, true);

        Color dashColor = TechGlowColor;
        dashColor.A = 0.75f;
        for (float d = 14f; d < length - 24f; d += 28f)
        {
            DrawLine(a + dir * d, a + dir * (d + 12f), dashColor, 3f, true);
        }
    }

    private void DrawHubTop()
    {
        Vector2[] outline = CircleOutline(_hubRadius);
        DrawColoredPolygon(outline, StoneColor.Lightened(0.05f));

        Vector2[] inner = CircleOutline(_hubRadius * 0.78);
        Color tech = TechGlowColor;
        tech.A = 0.55f;
        DrawPolyline(Close(inner), tech, 2.5f, true);
        tech.A = 0.18f;
        DrawPolyline(Close(inner), tech, 8f, true);

        DrawGlowOutline(outline);
    }

    private void DrawGlowOutline(Vector2[] outline)
    {
        Vector2[] loop = Close(outline);
        Color glow = EdgeGlowColor;
        glow.A = 0.10f;
        DrawPolyline(loop, glow, 14f, true);
        glow.A = 0.25f;
        DrawPolyline(loop, glow, 7f, true);
        glow.A = 0.95f;
        DrawPolyline(loop, glow, 2.4f, true);
    }

    // ------------------------------------------------------------------ outlines

    private Vector2[] SlabOutline(Slab slab)
    {
        double rin = Math.Max(14.0, slab.Radius - slab.HalfWidth);
        double rout = slab.Radius + slab.HalfWidth;
        int steps = Math.Max(3, (int)Math.Ceiling(slab.Length * rout / 22.0));

        var points = new List<Vector2>(steps * 2 + 2);
        for (int i = 0; i <= steps; i++)
        {
            points.Add(Point(rout, slab.Start + slab.Length * i / steps));
        }
        for (int i = steps; i >= 0; i--)
        {
            points.Add(Point(rin, slab.Start + slab.Length * i / steps));
        }
        return points.ToArray();
    }

    private Vector2[] BridgeOutline(Bridge bridge)
    {
        Vector2 a = Point(bridge.InnerRadius, bridge.Angle);
        Vector2 b = Point(bridge.OuterRadius, bridge.Angle);
        Vector2 dir = (b - a).Normalized();
        Vector2 normal = new Vector2(-dir.Y, dir.X) * (BridgeWidth * 0.5f);
        return new[] { a + normal, b + normal, b - normal, a - normal };
    }

    private Vector2[] CircleOutline(double radius)
    {
        const int Steps = 72;
        var points = new Vector2[Steps];
        for (int i = 0; i < Steps; i++)
        {
            points[i] = Point(radius, TwoPi * i / Steps);
        }
        return points;
    }

    private static Vector2[] Offset(Vector2[] points, float dy)
    {
        var shifted = new Vector2[points.Length];
        for (int i = 0; i < points.Length; i++) shifted[i] = new Vector2(points[i].X, points[i].Y + dy);
        return shifted;
    }

    private static Vector2[] Close(Vector2[] points)
    {
        var loop = new Vector2[points.Length + 1];
        Array.Copy(points, loop, points.Length);
        loop[points.Length] = points[0];
        return loop;
    }
}
