using System;
using System.Collections.Generic;
using Godot;

namespace Atlas;

/// <summary>Tunable parameters of the concentric island layout (WorldGenerator exposes them in the Inspector).</summary>
public sealed class IslandRingSettings
{
    /// <summary>Radius of the first ring from the hub centre. 0 = derived from the island sizes. A ring never gets less than InnerRingRadius + index * RingRadiusIncrement.</summary>
    public float InnerRingRadius = 0f;
    /// <summary>Minimum extra radius per ring index. 0 = derived from the island sizes.</summary>
    public float RingRadiusIncrement = 0f;
    /// <summary>
    /// Each ring lies at least this fraction of the larger island radius beyond the ring inside it, so the inner / outer alternation is
    /// clearly visible even when the islands would physically fit on one circle (without it, 6 islands would get radii 0.01 apart).
    /// </summary>
    public float RingSeparation = 0.5f;
    /// <summary>Clear distance kept between islands, and between a bridge and the islands it passes.</summary>
    public float MinimumIslandSpacing = 3f;
    /// <summary>Clear distance between the hub's visual edge and the first ring's islands (room for the bridges).</summary>
    public float HubIslandGap = 7f;
    /// <summary>With several rings every ring holds at least this many islands (this limits the number of rings to N / this).</summary>
    public int MinimumRingIslandCount = 3;
    /// <summary>Up to this many islands: one ring (the original layout).</summary>
    public int SingleRingMaxIslands = 5;
    /// <summary>Upper limit for the number of rings.</summary>
    public int MaxIslandRings = 6;
    /// <summary>Width of a bridge: used to keep a radial bridge clear of inner-ring islands it has to pass.</summary>
    public float BridgeWidth = 1.9f;
    /// <summary>Screen angle (degrees) of island 0. 120 = the original layout.</summary>
    public float StartAngleDeg = 120f;
}

/// <summary>Where one island goes: ring index, global index, screen angle (degrees) and its ring's radius.</summary>
public struct RingSlot
{
    public int Ring, Index;
    public float AngleDeg, Radius;
}

/// <summary>
/// Pure maths for the concentric island layout (no Godot nodes).
///
/// ANGLES are global: island i sits at  StartAngleDeg - i * (360 / N)  for ALL N islands, whatever the number of rings, so the whole
/// circle has one uniform angular spacing and no duplicate endpoint (angles decrease = clockwise on screen, like the original ring).
///
/// RINGS: up to SingleRingMaxIslands islands share one ring. With more, island i goes to ring (i mod k): with k = 2 the islands
/// alternate inner / outer / inner / outer in angular order. k is 2 unless more rings make the whole layout noticeably more
/// compact (outer extent at least 3 % smaller), because extra rings are only worth it when two rings cannot keep the clearance
/// economically. Assigning rings after the angles never disturbs an island's angular slot.
///
/// RADII: one radius per ring, the smallest that satisfies all of
///   - clearance from the hub,
///   - clearance between islands of the same ring and between rings (exact circle-circle distance, closed form),
///   - clearance between a radial bridge (hub to an outer island) and the inner-ring islands next to its line,
///   - InnerRingRadius + ring * RingRadiusIncrement, and outer ring radius &gt; inner ring radius.
/// Islands are never moved individually.
/// </summary>
public static class IslandRingLayout
{
    public static RingSlot[] Layout(IReadOnlyList<float> visualRadii, float hubVisualRadius, IslandRingSettings s, out float outerExtent)
    {
        int n = visualRadii.Count;
        outerExtent = hubVisualRadius;
        if (n == 0) return Array.Empty<RingSlot>();

        var angles = new float[n];
        float step = 360f / n;
        for (int i = 0; i < n; i++) angles[i] = s.StartAngleDeg - i * step;

        if (n <= Math.Max(1, s.SingleRingMaxIslands)) return Solve(1, visualRadii, angles, hubVisualRadius, s, out outerExtent);

        int kMax = Math.Max(2, Math.Min(Math.Max(2, s.MaxIslandRings), n / Math.Max(1, s.MinimumRingIslandCount)));
        RingSlot[] best = null;
        float bestExt = float.PositiveInfinity;
        for (int k = 2; k <= kMax; k++)
        {
            var cand = Solve(k, visualRadii, angles, hubVisualRadius, s, out float ext);
            if (best == null || ext < bestExt * 0.97f) { best = cand; bestExt = ext; }
        }
        outerExtent = bestExt;
        return best;
    }

    /// <summary>Smaller angle between two screen angles, in radians (0..pi).</summary>
    static float AngDiff(float aDeg, float bDeg) => Mathf.DegToRad(Mathf.Abs(Mathf.PosMod(aDeg - bDeg + 180f, 360f) - 180f));

    static RingSlot[] Solve(int k, IReadOnlyList<float> vis, float[] angles, float hubVis, IslandRingSettings s, out float extent)
    {
        int n = vis.Count;
        var ring = new int[n];
        var rad = new float[k];
        var maxVis = new float[k];
        for (int i = 0; i < n; i++) { ring[i] = i % k; maxVis[ring[i]] = Mathf.Max(maxVis[ring[i]], vis[i]); }
        float halfBridge = s.BridgeWidth * 0.5f;

        // lower bounds that depend only on the angles
        for (int m = 0; m < k; m++) rad[m] = hubVis + maxVis[m] + s.HubIslandGap;
        for (int i = 0; i < n; i++)
        {
            for (int j = i + 1; j < n; j++)
            {
                float d = AngDiff(angles[i], angles[j]);
                if (ring[i] == ring[j])
                {
                    // same ring: chord 2 r sin(d/2) must clear both islands
                    float need = (vis[i] + vis[j] + s.MinimumIslandSpacing) / (2f * Mathf.Sin(d * 0.5f));
                    rad[ring[i]] = Mathf.Max(rad[ring[i]], need);
                }
                else
                {
                    // the radial bridge of the OUTER island passes the INNER island at lateral distance r_in * sin(d): keep it clear
                    int q = ring[i] < ring[j] ? i : j;
                    if (Mathf.Cos(d) > 0f)
                        rad[ring[q]] = Mathf.Max(rad[ring[q]], (vis[q] + halfBridge + s.MinimumIslandSpacing) / Mathf.Sin(d));
                }
            }
        }

        // rings outward: one radius each, never smaller than the ring inside it
        extent = hubVis;
        for (int m = 0; m < k; m++)
        {
            float r = Mathf.Max(rad[m], s.InnerRingRadius + m * s.RingRadiusIncrement);
            if (m > 0) r = Mathf.Max(r, rad[m - 1] + Mathf.Max(0.01f, s.RingSeparation * Mathf.Max(maxVis[m], maxVis[m - 1])));
            for (int p = 0; p < n; p++)
            {
                if (ring[p] != m) continue;
                for (int q = 0; q < n; q++)
                {
                    if (ring[q] >= m) continue;
                    float d = AngDiff(angles[p], angles[q]);
                    float req = vis[p] + vis[q] + s.MinimumIslandSpacing;
                    float rq = rad[ring[q]];
                    float sin = Mathf.Sin(d), cos = Mathf.Cos(d);
                    if (req > rq * sin) r = Mathf.Max(r, rq * cos + Mathf.Sqrt(req * req - rq * rq * sin * sin));
                }
            }
            rad[m] = r;
        }

        var slots = new RingSlot[n];
        for (int i = 0; i < n; i++)
        {
            slots[i] = new RingSlot { Ring = ring[i], Index = i, AngleDeg = angles[i], Radius = rad[ring[i]] };
            extent = Mathf.Max(extent, rad[ring[i]] + vis[i]);
        }
        return slots;
    }
}
