using System;
using Godot;

namespace ClashExplorer.World;

/// <summary>
/// One row of BuildingAssetConfig: a group of file extensions that share a sprite.
/// Create these in the Inspector (Entries array) and drag a PNG into Texture.
/// </summary>
[GlobalClass]
public partial class BuildingAssetEntry : Resource
{
    /// <summary>Friendly name shown in the Inspector, e.g. "Code workshops".</summary>
    [Export] public string Label { get; set; } = "";

    /// <summary>Extensions handled by this entry, with or without the dot (".py" or "py").</summary>
    [Export] public string[] Extensions { get; set; } = Array.Empty<string>();

    [Export] public Texture2D? Texture { get; set; }

    /// <summary>Per-sprite size correction so differently sized PNGs look equally large.</summary>
    [Export(PropertyHint.Range, "0.1,4,0.01")] public float BaseScale { get; set; } = 1.0f;
}
