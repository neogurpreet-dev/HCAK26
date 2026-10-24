using System.Collections.Generic;
using Godot;

namespace ClashExplorer.World;

/// <summary>Input for SpatialRingLayout: one building to place. Plain data, thread-safe.</summary>
public sealed class LayoutItem
{
    /// <summary>Stable identity, normally the absolute path of the file or folder.</summary>
    public string Key { get; set; } = "";

    /// <summary>Footprint radius in world units. Use SpatialRingLayout.EstimateFootprintRadius.</summary>
    public float Radius { get; set; } = 60f;

    /// <summary>0 = innermost ring, 1 = outermost. Items are placed in ascending order of this value.</summary>
    public float RingPreference { get; set; } = 0.5f;

    /// <summary>Optional grouping. Items of the same group are placed next to each other within a ring.</summary>
    public int ClusterGroup { get; set; }
}

public sealed class PlacedItem
{
    /// <summary>Index of the item in the input list.</summary>
    public int Index { get; init; }
    public string Key { get; init; } = "";
    public Vector2 Position { get; init; }
    public int Ring { get; init; }
    public float AngleRadians { get; init; }
    public float Radius { get; init; }
}

public sealed class LayoutResult
{
    public List<PlacedItem> Placed { get; } = new();

    /// <summary>Indices of items that did not fit inside MaxWorldRadius (normally empty).</summary>
    public List<int> Unplaced { get; } = new();

    public int RingCount { get; set; }

    /// <summary>Center radius of each ring in ring space, indexed by PlacedItem.Ring.</summary>
    public List<float> RingRadii { get; } = new();

    /// <summary>Y-squash applied to the rings (1 = circles). The terrain draws its platforms with the same value.</summary>
    public float VerticalSquash { get; set; } = 1f;

    /// <summary>Distance from the origin to the outer edge of the outermost ring (in ring space).</summary>
    public float OuterRadius { get; set; }

    /// <summary>Suggested position for the overflow "+" structure, just outside the outer ring.</summary>
    public Vector2 OverflowPosition { get; set; }

    /// <summary>Bounding box of all placed buildings including their footprints.</summary>
    public Rect2 Bounds { get; set; }
}
