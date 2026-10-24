using System;
using System.Collections.Generic;
using Godot;

namespace ClashExplorer.World;

/// <summary>
/// Maps file extensions and folders to building sprites.
/// Assign it to WorldGenerator.AssetConfig in the Inspector, or leave that empty to use
/// BuildingAssetConfig.CreateDefault(), which wires up the PNGs in res://Assets/Buildings.
/// </summary>
[GlobalClass]
public partial class BuildingAssetConfig : Resource
{
    private const string BuildingsDir = "res://Assets/Buildings/";

    [ExportGroup("Fallbacks")]
    [Export] public Texture2D? DefaultTexture { get; set; }
    [Export] public Texture2D? SpecialLocationTexture { get; set; }

    [ExportGroup("Folders")]
    [Export] public Texture2D? FolderSmallTexture { get; set; }
    [Export] public Texture2D? FolderMediumTexture { get; set; }
    [Export] public Texture2D? FolderLargeTexture { get; set; }

    [ExportGroup("File types")]
    [Export] public Godot.Collections.Array<BuildingAssetEntry> Entries { get; set; } = new();

    private Dictionary<string, BuildingAssetEntry>? _lookup;
    private int _lookupEntryCount = -1;
    private static ImageTexture? _placeholder;

    /// <summary>Lower-cases and ensures a single leading dot. Returns "" for null/blank input.</summary>
    public static string NormalizeExtension(string? extension)
    {
        if (string.IsNullOrWhiteSpace(extension)) return "";
        string e = extension.Trim().ToLowerInvariant();
        e = e.TrimStart('.');
        return e.Length == 0 ? "" : "." + e;
    }

    /// <summary>Call after editing Entries from code so the extension lookup is rebuilt.</summary>
    public void InvalidateCache()
    {
        _lookup = null;
        _lookupEntryCount = -1;
    }

    /// <summary>Entry that handles this extension, or null when none is configured.</summary>
    public BuildingAssetEntry? FindEntry(string? extension)
    {
        string key = NormalizeExtension(extension);
        if (key.Length == 0) return null;
        EnsureLookup();
        return _lookup != null && _lookup.TryGetValue(key, out BuildingAssetEntry? entry) ? entry : null;
    }

    /// <summary>
    /// Always returns a usable texture: specific entry, then Default, then a generated placeholder.
    /// folderWeight01 (0..1) picks the small/medium/large folder sprite.
    /// </summary>
    public Texture2D GetTexture(string? extension, bool isFolder, float folderWeight01 = 0.5f)
    {
        if (isFolder)
        {
            Texture2D? folder = PickFolderTexture(folderWeight01);
            if (folder != null) return folder;
            return DefaultTexture ?? GetPlaceholder();
        }

        BuildingAssetEntry? entry = FindEntry(extension);
        if (entry?.Texture != null) return entry.Texture;
        return DefaultTexture ?? GetPlaceholder();
    }

    /// <summary>Per-sprite size correction for the entry that handles this extension (1 if none).</summary>
    public float GetBaseScale(string? extension, bool isFolder)
    {
        if (isFolder) return 1.0f;
        BuildingAssetEntry? entry = FindEntry(extension);
        float s = entry?.BaseScale ?? 1.0f;
        return float.IsFinite(s) && s > 0.01f ? s : 1.0f;
    }

    private Texture2D? PickFolderTexture(float weight01)
    {
        float w = float.IsFinite(weight01) ? Mathf.Clamp(weight01, 0f, 1f) : 0.5f;
        Texture2D?[] order = w < 0.34f
            ? new[] { FolderSmallTexture, FolderMediumTexture, FolderLargeTexture }
            : w < 0.67f
                ? new[] { FolderMediumTexture, FolderSmallTexture, FolderLargeTexture }
                : new[] { FolderLargeTexture, FolderMediumTexture, FolderSmallTexture };
        foreach (Texture2D? t in order)
        {
            if (t != null) return t;
        }
        return null;
    }

    private void EnsureLookup()
    {
        if (_lookup != null && _lookupEntryCount == Entries.Count) return;

        var map = new Dictionary<string, BuildingAssetEntry>(StringComparer.Ordinal);
        foreach (BuildingAssetEntry? entry in Entries)
        {
            if (entry == null) continue;
            foreach (string raw in entry.Extensions)
            {
                string key = NormalizeExtension(raw);
                if (key.Length == 0) continue;
                map[key] = entry; // later entries override earlier ones
            }
        }
        _lookup = map;
        _lookupEntryCount = Entries.Count;
    }

    private static Texture2D GetPlaceholder()
    {
        if (_placeholder != null) return _placeholder;
        var img = Image.CreateEmpty(96, 96, false, Image.Format.Rgba8);
        img.Fill(new Color(0.35f, 0.38f, 0.45f, 0.9f));
        for (int i = 0; i < 96; i++)
        {
            img.SetPixel(i, 0, Colors.White);
            img.SetPixel(i, 95, Colors.White);
            img.SetPixel(0, i, Colors.White);
            img.SetPixel(95, i, Colors.White);
        }
        _placeholder = ImageTexture.CreateFromImage(img);
        return _placeholder;
    }

    /// <summary>
    /// Builds the default configuration from the sprites in res://Assets/Buildings.
    /// Missing PNGs are skipped with a warning; the config still works through fallbacks.
    /// </summary>
    public static BuildingAssetConfig CreateDefault()
    {
        var config = new BuildingAssetConfig
        {
            DefaultTexture = LoadTexture("generic_file.png"),
            SpecialLocationTexture = LoadTexture("drive_special_location.png"),
            FolderSmallTexture = LoadTexture("folder_cluster_small.png"),
            FolderMediumTexture = LoadTexture("folder_cluster_medium.png"),
            FolderLargeTexture = LoadTexture("folder_cluster_large.png")
        };

        config.Entries.Add(MakeEntry("Python", "code_python", "python_file.png",
            ".py", ".pyw", ".ipynb"));
        config.Entries.Add(MakeEntry("Code", "code", "code_file.png",
            ".cs", ".cpp", ".c", ".h", ".hpp", ".js", ".ts", ".html", ".css", ".json", ".xml",
            ".java", ".rs", ".go", ".sh", ".bat", ".ps1", ".yml", ".yaml", ".gd", ".gdshader", ".sql"));
        config.Entries.Add(MakeEntry("Documents", "documents", "document_file.png",
            ".pdf", ".doc", ".docx", ".txt", ".md", ".rtf", ".xls", ".xlsx", ".ppt", ".pptx", ".csv"));
        config.Entries.Add(MakeEntry("Images", "images", "image_file.png",
            ".png", ".jpg", ".jpeg", ".gif", ".bmp", ".webp", ".svg", ".ico"));
        config.Entries.Add(MakeEntry("Video", "video", "video_file.png",
            ".mp4", ".mkv", ".avi", ".mov", ".webm", ".wmv"));
        config.Entries.Add(MakeEntry("Audio", "audio", "audio_file.png",
            ".mp3", ".wav", ".flac", ".ogg", ".m4a", ".aac"));
        config.Entries.Add(MakeEntry("Archives", "archives", "archive_file.png",
            ".zip", ".rar", ".7z", ".tar", ".gz"));

        return config;
    }

    private static BuildingAssetEntry MakeEntry(string label, string id, string fileName, params string[] extensions)
    {
        return new BuildingAssetEntry
        {
            Label = label,
            ResourceName = id,
            Extensions = extensions,
            Texture = LoadTexture(fileName),
            BaseScale = 1.0f
        };
    }

    private static Texture2D? LoadTexture(string fileName)
    {
        string path = BuildingsDir + fileName;
        try
        {
            if (ResourceLoader.Exists(path)) return GD.Load<Texture2D>(path);
        }
        catch (Exception ex)
        {
            GD.PushWarning($"[BuildingAssetConfig] Failed to load {path}: {ex.Message}");
            return null;
        }
        GD.PushWarning($"[BuildingAssetConfig] Sprite not found: {path}");
        return null;
    }
}
