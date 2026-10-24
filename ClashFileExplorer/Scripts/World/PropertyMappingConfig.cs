using System;
using Godot;

namespace ClashExplorer.World;

/// <summary>
/// The full set of property mappings plus the relevance-scoring weights. Persisted to
/// user://property_mapping.tres. Consumers (WorldGenerator, SpatialRingLayout) read it and
/// listen to the Changed signal; the mapping UI (Part 2) edits it and calls Touch().
/// </summary>
[GlobalClass]
public partial class PropertyMappingConfig : Resource
{
    public const int CurrentSchemaVersion = 1;
    public const string DefaultUserPath = "user://property_mapping.tres";

    [Export] public int SchemaVersion { get; set; } = CurrentSchemaVersion;
    [Export] public Godot.Collections.Array<PropertyMapping> Mappings { get; set; } = new();

    [ExportGroup("Relevance scoring")]
    /// <summary>Weight of how often the item was opened in this app.</summary>
    [Export(PropertyHint.Range, "0,1,0.01")] public float FrequencyWeight { get; set; } = 0.45f;
    /// <summary>Weight of how recently the item was opened in this app.</summary>
    [Export(PropertyHint.Range, "0,1,0.01")] public float RecencyWeight { get; set; } = 0.35f;
    /// <summary>Weight of the filesystem modified/accessed timestamp (a weaker, complementary signal).</summary>
    [Export(PropertyHint.Range, "0,1,0.01")] public float FilesystemWeight { get; set; } = 0.20f;
    /// <summary>Days after which the recency signal halves.</summary>
    [Export(PropertyHint.Range, "1,365,1")] public float RecencyHalfLifeDays { get; set; } = 14f;
    /// <summary>Interaction count at which the frequency signal reaches about 63 percent.</summary>
    [Export(PropertyHint.Range, "1,200,1")] public float FrequencySaturation { get; set; } = 12f;

    [ExportGroup("Safety limits")]
    [Export(PropertyHint.Range, "0.1,1,0.01")] public float SafeScaleMin { get; set; } = 0.4f;
    [Export(PropertyHint.Range, "1,6,0.01")] public float SafeScaleMax { get; set; } = 3.0f;

    /// <summary>Incremented by Touch(); lets consumers detect edits cheaply.</summary>
    public int Revision { get; private set; }

    /// <summary>Call after editing mappings or weights so listeners rebuild the world.</summary>
    public void Touch()
    {
        Revision++;
        EmitChanged();
    }

    // ---------------------------------------------------------------- resolving

    /// <summary>
    /// Finds the best mapping for a target (type-specific overrides first) and returns its
    /// normalized 0..1 position. False when no mapping applies or the source value is unavailable.
    /// </summary>
    public bool TryResolveNormalized(VisualProperty target, EntryMetrics entry, DateTime nowUtc,
        out float normalized, out PropertyMapping? used)
    {
        for (int pass = 0; pass < 2; pass++)
        {
            bool wantOverride = pass == 0;
            foreach (PropertyMapping? mapping in Mappings)
            {
                if (mapping == null || mapping.Target != target) continue;
                if (mapping.IsOverride != wantOverride) continue;
                if (!mapping.AppliesTo(entry)) continue;
                if (mapping.TryEvaluateNormalized(entry, nowUtc, out normalized))
                {
                    used = mapping;
                    return true;
                }
            }
        }
        normalized = 0f;
        used = null;
        return false;
    }

    /// <summary>Mapped numeric value for a target, or fallback when nothing applies.</summary>
    public float ResolveValue(VisualProperty target, EntryMetrics entry, DateTime nowUtc, float fallback)
    {
        if (TryResolveNormalized(target, entry, nowUtc, out float t, out PropertyMapping? mapping) && mapping != null)
        {
            return mapping.MapToOutput(t);
        }
        return fallback;
    }

    /// <summary>Building scale, always finite and within the safety limits.</summary>
    public float ResolveScale(EntryMetrics entry, DateTime nowUtc, float fallback = 1.0f)
    {
        float v = ResolveValue(VisualProperty.Scale, entry, nowUtc, fallback);
        if (!float.IsFinite(v)) v = 1.0f;
        float lo = Mathf.Min(SafeScaleMin, SafeScaleMax);
        float hi = Mathf.Max(SafeScaleMin, SafeScaleMax);
        return Mathf.Clamp(v, lo, hi);
    }

    /// <summary>Tint color; white (no tint) when no Tint mapping applies.</summary>
    public Color ResolveTint(EntryMetrics entry, DateTime nowUtc)
    {
        if (TryResolveNormalized(VisualProperty.Tint, entry, nowUtc, out float t, out PropertyMapping? mapping) && mapping != null)
        {
            return mapping.MapToColor(t);
        }
        return Colors.White;
    }

    /// <summary>
    /// Ring preference 0..1 (0 = innermost, 1 = outermost). The layout sorts by this value, so
    /// any source can drive it. Falls back to 0.5 when unavailable.
    /// </summary>
    public float ResolveRingPreference(EntryMetrics entry, DateTime nowUtc)
    {
        float v = ResolveValue(VisualProperty.RingDistance, entry, nowUtc, 0.5f);
        return float.IsFinite(v) ? Mathf.Clamp(v, 0f, 1f) : 0.5f;
    }

    // ---------------------------------------------------------------- scoring

    /// <summary>
    /// Relevance 0..1 = weighted blend of three signals (weights are normalized):
    ///   frequency  = 1 - exp(-usageCount / FrequencySaturation)
    ///   recency    = 0.5 ^ (daysSinceInteraction / RecencyHalfLifeDays)
    ///   filesystem = 0.5 ^ (daysSinceModifiedOrAccessed / (4 * RecencyHalfLifeDays))
    /// App-recorded usage is trusted more than filesystem timestamps because Windows may not
    /// update LastAccessTime. Missing signals contribute 0.
    /// </summary>
    public double ComputeRelevance(int usageCount, DateTime? lastInteractionUtc,
        DateTime? modifiedUtc, DateTime? accessedUtc, DateTime nowUtc)
    {
        double halfLife = Math.Max(1.0, RecencyHalfLifeDays);
        double saturation = Math.Max(1.0, FrequencySaturation);

        double frequency = usageCount > 0 ? 1.0 - Math.Exp(-usageCount / saturation) : 0.0;
        double recency = DecayFromAge(lastInteractionUtc, nowUtc, halfLife);

        DateTime? fsTime = modifiedUtc;
        if (accessedUtc.HasValue && (!fsTime.HasValue || accessedUtc.Value > fsTime.Value)) fsTime = accessedUtc;
        double filesystem = DecayFromAge(fsTime, nowUtc, halfLife * 4.0);

        double wf = Math.Max(0.0, FrequencyWeight);
        double wr = Math.Max(0.0, RecencyWeight);
        double ws = Math.Max(0.0, FilesystemWeight);
        double total = wf + wr + ws;
        if (total <= 1e-9) return 0.0;

        double score = (wf * frequency + wr * recency + ws * filesystem) / total;
        return double.IsFinite(score) ? Math.Clamp(score, 0.0, 1.0) : 0.0;
    }

    private static double DecayFromAge(DateTime? timestamp, DateTime nowUtc, double halfLifeDays)
    {
        if (!timestamp.HasValue || timestamp.Value.Year < 1980) return 0.0;
        double days = Math.Max(0.0, (nowUtc - timestamp.Value).TotalDays);
        double v = Math.Pow(0.5, days / halfLifeDays);
        return double.IsFinite(v) ? v : 0.0;
    }

    // ---------------------------------------------------------------- presets and persistence

    /// <summary>
    /// Default preset: usage-based rings (high usage = inner ring), logarithmic size-based
    /// scale 0.6 to 2.2, and a glow for recently modified items.
    /// </summary>
    public static PropertyMappingConfig CreateDefault()
    {
        var config = new PropertyMappingConfig();

        config.Mappings.Add(new PropertyMapping
        {
            Label = "File size -> scale (log)",
            Source = SourceProperty.FileSize,
            Target = VisualProperty.Scale,
            Scope = MappingScope.FilesOnly,
            TransformMode = MappingTransform.Logarithmic,
            InputMin = 0f, InputMax = 1_000_000_000f,
            OutputMin = 0.6f, OutputMax = 2.2f
        });
        config.Mappings.Add(new PropertyMapping
        {
            Label = "Folder size -> scale (log)",
            Source = SourceProperty.RecursiveSize,
            Target = VisualProperty.Scale,
            Scope = MappingScope.FoldersOnly,
            TransformMode = MappingTransform.Logarithmic,
            InputMin = 0f, InputMax = 20_000_000_000f,
            OutputMin = 0.9f, OutputMax = 2.2f
        });
        config.Mappings.Add(new PropertyMapping
        {
            Label = "Usage -> ring (frequent = inner)",
            Source = SourceProperty.UsageScore,
            Target = VisualProperty.RingDistance,
            TransformMode = MappingTransform.Linear,
            InputMin = 0f, InputMax = 1f,
            OutputMin = 0f, OutputMax = 1f,
            Invert = true
        });
        config.Mappings.Add(new PropertyMapping
        {
            Label = "Recently modified -> glow",
            Source = SourceProperty.ModifiedAgeDays,
            Target = VisualProperty.GlowIntensity,
            TransformMode = MappingTransform.Logarithmic,
            InputMin = 0f, InputMax = 365f,
            OutputMin = 0f, OutputMax = 1f,
            Invert = true
        });
        config.Mappings.Add(new PropertyMapping
        {
            Label = "File type -> tint (off by default)",
            Enabled = false,
            Source = SourceProperty.ExtensionHash,
            Target = VisualProperty.Tint,
            TransformMode = MappingTransform.Linear,
            InputMin = 0f, InputMax = 1f,
            OutputMin = 0f, OutputMax = 1f
        });

        return config;
    }

    /// <summary>Restores the default preset in place (keeps this instance so listeners stay connected).</summary>
    public void ResetToDefault()
    {
        PropertyMappingConfig d = CreateDefault();
        Mappings = d.Mappings;
        FrequencyWeight = d.FrequencyWeight;
        RecencyWeight = d.RecencyWeight;
        FilesystemWeight = d.FilesystemWeight;
        RecencyHalfLifeDays = d.RecencyHalfLifeDays;
        FrequencySaturation = d.FrequencySaturation;
        SafeScaleMin = d.SafeScaleMin;
        SafeScaleMax = d.SafeScaleMax;
        Touch();
    }

    /// <summary>Loads saved preferences; falls back to the default preset when missing, outdated or corrupt.</summary>
    public static PropertyMappingConfig LoadOrDefault(string path = DefaultUserPath)
    {
        try
        {
            if (ResourceLoader.Exists(path))
            {
                var loaded = ResourceLoader.Load(path, "", ResourceLoader.CacheMode.Ignore) as PropertyMappingConfig;
                if (loaded != null && loaded.SchemaVersion == CurrentSchemaVersion && loaded.Mappings.Count > 0)
                {
                    return loaded;
                }
                GD.PushWarning("[PropertyMappingConfig] Saved mapping is outdated or empty; using defaults.");
            }
        }
        catch (Exception ex)
        {
            GD.PushWarning($"[PropertyMappingConfig] Could not load {path}: {ex.Message}");
        }
        return CreateDefault();
    }

    /// <summary>Saves to user:// (local app data). Returns false and logs on failure.</summary>
    public bool Save(string path = DefaultUserPath)
    {
        try
        {
            SchemaVersion = CurrentSchemaVersion;
            Error err = ResourceSaver.Save(this, path);
            if (err != Error.Ok)
            {
                GD.PushWarning($"[PropertyMappingConfig] Save failed ({err}) for {path}");
                return false;
            }
            return true;
        }
        catch (Exception ex)
        {
            GD.PushWarning($"[PropertyMappingConfig] Save failed: {ex.Message}");
            return false;
        }
    }
}
