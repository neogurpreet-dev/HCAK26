using System;
using System.Collections.Generic;
using System.Linq;
using System.Text;

namespace Atlas;

/// <summary>
/// Self-test for the "Others" island rule (<see cref="IslandGrouping"/>). Pure .NET, no Godot types.
/// Runs together with the other self-tests:   Godot --path D:\HCAK26\ATLAS -- selftest-quit
/// </summary>
public static class IslandGroupingSelfTest
{
    sealed class Item
    {
        public string Name, Category, Path;
        public override string ToString() => Name;
    }

    static int _n;
    static List<Item> Make(string category, int count)
    {
        var l = new List<Item>();
        for (int i = 0; i < count; i++) { _n++; l.Add(new Item { Name = category + "_" + i, Category = category, Path = @"C:\t\" + category + @"\" + i + "_" + _n }); }
        return l;
    }

    static List<KeyValuePair<string, List<Item>>> View(params (string, int)[] cats)
        => cats.Select(c => new KeyValuePair<string, List<Item>>(c.Item1, Make(c.Item1, c.Item2))).ToList();

    static string Describe(List<IslandBucket<Item>> r) => string.Join(", ", r.Select(b => b.Name + "=" + b.Items.Count));

    public static string Run(out bool allOk)
    {
        var sb = new StringBuilder();
        int pass = 0, fail = 0;
        void Check(string name, bool ok, string detail = "")
        {
            if (ok) pass++; else fail++;
            sb.Append(ok ? "PASS " : "FAIL ").Append(name);
            if (detail.Length > 0) sb.Append("   ").Append(detail);
            sb.AppendLine();
        }
        bool Conserved(List<KeyValuePair<string, List<Item>>> input, List<IslandBucket<Item>> r)
        {
            var src = input.SelectMany(c => c.Value).ToList();
            var dst = r.SelectMany(b => b.Items).ToList();
            return src.Count == dst.Count && dst.Distinct().Count() == dst.Count && src.All(x => dst.Contains(x));
        }

        sb.AppendLine("== Others island grouping (threshold " + IslandGrouping.DefaultSmallCategoryThreshold + ") ==");

        // 1 / 2 / 3 : the boundary
        var v1 = View(("Documents", 5), ("Solo", 1));
        var r1 = IslandGrouping.Group(v1);
        Check("1. a category with 1 building is grouped into Others",
            r1.Any(b => b.IsOthers && b.Sources.Contains("Solo") && b.Items.Count == 1) && !r1.Any(b => b.Name == "Solo"), Describe(r1));
        var v2 = View(("Documents", 5), ("Pair", 2));
        var r2 = IslandGrouping.Group(v2);
        Check("2. a category with 2 buildings is grouped into Others",
            r2.Any(b => b.IsOthers && b.Sources.Contains("Pair") && b.Items.Count == 2) && !r2.Any(b => b.Name == "Pair"), Describe(r2));
        var v3 = View(("Documents", 5), ("Trio", 3));
        var r3 = IslandGrouping.Group(v3);
        Check("3. a category with 3 buildings receives its own island",
            r3.Any(b => b.Name == "Trio" && !b.IsOthers && b.Items.Count == 3) && !r3.Any(b => b.IsOthers), Describe(r3));

        // 4 : the example from the brief
        var v4 = View(("Documents", 8), ("Images", 5), ("Videos", 2), ("Audio", 1), ("Archives", 3), ("Shortcuts", 1));
        var r4 = IslandGrouping.Group(v4);
        var others4 = r4.Where(b => b.IsOthers).ToList();
        Check("4. multiple small categories are combined into exactly one Others island",
            others4.Count == 1 && others4[0].Items.Count == 4 && others4[0].Sources.SequenceEqual(new[] { "Videos", "Audio", "Shortcuts" }), Describe(r4));
        Check("4b. brief example gives Documents 8, Images 5, Archives 3, Others 4 (Others last)",
            Describe(r4) == "Documents=8, Images=5, Archives=3, Others=4", Describe(r4));

        // 5 : everything small
        var v5 = View(("A", 2), ("B", 1), ("C", 2));
        var r5 = IslandGrouping.Group(v5);
        Check("5. if all categories are small, only one Others island is created holding everything",
            r5.Count == 1 && r5[0].IsOthers && r5[0].Items.Count == 5, Describe(r5));

        // 6 : everything big
        var v6 = View(("A", 3), ("B", 9), ("C", 4));
        var r6 = IslandGrouping.Group(v6);
        Check("6. if every category has at least 3 buildings, no Others island is created",
            r6.Count == 3 && !r6.Any(b => b.IsOthers), Describe(r6));

        // 7 : changing the view filter recalculates (same files, different categories)
        var files = Make("f", 12);
        List<KeyValuePair<string, List<Item>>> By(Func<Item, string> key)
            => files.GroupBy(key).Select(g => new KeyValuePair<string, List<Item>>(g.Key, g.ToList())).ToList();
        string[] ext = { "a", "a", "a", "a", "b", "b", "b", "c", "c", "d", "e", "f" };
        for (int i = 0; i < files.Count; i++) files[i].Category = ext[i];
        var byType = IslandGrouping.Group(By(f => f.Category));                           // a=4, b=3, c=2, d=1, e=1, f=1
        var bySize = IslandGrouping.Group(By(f => files.IndexOf(f) < 6 ? "Small" : "Large"));   // 6 / 6
        Check("7. changing the active view filter recalculates the grouping",
            Describe(byType) == "a=4, b=3, Others=5" && Describe(bySize) == "Small=6, Large=6" && Conserved(By(f => f.Category), byType),
            Describe(byType) + "  |  " + Describe(bySize));

        // 8 : adding / removing files
        var live = View(("Docs", 4), ("Code", 2));
        var before = IslandGrouping.Group(live);                      // Code folds into Others
        live[1].Value.AddRange(Make("Code", 1));                      // Code gains a file -> 3 -> own island
        var afterAdd = IslandGrouping.Group(live);
        live[0].Value.RemoveRange(0, 2);                              // Docs loses two -> 2 -> Others
        var afterRemove = IslandGrouping.Group(live);
        live[0].Value.Clear();                                        // Docs empty -> disappears, no empty island
        var afterEmpty = IslandGrouping.Group(live);
        Check("8. adding or removing files updates counts and island assignments",
            Describe(before) == "Docs=4, Others=2" && Describe(afterAdd) == "Docs=4, Code=3"
            && Describe(afterRemove) == "Code=3, Others=2" && Describe(afterEmpty) == "Code=3",
            Describe(before) + " | " + Describe(afterAdd) + " | " + Describe(afterRemove) + " | " + Describe(afterEmpty));

        // 9 : identity preserved
        var v9 = View(("Big", 4), ("Tiny1", 1), ("Tiny2", 2));
        var snapshot = v9.SelectMany(c => c.Value).Select(x => (x, x.Name, x.Category, x.Path)).ToList();
        var r9 = IslandGrouping.Group(v9);
        var moved = r9.First(b => b.IsOthers).Items;
        Check("9. Others keeps every file's identity, original category and path (same objects, nothing renamed)",
            Conserved(v9, r9) && snapshot.All(s => s.x.Name == s.Name && s.x.Category == s.Category && s.x.Path == s.Path)
            && moved.All(x => x.Category.StartsWith("Tiny")), "Others holds " + string.Join(", ", moved));

        // robustness
        Check("R1. empty / null input gives no islands and no empty Others",
            IslandGrouping.Group(new List<KeyValuePair<string, List<Item>>>()).Count == 0
            && IslandGrouping.Group<Item>(null).Count == 0
            && IslandGrouping.Group(View(("Ghost", 0))).Count == 0);
        var cap = View(("A", 9), ("B", 8), ("C", 7), ("D", 6), ("E", 5));
        var rc = IslandGrouping.Group(cap, 2, 3);
        Check("R2. an island cap folds the smallest named categories into the one Others island",
            Describe(rc) == "A=9, B=8, Others=18" && rc.Count(b => b.IsOthers) == 1 && Conserved(cap, rc), Describe(rc));
        var rt = IslandGrouping.Group(View(("A", 5), ("B", 4)), 4);
        Check("R3. the threshold is configurable (4 -> a 4-item category folds into Others)", Describe(rt) == "A=5, Others=4", Describe(rt));
        var rr = Enumerable.Range(0, 5).Select(_ => IslandGrouping.Group(View(("A", 6), ("B", 2), ("C", 1)))).Select(Describe).Distinct().Count();
        Check("R4. repeated regrouping is deterministic and never creates a second Others island", rr == 1);

        sb.AppendLine().Append("Others grouping: ").Append(pass).Append(" passed, ").Append(fail).AppendLine(" failed");
        allOk = fail == 0;
        return sb.ToString();
    }
}
