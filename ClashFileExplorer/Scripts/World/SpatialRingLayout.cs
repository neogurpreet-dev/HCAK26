using System;
using System.Collections.Generic;
using System.Linq;
using Godot;

namespace ClashExplorer.World;

/// <summary>
/// Concentric, footprint-aware polar placement around the world origin (0, 0).
///
/// How it works:
///  1. Items are sorted by RingPreference (0 = inner), so frequently used items land on inner rings.
///  2. Each ring is filled greedily: an item joins the ring only if it can be placed without
///     touching any item already on it (distance between centers >= radiusA + radiusB + MinSpacing,
///     checked against every pair, so a small building between two large ones cannot hide an overlap).
///  3. The ring radius is then raised so it clears the previous ring by the largest building on
///     each (this is where "the largest building on a ring" is accounted for).
///  4. Leftover angle is spread proportionally between neighbours for a balanced ring.
///
/// Placement never overlaps: every accepted pair is verified numerically. The class is pure math,
/// holds no scene references and may be called from a worker thread.
/// </summary>
[GlobalClass]
public partial class SpatialRingLayout : Resource
{
    private const double TwoPi = Math.PI * 2.0;
    private const double GoldenAngle = 2.399963229728653;
    private const int MaxRings = 400;

    [Export(PropertyHint.Range, "50,3000,1")] public float InnerRadius { get; set; } = 320f;
    /// <summary>Minimum free space between two building footprints.</summary>
    [Export(PropertyHint.Range, "0,200,1")] public float MinSpacing { get; set; } = 28f;
    /// <summary>Extra empty space between consecutive rings.</summary>
    [Export(PropertyHint.Range, "0,300,1")] public float RingGap { get; set; } = 90f;
    /// <summary>1 = circular rings. Lower values flatten them into isometric-looking ellipses.</summary>
    [Export(PropertyHint.Range, "0.4,1,0.01")] public float VerticalSquash { get; set; } = 0.75f;
    /// <summary>Items that would need a larger ring radius than this are reported as unplaced.</summary>
    [Export(PropertyHint.Range, "500,50000,10")] public float MaxWorldRadius { get; set; } = 9000f;
    [Export(PropertyHint.Range, "-180,180,1")] public float StartAngleDegrees { get; set; } = -90f;
    /// <summary>Rotates each ring by the golden angle so buildings on neighbouring rings do not line up.</summary>
    [Export] public bool StaggerRings { get; set; } = true;
    /// <summary>Fraction of the sprite width used as footprint diameter. See EstimateFootprintRadius.</summary>
    [Export(PropertyHint.Range, "0.3,1.5,0.01")] public float FootprintWidthFactor { get; set; } = 0.9f;

    /// <summary>Footprint radius for a sprite of the given texture size drawn at the given scale.</summary>
    public float EstimateFootprintRadius(Vector2 textureSize, float scale)
    {
        float s = float.IsFinite(scale) && scale > 0f ? scale : 1f;
        float w = textureSize.X > 1f ? textureSize.X : 128f;
        float r = 0.5f * w * s * FootprintWidthFactor;
        return Mathf.Clamp(r, 12f, 600f);
    }

    public LayoutResult Compute(IReadOnlyList<LayoutItem> items)
    {
        var result = new LayoutResult();
        double s = Math.Clamp(VerticalSquash, 0.4, 1.0);
        result.VerticalSquash = (float)s;
        double spacing = Math.Max(0.0, MinSpacing);
        double gap = Math.Max(0.0, RingGap);
        double startAngle = Mathf.DegToRad(StartAngleDegrees);
        int n = items.Count;

        if (n == 0)
        {
            result.OuterRadius = InnerRadius;
            result.OverflowPosition = new Vector2(0f, (float)(-s * InnerRadius));
            result.Bounds = new Rect2(-InnerRadius, -InnerRadius, InnerRadius * 2f, InnerRadius * 2f);
            return result;
        }

        // Sanitized per-item values.
        var radius = new double[n];
        var preference = new double[n];
        for (int i = 0; i < n; i++)
        {
            double r = items[i].Radius;
            radius[i] = double.IsFinite(r) ? Math.Clamp(r, 8.0, 800.0) : 60.0;
            double p = items[i].RingPreference;
            preference[i] = double.IsFinite(p) ? p : 0.5;
        }

        int[] order = Enumerable.Range(0, n).OrderBy(i => preference[i]).ThenBy(i => i).ToArray();

        int cursor = 0;
        int ring = 0;
        double prevRadius = 0.0;
        double prevMax = 0.0;
        bool outOfBounds = false;

        while (cursor < n && ring < MaxRings)
        {
            double firstRadius = radius[order[cursor]];
            double a0 = startAngle + (StaggerRings ? ring * GoldenAngle : 0.0);
            double guess = ring == 0
                ? Math.Max(InnerRadius, firstRadius * 1.2 + spacing)
                : prevRadius + (prevMax + firstRadius + spacing + gap) / s;

            // 1. Greedy membership at the guessed radius.
            var members = new List<int> { order[cursor] };
            var memberRadii = new List<double> { firstRadius };
            while (cursor + members.Count < n)
            {
                int candidate = order[cursor + members.Count];
                memberRadii.Add(radius[candidate]);
                double[]? trial = AssignAngles(memberRadii, guess, s, spacing, a0);
                if (trial == null)
                {
                    memberRadii.RemoveAt(memberRadii.Count - 1);
                    break;
                }
                members.Add(candidate);
            }

            // 2. Final ring radius: clear the previous ring by the largest building on each.
            double[]? angles = null;
            double finalRadius = guess;
            while (true)
            {
                double maxHere = memberRadii.Max();
                finalRadius = ring == 0
                    ? Math.Max(InnerRadius, maxHere * 1.2 + spacing)
                    : Math.Max(guess, prevRadius + (prevMax + maxHere + spacing + gap) / s);
                angles = AssignAngles(memberRadii, finalRadius, s, spacing, a0);
                if (angles != null || members.Count == 1) break;
                members.RemoveAt(members.Count - 1);
                memberRadii.RemoveAt(memberRadii.Count - 1);
            }

            if (angles == null) angles = new[] { a0 }; // a single item always fits

            if (finalRadius > MaxWorldRadius)
            {
                outOfBounds = true;
                break;
            }

            // 3. Optional clustering inside the ring (kept only if it still fits).
            if (members.Count > 2 && members.Select(m => items[m].ClusterGroup).Distinct().Count() > 1)
            {
                List<int> clustered = members
                    .Select((m, idx) => (Member: m, Idx: idx))
                    .OrderBy(t => items[t.Member].ClusterGroup)
                    .ThenBy(t => t.Idx)
                    .Select(t => t.Member)
                    .ToList();
                List<double> clusteredRadii = clustered.Select(m => radius[m]).ToList();
                double[]? clusteredAngles = AssignAngles(clusteredRadii, finalRadius, s, spacing, a0);
                if (clusteredAngles != null)
                {
                    members = clustered;
                    memberRadii = clusteredRadii;
                    angles = clusteredAngles;
                }
            }

            // 4. Spread leftover angle for a balanced ring.
            StretchToFullCircle(angles, memberRadii, finalRadius, s, spacing);

            for (int k = 0; k < members.Count; k++)
            {
                int index = members[k];
                double a = angles[k];
                result.Placed.Add(new PlacedItem
                {
                    Index = index,
                    Key = items[index].Key,
                    Position = new Vector2(
                        (float)(finalRadius * Math.Cos(a)),
                        (float)(s * finalRadius * Math.Sin(a))),
                    Ring = ring,
                    AngleRadians = (float)a,
                    Radius = (float)radius[index]
                });
            }

            result.RingRadii.Add((float)finalRadius);
            prevRadius = finalRadius;
            prevMax = memberRadii.Max();
            cursor += members.Count;
            ring++;
        }

        for (int k = cursor; k < n; k++) result.Unplaced.Add(order[k]);
        if (outOfBounds || ring >= MaxRings)
        {
            GD.PushWarning($"[SpatialRingLayout] {result.Unplaced.Count} item(s) did not fit inside MaxWorldRadius.");
        }

        result.RingCount = ring;
        result.OuterRadius = (float)(prevRadius + prevMax);

        // Overflow structure sits straight "north", one ring beyond the outermost ring.
        double overflowRadius = prevRadius + (prevMax + 90.0 + spacing + gap) / s;
        result.OverflowPosition = new Vector2(0f, (float)(-s * overflowRadius));

        result.Bounds = ComputeBounds(result);
        return result;
    }

    // ------------------------------------------------------------------ geometry

    private static double Distance(double ringRadius, double s, double a, double b)
    {
        double dx = ringRadius * (Math.Cos(a) - Math.Cos(b));
        double dy = s * ringRadius * (Math.Sin(a) - Math.Sin(b));
        return Math.Sqrt(dx * dx + dy * dy);
    }

    /// <summary>
    /// Packs items around a ring in order, as tightly as the footprints allow, starting at a0.
    /// Returns null if they cannot all fit in one revolution without touching.
    /// </summary>
    private static double[]? AssignAngles(IReadOnlyList<double> radii, double ringRadius, double s,
        double spacing, double a0)
    {
        int m = radii.Count;
        var angles = new double[m];
        angles[0] = a0;

        for (int c = 1; c < m; c++)
        {
            double a = angles[c - 1];

            for (int j = c - 1; j >= 0; j--)
            {
                double need = radii[j] + radii[c] + spacing;
                if (Distance(ringRadius, s, a, angles[j]) >= need) continue;

                double lo = a;
                double hi = angles[j] + Math.PI;
                if (hi <= lo) return null;
                if (Distance(ringRadius, s, hi, angles[j]) < need) return null;

                for (int it = 0; it < 40; it++)
                {
                    double mid = (lo + hi) * 0.5;
                    if (Distance(ringRadius, s, mid, angles[j]) >= need) hi = mid;
                    else lo = mid;
                }
                a = hi;
            }

            if (a - a0 >= TwoPi) return null;

            // Verify against every earlier item, including the ones across the wrap-around.
            for (int j = 0; j < c; j++)
            {
                double need = radii[j] + radii[c] + spacing;
                if (Distance(ringRadius, s, a, angles[j]) < need - 1e-6) return null;
            }
            angles[c] = a;
        }
        return angles;
    }

    private static bool VerifyAll(double[] angles, IReadOnlyList<double> radii, double ringRadius, double s, double spacing)
    {
        for (int i = 0; i < angles.Length; i++)
        {
            for (int j = i + 1; j < angles.Length; j++)
            {
                double need = radii[i] + radii[j] + spacing;
                if (Distance(ringRadius, s, angles[i], angles[j]) < need - 1e-6) return false;
            }
        }
        return true;
    }

    /// <summary>
    /// Scales the gaps between neighbours uniformly so the ring spans a full circle. The result is
    /// verified; if stretching would cause any contact the tight packing is kept.
    /// </summary>
    private static void StretchToFullCircle(double[] angles, IReadOnlyList<double> radii, double ringRadius,
        double s, double spacing)
    {
        int m = angles.Length;
        if (m < 2) return;

        double a0 = angles[0];
        double last = angles[m - 1];
        double span = last - a0;

        // Smallest angular gap between the last and the first item (wrapping around).
        double needClosure = radii[m - 1] + radii[0] + spacing;
        double lo = 0.0;
        double hi = Math.PI;
        if (Distance(ringRadius, s, last, last + hi) < needClosure)
        {
            lo = hi;
        }
        else
        {
            for (int it = 0; it < 40; it++)
            {
                double mid = (lo + hi) * 0.5;
                if (Distance(ringRadius, s, last, last + mid) >= needClosure) hi = mid;
                else lo = mid;
            }
            lo = hi;
        }

        double total = span + lo;
        if (total <= 1e-6) return;
        double lambda = TwoPi / total;
        if (lambda <= 1.0001) return;

        double[] original = (double[])angles.Clone();
        for (int attempt = 0; attempt < 6; attempt++)
        {
            for (int i = 0; i < m; i++) angles[i] = a0 + lambda * (original[i] - a0);
            if (angles[m - 1] - a0 < TwoPi && VerifyAll(angles, radii, ringRadius, s, spacing)) return;
            lambda = 1.0 + (lambda - 1.0) * 0.5;
        }
        Array.Copy(original, angles, m);
    }

    private static Rect2 ComputeBounds(LayoutResult result)
    {
        if (result.Placed.Count == 0) return new Rect2(-100f, -100f, 200f, 200f);

        float minX = float.MaxValue, minY = float.MaxValue, maxX = float.MinValue, maxY = float.MinValue;
        foreach (PlacedItem p in result.Placed)
        {
            minX = Mathf.Min(minX, p.Position.X - p.Radius);
            minY = Mathf.Min(minY, p.Position.Y - p.Radius);
            maxX = Mathf.Max(maxX, p.Position.X + p.Radius);
            maxY = Mathf.Max(maxY, p.Position.Y + p.Radius);
        }
        minX = Mathf.Min(minX, result.OverflowPosition.X - 90f);
        minY = Mathf.Min(minY, result.OverflowPosition.Y - 90f);
        maxX = Mathf.Max(maxX, result.OverflowPosition.X + 90f);
        maxY = Mathf.Max(maxY, result.OverflowPosition.Y + 90f);
        return new Rect2(minX, minY, maxX - minX, maxY - minY);
    }
}
