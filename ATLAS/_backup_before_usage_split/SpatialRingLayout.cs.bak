using System;
using System.Collections.Generic;
using Godot;

namespace Atlas;

public struct RingLayoutResult
{
    /// <summary>Positions on the platform plane (x = world X, y = world Z), relative to the platform centre.</summary>
    public Vector2[] Positions;
    /// <summary>Distance from the centre to the outer edge of the outermost building.</summary>
    public float OuterRadius;
}

/// <summary>
/// Footprint-aware concentric ring placement.
/// Items must be passed in priority order (most relevant first): the first item takes the centre,
/// following items fill rings outward. Each item is a circle of radius <c>footprint</c>.
///
/// Ring capacity: an item with footprint f on a ring of radius R needs an angular width of
/// 2*asin((f + spacing/2) / R). Items are accumulated until 2*pi is used; leftover angle is
/// distributed evenly. Because R*sin(wa/2) + R*sin(wb/2) never exceeds the chord between two
/// neighbours, adjacent circles can never overlap. Ring radius is grown until the largest item on
/// the ring clears the previous ring by <c>minSpacing</c>.
/// </summary>
public static class SpatialRingLayout
{
    public static RingLayoutResult Compute(IReadOnlyList<float> footprints, float minSpacing = 0.7f, bool centerFirst = true, float centerClearance = 0f)
    {
        int n = footprints.Count;
        var res = new RingLayoutResult { Positions = new Vector2[n], OuterRadius = 0f };
        if (n == 0) return res;

        minSpacing = Mathf.Max(0.05f, minSpacing);
        int i = 0;
        float prevOuter = centerClearance;
        if (centerFirst)
        {
            res.Positions[0] = Vector2.Zero;
            prevOuter = Mathf.Max(centerClearance, Sanitize(footprints[0]));
            i = 1;
        }

        int ring = 0;
        while (i < n)
        {
            float R = prevOuter + minSpacing + Sanitize(footprints[i]);
            int count = 0;
            float maxFp = 0f;

            // Grow R until the largest chosen footprint clears the previous ring.
            for (int iter = 0; iter < 12; iter++)
            {
                count = 0; maxFp = 0f;
                float used = 0f;
                for (int j = i; j < n; j++)
                {
                    float fp = Sanitize(footprints[j]);
                    float w = AngularWidth(fp, minSpacing, R);
                    if (count > 0 && used + w > Mathf.Tau) break;
                    used += w;
                    count++;
                    if (fp > maxFp) maxFp = fp;
                }
                float needed = prevOuter + minSpacing + maxFp;
                if (R >= needed - 1e-4f) break;
                R = needed;
            }

            // Place the chosen items.
            float total = 0f;
            for (int k = 0; k < count; k++) total += AngularWidth(Sanitize(footprints[i + k]), minSpacing, R);
            float slack = count > 0 ? Mathf.Max(0f, (Mathf.Tau - total) / count) : 0f;
            float angle = ring * 0.7f; // stagger rings so items do not line up radially
            for (int k = 0; k < count; k++)
            {
                float w = AngularWidth(Sanitize(footprints[i + k]), minSpacing, R);
                angle += w * 0.5f;
                res.Positions[i + k] = new Vector2(Mathf.Cos(angle), Mathf.Sin(angle)) * R;
                angle += w * 0.5f + slack;
            }

            prevOuter = R + maxFp;
            i += count;
            ring++;
        }

        res.OuterRadius = prevOuter;
        return res;
    }

    static float AngularWidth(float footprint, float spacing, float radius)
    {
        float ratio = (footprint + spacing * 0.5f) / Mathf.Max(radius, 0.001f);
        return 2f * Mathf.Asin(Mathf.Clamp(ratio, 0.0001f, 1f));
    }

    static float Sanitize(float f) => float.IsFinite(f) && f > 0.05f ? f : 0.05f;
}
