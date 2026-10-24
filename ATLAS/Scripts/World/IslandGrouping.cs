using System;
using System.Collections.Generic;
using System.Linq;

namespace Atlas;

/// <summary>One island's worth of items after small categories have been folded together.</summary>
public sealed class IslandBucket<T>
{
    /// <summary>Island name: the category name, or <see cref="IslandGrouping.OthersName"/>.</summary>
    public string Name = "";
    public bool IsOthers;
    public List<T> Items = new();
    /// <summary>The original categories whose items live on this island (more than one only for Others).</summary>
    public List<string> Sources = new();
}

/// <summary>
/// The "Others" rule for satellite islands, independent of Godot so it can be self-tested:
///  * a category with MORE than <c>threshold</c> items (default 2, so 3 or more) gets its own island
///  * every category with <c>threshold</c> items or fewer is folded into ONE island called "Others"
///  * if too many islands would result (<c>maxIslands</c>), the smallest named categories are folded into Others as well
///  * nothing is dropped, duplicated or modified: the items are the very same objects, only their island changes
/// It is applied to whatever categories the active view filter produced, so it works for every grouping mode and for any category name.
/// </summary>
public static class IslandGrouping
{
    /// <summary>Categories with this many items or fewer are folded into Others.</summary>
    public const int DefaultSmallCategoryThreshold = 2;
    public const string OthersName = "Others";

    /// <param name="categories">Categories in display order (key = category name, value = its items). Empty categories are ignored.</param>
    /// <param name="threshold">Categories with this many items or fewer go to Others.</param>
    /// <param name="maxIslands">Upper limit for the number of islands including Others (minimum 2).</param>
    /// <returns>Named islands in the order given, then Others last (only if there is anything in it).</returns>
    public static List<IslandBucket<T>> Group<T>(IEnumerable<KeyValuePair<string, List<T>>> categories,
                                                 int threshold = DefaultSmallCategoryThreshold, int maxIslands = int.MaxValue)
    {
        threshold = Math.Max(0, threshold);
        int cap = Math.Max(2, maxIslands);
        var cats = (categories ?? Enumerable.Empty<KeyValuePair<string, List<T>>>())
            .Where(c => c.Value != null && c.Value.Count > 0).ToList();

        var named = cats.Where(c => c.Value.Count > threshold).ToList();
        bool anySmall = named.Count != cats.Count;

        // Too many named islands: keep the largest ones (ties keep their display order) and fold the rest into Others.
        int namedAllowed = anySmall ? cap - 1 : cap;
        if (named.Count > namedAllowed)
        {
            namedAllowed = cap - 1;
            var keepKeys = named.Select((c, i) => (c, i))
                .OrderByDescending(x => x.c.Value.Count).ThenBy(x => x.i)
                .Take(namedAllowed).Select(x => x.c.Key)
                .ToHashSet(StringComparer.OrdinalIgnoreCase);
            named = named.Where(c => keepKeys.Contains(c.Key)).ToList();
        }

        var namedKeys = new HashSet<string>(named.Select(c => c.Key), StringComparer.OrdinalIgnoreCase);
        var folded = cats.Where(c => !namedKeys.Contains(c.Key)).ToList();   // original display order

        var result = new List<IslandBucket<T>>();
        foreach (var c in named)
            result.Add(new IslandBucket<T> { Name = c.Key, Items = new List<T>(c.Value), Sources = { c.Key } });
        if (folded.Count > 0)
        {
            var others = new IslandBucket<T> { Name = OthersName, IsOthers = true };
            foreach (var c in folded)
            {
                others.Items.AddRange(c.Value);
                others.Sources.Add(c.Key);
            }
            result.Add(others);
        }
        return result;
    }
}
