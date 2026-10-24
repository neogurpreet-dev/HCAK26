using System;
using Godot;

namespace ClashExplorer.World;

/// <summary>
/// One rule: "source property -> visual property". Pure data plus math; holds no scene references.
/// After changing any field at runtime, call PropertyMappingConfig.Touch() so the world refreshes.
/// All methods are for the main thread because the extension-filter cache is not synchronized.
/// </summary>
[GlobalClass]
public partial class PropertyMapping : Resource
{
    [Export] public bool Enabled { get; set; } = true;
    [Export] public string Label { get; set; } = "";

    [ExportGroup("Mapping")]
    [Export] public SourceProperty Source { get; set; } = SourceProperty.FileSize;
    [Export] public VisualProperty Target { get; set; } = VisualProperty.Scale;
    [Export] public MappingTransform TransformMode { get; set; } = MappingTransform.Logarithmic;

    [ExportGroup("Input range")]
    [Export] public float InputMin { get; set; } = 0f;
    [Export] public float InputMax { get; set; } = 1_000_000_000f;

    [ExportGroup("Output range")]
    [Export] public float OutputMin { get; set; } = 0.6f;
    [Export] public float OutputMax { get; set; } = 2.2f;
    [Export] public bool ClampOutput { get; set; } = true;
    [Export] public bool Invert { get; set; } = false;

    [ExportGroup("Scope and overrides")]
    [Export] public MappingScope Scope { get; set; } = MappingScope.All;

    /// <summary>
    /// Comma-separated extensions (".py,.cs"). When set, the mapping only applies to those file
    /// types and wins over a general mapping for the same target.
    /// </summary>
    [Export] public string ExtensionFilter { get; set; } = "";

    [ExportGroup("Color")]
    /// <summary>Used when Target is Tint. If empty a soft rainbow ramp is used.</summary>
    [Export] public Gradient? ColorRamp { get; set; }

    private string _filterCacheKey = "\u0001";
    private string[] _filterParts = Array.Empty<string>();

    /// <summary>True when this mapping is a type-specific override (scope or extension filter).</summary>
    public bool IsOverride =>
        Scope != MappingScope.All || !string.IsNullOrWhiteSpace(ExtensionFilter);

    public bool AppliesTo(EntryMetrics entry)
    {
        if (!Enabled || Source == SourceProperty.None) return false;
        if (Scope == MappingScope.FilesOnly && entry.IsFolder) return false;
        if (Scope == MappingScope.FoldersOnly && !entry.IsFolder) return false;

        string[] parts = GetFilterParts();
        if (parts.Length == 0) return true;
        if (entry.IsFolder) return false;
        return Array.IndexOf(parts, entry.Extension) >= 0;
    }

    /// <summary>
    /// Evaluates the source property and returns its normalized position 0..1 (after transform,
    /// clamping and inversion). Returns false when the source value is unavailable.
    /// </summary>
    public bool TryEvaluateNormalized(EntryMetrics entry, DateTime nowUtc, out float normalized)
    {
        normalized = 0f;
        double raw = entry.GetValue(Source, nowUtc);
        if (!double.IsFinite(raw)) return false;
        normalized = Normalize(raw);
        return true;
    }

    /// <summary>Applies range, transform, clamp and inversion. Never returns NaN or infinity.</summary>
    public float Normalize(double raw)
    {
        double min = double.IsFinite(InputMin) ? InputMin : 0.0;
        double max = double.IsFinite(InputMax) ? InputMax : min + 1.0;
        double range = max - min;
        if (range < 1e-9) range = 1e-9;

        double x = raw - min;
        double t;
        if (TransformMode == MappingTransform.Logarithmic)
        {
            x = Math.Max(0.0, x);
            t = Math.Log(1.0 + x) / Math.Log(1.0 + range);
        }
        else
        {
            t = x / range;
        }

        if (!double.IsFinite(t)) t = 0.0;
        t = ClampOutput ? Math.Clamp(t, 0.0, 1.0) : Math.Clamp(t, -1.0, 2.0);
        if (Invert) t = 1.0 - t;
        return (float)t;
    }

    /// <summary>Maps a normalized value onto OutputMin..OutputMax.</summary>
    public float MapToOutput(float normalized)
    {
        float lo = float.IsFinite(OutputMin) ? OutputMin : 0f;
        float hi = float.IsFinite(OutputMax) ? OutputMax : 1f;
        float value = Mathf.Lerp(lo, hi, normalized);
        return float.IsFinite(value) ? value : (lo + hi) * 0.5f;
    }

    public Color MapToColor(float normalized)
    {
        float t = Mathf.Clamp(normalized, 0f, 1f);
        if (ColorRamp != null) return ColorRamp.Sample(t);
        return Color.FromHsv(t * 0.85f, 0.45f, 1f);
    }

    private string[] GetFilterParts()
    {
        string filter = ExtensionFilter ?? "";
        if (filter == _filterCacheKey) return _filterParts;

        var list = new System.Collections.Generic.List<string>();
        foreach (string raw in filter.Split(new[] { ',', ';', ' ' }, StringSplitOptions.RemoveEmptyEntries))
        {
            string norm = BuildingAssetConfig.NormalizeExtension(raw);
            if (norm.Length > 0) list.Add(norm);
        }
        _filterParts = list.ToArray();
        _filterCacheKey = filter;
        return _filterParts;
    }
}
