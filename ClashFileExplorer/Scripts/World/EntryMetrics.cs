using System;

namespace ClashExplorer.World;

/// <summary>Filesystem / usage properties that can drive a visual property.</summary>
public enum SourceProperty
{
    None = 0,
    FileSize,
    RecursiveSize,
    ExtensionHash,
    ModifiedAgeDays,
    AccessedAgeDays,
    CreatedAgeDays,
    UsageScore,
    UsageFrequency,
    InteractionRecencyDays,
    HierarchyDepth,
    NameHash,
    NameLength,
    IsArchive,
    IsFolder
}

/// <summary>Visual characteristics of the world that can be driven by a source property.</summary>
public enum VisualProperty
{
    Scale = 0,
    HeightOffset,
    Tint,
    GlowIntensity,
    RingDistance,
    AngularOrder,
    ClusterGroup,
    AnimationSpeed
}

public enum MappingTransform
{
    Linear = 0,
    Logarithmic
}

public enum MappingScope
{
    All = 0,
    FilesOnly,
    FoldersOnly
}

/// <summary>
/// Plain data snapshot describing one file or folder. Built by the WorldGenerator on a worker
/// thread, consumed by PropertyMappingConfig and SpatialRingLayout. Contains no Godot objects,
/// so it is safe to create and read off the main thread.
/// </summary>
public sealed class EntryMetrics
{
    public string Path { get; set; } = "";
    public string Name { get; set; } = "";

    /// <summary>Lower-case extension with leading dot (".py"), or "" for none / folders.</summary>
    public string Extension { get; set; } = "";

    public bool IsFolder { get; set; }
    public bool IsArchive { get; set; }

    /// <summary>File length in bytes, or -1 when unavailable.</summary>
    public long SizeBytes { get; set; } = -1;

    /// <summary>Recursive folder size in bytes, or -1 while pending / unavailable.</summary>
    public long RecursiveSizeBytes { get; set; } = -1;

    public DateTime? ModifiedUtc { get; set; }
    public DateTime? AccessedUtc { get; set; }
    public DateTime? CreatedUtc { get; set; }

    /// <summary>Combined relevance 0..1 (see PropertyMappingConfig.ComputeRelevance).</summary>
    public double UsageScore { get; set; }

    /// <summary>Number of recorded interactions from the UsageTracker.</summary>
    public int UsageCount { get; set; }

    public DateTime? LastInteractionUtc { get; set; }

    /// <summary>Depth below the root directory currently displayed (0 = direct child).</summary>
    public int Depth { get; set; }

    /// <summary>
    /// Returns the raw value of a source property, or double.NaN when the value is unavailable
    /// or unreliable. Callers must treat NaN as "use the fallback".
    /// </summary>
    public double GetValue(SourceProperty source, DateTime nowUtc)
    {
        switch (source)
        {
            case SourceProperty.FileSize:
                return !IsFolder && SizeBytes >= 0 ? SizeBytes : double.NaN;
            case SourceProperty.RecursiveSize:
                if (IsFolder) return RecursiveSizeBytes >= 0 ? RecursiveSizeBytes : double.NaN;
                return SizeBytes >= 0 ? SizeBytes : double.NaN;
            case SourceProperty.ExtensionHash:
                return IsFolder ? 0.0 : StableHash01(Extension);
            case SourceProperty.ModifiedAgeDays:
                return AgeDays(ModifiedUtc, nowUtc);
            case SourceProperty.AccessedAgeDays:
                return AgeDays(AccessedUtc, nowUtc);
            case SourceProperty.CreatedAgeDays:
                return AgeDays(CreatedUtc, nowUtc);
            case SourceProperty.UsageScore:
                return double.IsFinite(UsageScore) ? UsageScore : 0.0;
            case SourceProperty.UsageFrequency:
                return UsageCount;
            case SourceProperty.InteractionRecencyDays:
                return AgeDays(LastInteractionUtc, nowUtc);
            case SourceProperty.HierarchyDepth:
                return Depth;
            case SourceProperty.NameHash:
                return StableHash01(Name);
            case SourceProperty.NameLength:
                return Name.Length;
            case SourceProperty.IsArchive:
                return IsArchive ? 1.0 : 0.0;
            case SourceProperty.IsFolder:
                return IsFolder ? 1.0 : 0.0;
            default:
                return double.NaN;
        }
    }

    /// <summary>Age in days; NaN when the timestamp is missing or implausible (before 1980).</summary>
    private static double AgeDays(DateTime? timestamp, DateTime nowUtc)
    {
        if (!timestamp.HasValue) return double.NaN;
        DateTime t = timestamp.Value;
        if (t.Year < 1980) return double.NaN;
        double days = (nowUtc - t).TotalDays;
        if (!double.IsFinite(days)) return double.NaN;
        return days < 0.0 ? 0.0 : days;
    }

    /// <summary>Deterministic FNV-1a hash of the lower-cased string, mapped to 0..1.</summary>
    public static double StableHash01(string? text)
    {
        if (string.IsNullOrEmpty(text)) return 0.0;
        unchecked
        {
            uint hash = 2166136261;
            foreach (char c in text.ToLowerInvariant())
            {
                hash ^= c;
                hash *= 16777619;
            }
            return hash / (double)uint.MaxValue;
        }
    }
}
