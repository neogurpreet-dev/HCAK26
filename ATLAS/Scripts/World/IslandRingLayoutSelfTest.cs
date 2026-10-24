using System;
using System.Collections.Generic;
using System.Linq;
using System.Text;
using Godot;

namespace Atlas;

/// <summary>
/// Self-test for the concentric island layout (<see cref="IslandRingLayout"/>): equal angular spacing on one global sequence, ring
/// assignment, one radius per ring, outer ring clearly farther than the inner, progressive ring gaps, no overlaps, bridges lying exactly
/// along each island's angle, and the ascending stable sort. Pure maths (Godot's Vector3 / Mathf structs only, no nodes).
/// Runs together with the other self-tests:   Godot --path D:\HCAK26\ATLAS -- selftest-quit
/// </summary>
public static class IslandRingLayoutSelfTest
{
    public static string Run(out bool allOk)
    {
        var sb = new StringBuilder();
        int pass = 0, fail = 0;
        void Check(string name, bool ok, string detail = "")
        {
            if (ok) pass++; else fail++;
            if (!ok || detail.Length > 0) sb.Append(ok ? "PASS " : "FAIL ").Append(name).Append("   ").AppendLine(detail);
        }

        sb.AppendLine("== Island ring layout ==");
        var s = new IslandRingSettings();
        const float hubVis = 13f;
        int[] sizes = { 1, 2, 3, 4, 5, 6, 7, 8, 9, 10, 12, 13, 16, 20, 24, 30 };
        var summary = new StringBuilder();

        foreach (int n in sizes)
        {
            var vis = Enumerable.Range(0, n).Select(i => 8f + (i * 7 % 5)).ToList();   // 8..12, deterministic mix of island sizes
            var slots = IslandRingLayout.Layout(vis, hubVis, s, out float extent);
            string tag = "N=" + n + ": ";
            int rings = slots.Length == 0 ? 0 : slots.Max(x => x.Ring) + 1;
            summary.Append(n).Append("->").Append(rings).Append(rings == 1 ? " ring  " : " rings ");

            Check(tag + "one slot per island", slots.Length == n && slots.Select((x, i) => x.Index == i).All(b => b));

            float step = 360f / n;
            bool equal = true;
            for (int i = 0; i < n; i++)
            {
                float d = Mathf.PosMod(slots[i].AngleDeg - slots[(i + 1) % n].AngleDeg, 360f);   // angles decrease = clockwise
                if (n > 1 && Mathf.Abs(d - step) > 1e-3f) equal = false;
            }
            Check(tag + "equal angular spacing of " + step.ToString("0.###") + " deg on one global sequence", equal);

            if (n <= s.SingleRingMaxIslands) Check(tag + "single ring", rings == 1);
            else
            {
                bool alt = rings >= 2 && slots.Select((x, i) => x.Ring == i % rings).All(b => b);
                Check(tag + "rings alternate in angular order (inner, outer, inner, outer...)", alt, rings + " rings");
                if (rings == 2) Check(tag + "two rings: even slots inner, odd slots outer", slots.Select((x, i) => x.Ring == (i % 2)).All(b => b));
            }

            bool sameRadius = slots.GroupBy(x => x.Ring).All(g => g.Max(x => x.Radius) - g.Min(x => x.Radius) < 1e-4f);
            Check(tag + "one radius per ring", sameRadius);

            var ringR = new float[rings];
            var ringVis = new float[rings];
            foreach (var x in slots) { ringR[x.Ring] = x.Radius; ringVis[x.Ring] = Mathf.Max(ringVis[x.Ring], vis[x.Index]); }
            if (rings >= 2)
            {
                bool far = ringR[1] >= ringR[0] * s.MinOuterToInnerRatio - 1e-3f;
                bool gaps = true;
                for (int m = 1; m < rings; m++)
                {
                    float baseGap = Mathf.Max(0.01f, s.RingSeparation * Mathf.Max(ringVis[m], ringVis[m - 1]));
                    if (ringR[m] - ringR[m - 1] < baseGap * (1f + s.RingGapGrowth * (m - 1)) - 1e-3f) gaps = false;
                }
                Check(tag + "outer radius >= " + s.MinOuterToInnerRatio + " x inner radius", far, ringR[1].ToString("0.0") + " vs " + ringR[0].ToString("0.0"));
                Check(tag + "ring gaps at least the base gap, widening outward", gaps);
            }

            bool clear = true;
            var pos = slots.Select(x => IslandRingLayout.ScreenDir(Mathf.DegToRad(x.AngleDeg)) * x.Radius).ToList();
            for (int i = 0; i < n; i++)
                for (int j = i + 1; j < n; j++)
                    if ((pos[i] - pos[j]).Length() < vis[i] + vis[j] + s.MinimumIslandSpacing - 0.01f) clear = false;
            for (int i = 0; i < n; i++) if (slots[i].Radius < hubVis + vis[i] + s.HubIslandGap - 0.01f) clear = false;
            Check(tag + "no overlaps, hub clearance kept", clear);

            bool aligned = true;
            for (int i = 0; i < n; i++)
            {
                var dir = IslandRingLayout.ScreenDir(Mathf.DegToRad(slots[i].AngleDeg));
                IslandRingLayout.BridgeEnds(Vector3.Zero, hubVis, dir, pos[i], vis[i], 1f, out var a, out var b);
                var along = (b - a).Normalized();
                bool onLine = a.Cross(dir).Length() < 1e-3f && b.Cross(dir).Length() < 1e-3f && along.Dot(dir) > 0.9999f;
                bool atSlot = (pos[i] - dir * slots[i].Radius).Length() < 1e-3f;
                if (!onLine || !atSlot) aligned = false;
            }
            Check(tag + "bridges lie exactly along each island's angle", aligned);
        }
        sb.AppendLine("rings per island count: " + summary);

        var items = new[] { ("A", 5), ("B", 1), ("C", 3), ("D", 3), ("E", 2), ("F", 3) };
        var sorted = IslandRingLayout.OrderForLayout(items, x => x.Item2).Select(x => x.Item1).ToList();
        Check("sorting: ascending building count, ties keep their order", string.Join("", sorted) == "BECDFA", string.Join("", sorted));
        var big = Enumerable.Range(0, 11).Select(i => (i, (i * 5) % 7 + 1)).ToList();
        var order = IslandRingLayout.OrderForLayout(big, x => x.Item2);
        bool innerSmaller = true;
        for (int i = 0; i + 1 < order.Count; i += 2) if (order[i].Item2 > order[i + 1].Item2) innerSmaller = false;
        Check("sorting: of every neighbouring (inner, outer) pair the inner island has the fewer buildings", innerSmaller);

        var a7 = IslandRingLayout.Layout(Enumerable.Repeat(10f, 7).ToList(), hubVis, s, out _);
        var a8 = IslandRingLayout.Layout(Enumerable.Repeat(10f, 8).ToList(), hubVis, s, out _);
        var a7again = IslandRingLayout.Layout(Enumerable.Repeat(10f, 7).ToList(), hubVis, s, out _);
        Check("dynamic: 7 -> 8 -> 7 islands recomputes the step deterministically",
            Mathf.Abs(Mathf.PosMod(a8[0].AngleDeg - a8[1].AngleDeg, 360f) - 45f) < 1e-3f
            && a7.Zip(a7again, (p, q) => Mathf.Abs(p.AngleDeg - q.AngleDeg) < 1e-6f && Mathf.Abs(p.Radius - q.Radius) < 1e-6f).All(b => b));

        sb.AppendLine().Append("Ring layout: ").Append(pass).Append(" passed, ").Append(fail).AppendLine(" failed");
        allOk = fail == 0;
        return sb.ToString();
    }
}
