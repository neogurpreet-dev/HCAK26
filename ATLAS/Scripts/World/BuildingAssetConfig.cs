using System;
using System.Collections.Generic;
using Godot;

namespace Atlas;

/// <summary>One Inspector row: a file extension (e.g. "py") or category ("Code", "Folder", "Default").</summary>
[GlobalClass]
public partial class AssetEntry : Resource
{
    /// <summary>Extension with or without a dot ("py", ".PY") or a category/special key ("Code", "Folder", "Default").</summary>
    [Export] public string Key { get; set; } = "";
    /// <summary>Optional PNG/SVG decal shown on the building face.</summary>
    [Export] public Texture2D Icon { get; set; }
    /// <summary>Optional albedo texture layered onto the generated body (the body tint still applies).</summary>
    [Export] public Texture2D AlbedoTexture { get; set; }
    /// <summary>Optional full material replacing the generated base material.</summary>
    [Export] public Material MaterialOverride { get; set; }
}

/// <summary>
/// Maps file types to optional textures/materials for the procedurally generated buildings.
/// No model files are required: with an empty config every building gets a generated SVG decal.
/// Lookup order: extension -> category -> "Folder" (directories) -> "Default".
/// </summary>
[GlobalClass]
public partial class BuildingAssetConfig : Resource
{
    [Export] public Godot.Collections.Array<AssetEntry> Entries { get; set; } = new();

    Dictionary<string, AssetEntry> _lookup;

    /// <summary>Call after editing <see cref="Entries"/> at runtime.</summary>
    public void Rebuild()
    {
        _lookup = new Dictionary<string, AssetEntry>(StringComparer.OrdinalIgnoreCase);
        foreach (var e in Entries)
        {
            if (e == null || string.IsNullOrWhiteSpace(e.Key)) continue;
            _lookup[NormaliseKey(e.Key)] = e;
        }
    }

    public static string NormaliseKey(string key)
    {
        key = key.Trim();
        // Category/special keys keep their name; extensions lose the dot and are lower-cased.
        if (key.Equals("Default", StringComparison.OrdinalIgnoreCase) || key.Equals("Folder", StringComparison.OrdinalIgnoreCase)) return key.ToLowerInvariant();
        foreach (var c in FileCategory.Ordered) if (key.Equals(c, StringComparison.OrdinalIgnoreCase)) return c.ToLowerInvariant();
        return FileCategory.NormalizeExtension(key);
    }

    public AssetEntry Resolve(FsEntry e)
    {
        if (_lookup == null) Rebuild();
        if (e.IsDirectory)
        {
            if (_lookup.TryGetValue("folder", out var f)) return f;
        }
        else
        {
            if (e.Extension.Length > 0 && _lookup.TryGetValue(e.Extension, out var byExt)) return byExt;
            if (_lookup.TryGetValue(e.Category.ToLowerInvariant(), out var byCat)) return byCat;
        }
        return _lookup.TryGetValue("default", out var d) ? d : null;
    }

    public Texture2D ResolveIcon(FsEntry e)
    {
        var a = Resolve(e);
        return a?.Icon;
    }

    /// <summary>Creates a config that lists the common types so they are easy to fill in from the Inspector.</summary>
    public static BuildingAssetConfig CreateDefault()
    {
        var cfg = new BuildingAssetConfig();
        foreach (var key in new[] { "py", "cs", "cpp", "js", "html", "json", "pdf", "mp4", "png", "jpg", "zip", "Folder", "Default" })
            cfg.Entries.Add(new AssetEntry { Key = key });
        cfg.Rebuild();
        return cfg;
    }
}
