using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text.Json;
using Godot;

namespace Atlas;

/// <summary>Filesystem-derived values a mapping can read.</summary>
public enum SourceProperty
{
    None = 0,
    FileSize,        // files: length; folders: recursive size once computed
    RecursiveSize,   // folders: recursive size; files: length
    LastModified,    // freshness: 1 = just modified, ->0 as it ages (90-day horizon)
    LastAccessed,    // freshness of LastAccessTime (may be unreliable on Windows)
    Created,         // freshness of creation time (365-day horizon)
    UsageFrequency,  // number of times opened through ATLAS
    Recency,         // freshness of last interaction through ATLAS (7-day half-life)
    FileType,        // category index
    NameLength,
    Hierarchy,       // 0 = folder, 1 = file
    IsArchive,       // 0 / 1
}

/// <summary>Visual characteristics a mapping can drive.</summary>
public enum VisualProperty
{
    Scale = 0,
    Height,
    BaseColor,
    AccentColor,
    GlowIntensity,
    Opacity,
    RingDistance,
}

public enum TransformKind { Linear = 0, Log = 1 }

public enum GroupMode { FileType = 0, Extension, LastModified, SizeBucket, None }

/// <summary>One "source property -> visual property" rule. Inspector friendly.</summary>
[GlobalClass]
public partial class MappingRule : Resource
{
    [Export] public bool Enabled { get; set; } = true;
    [Export] public VisualProperty Target { get; set; } = VisualProperty.Scale;
    [Export] public SourceProperty Source { get; set; } = SourceProperty.FileSize;
    [Export] public TransformKind Transform { get; set; } = TransformKind.Log;
    /// <summary>Input range used for normalisation (ignored when AutoRange is true).</summary>
    [Export] public double InMin { get; set; } = 0;
    [Export] public double InMax { get; set; } = 1073741824.0;
    /// <summary>Use the min/max found in the current directory instead of InMin/InMax.</summary>
    [Export] public bool AutoRange { get; set; } = false;
    [Export] public float OutMin { get; set; } = 0.6f;
    [Export] public float OutMax { get; set; } = 2.2f;
    [Export] public bool Invert { get; set; } = false;
    [Export] public Color ColorLow { get; set; } = new Color("24407F");
    [Export] public Color ColorHigh { get; set; } = new Color("BFEFFF");
    /// <summary>Optional type-specific override: "" = all, otherwise a category name ("Code", "Folder", ...).</summary>
    [Export] public string CategoryFilter { get; set; } = "";
}

/// <summary>Per-directory statistics used by auto-ranged rules.</summary>
public sealed class MappingContext
{
    public readonly Dictionary<SourceProperty, (double min, double max)> Ranges = new();
}

/// <summary>
/// The full, runtime-editable property mapping. Fires <see cref="Changed"/> after edits so the
/// world can rebuild live; persists to user://atlas_mapping.json.
/// </summary>
[GlobalClass]
public partial class PropertyMappingConfig : Resource
{
    [Export] public GroupMode GroupBy { get; set; } = GroupMode.FileType;
    [Export] public Godot.Collections.Array<MappingRule> Rules { get; set; } = new();
    [Export] public bool AccentOverrideEnabled { get; set; } = false;
    [Export] public Color AccentOverride { get; set; } = new Color("3B82F6");

    /// <summary>Raised after any edit made through the helper methods (not Godot's Resource.Changed).</summary>
    public event Action MappingChanged;

    public void NotifyChanged()
    {
        MappingChanged?.Invoke();
        SaveSoon();
    }

    // ------------------------------------------------------------------ defaults

    public static PropertyMappingConfig CreateDefault()
    {
        var c = new PropertyMappingConfig();
        c.ResetToDefaults();
        return c;
    }

    public void ResetToDefaults()
    {
        GroupBy = GroupMode.FileType;
        AccentOverrideEnabled = false;
        Rules = new Godot.Collections.Array<MappingRule>
        {
            new MappingRule { Target = VisualProperty.Scale, Source = SourceProperty.FileSize, Transform = TransformKind.Log, InMin = 0, InMax = 1073741824.0, OutMin = 0.6f, OutMax = 2.2f },
            new MappingRule { Target = VisualProperty.Height, Source = SourceProperty.FileSize, Transform = TransformKind.Log, InMin = 0, InMax = 1073741824.0, OutMin = 1.8f, OutMax = 9.5f },
            new MappingRule { Target = VisualProperty.BaseColor, Enabled = false, Source = SourceProperty.None, Transform = TransformKind.Linear, AutoRange = true },
            new MappingRule { Target = VisualProperty.AccentColor, Enabled = false, Source = SourceProperty.None, Transform = TransformKind.Linear, AutoRange = true },
            new MappingRule { Target = VisualProperty.GlowIntensity, Source = SourceProperty.Recency, Transform = TransformKind.Linear, AutoRange = false, InMin = 0, InMax = 1, OutMin = 1.4f, OutMax = 3.0f },
            new MappingRule { Target = VisualProperty.Opacity, Source = SourceProperty.UsageFrequency, Transform = TransformKind.Linear, AutoRange = true, OutMin = 0.72f, OutMax = 1.0f },
            new MappingRule { Target = VisualProperty.RingDistance, Enabled = false, Source = SourceProperty.None, Transform = TransformKind.Linear, AutoRange = true, OutMin = 0, OutMax = 1 },
        };
    }

    // ------------------------------------------------------------------ rule access

    /// <summary>Returns the rule for a target, preferring a category-specific override when it matches.</summary>
    public MappingRule FindRule(VisualProperty target, string category = null)
    {
        MappingRule general = null;
        foreach (var r in Rules)
        {
            if (r == null || r.Target != target) continue;
            if (!string.IsNullOrEmpty(r.CategoryFilter))
            {
                if (r.CategoryFilter == category && r.Enabled) return r;
            }
            else general ??= r;
        }
        return general;
    }

    public MappingRule EnsureRule(VisualProperty target)
    {
        var r = Rules.FirstOrDefault(x => x != null && x.Target == target && string.IsNullOrEmpty(x.CategoryFilter));
        if (r == null)
        {
            r = new MappingRule { Target = target, Enabled = false, Source = SourceProperty.None };
            Rules.Add(r);
        }
        return r;
    }

    /// <summary>Convenience for the simple panel dropdowns: pick a source (None disables the rule).</summary>
    public void SetSource(VisualProperty target, SourceProperty source)
    {
        var r = EnsureRule(target);
        r.Source = source;
        r.Enabled = source != SourceProperty.None;
        if (r.Enabled) ApplySourceDefaults(r, source);
        NotifyChanged();
    }

    static void ApplySourceDefaults(MappingRule r, SourceProperty s)
    {
        bool sizeLike = s == SourceProperty.FileSize || s == SourceProperty.RecursiveSize;
        r.Transform = sizeLike ? TransformKind.Log : TransformKind.Linear;
        if (sizeLike) { r.AutoRange = false; r.InMin = 0; r.InMax = 1073741824.0; }
        else r.AutoRange = true;
    }

    public SourceProperty GetSource(VisualProperty target)
    {
        var r = FindRule(target);
        return r != null && r.Enabled ? r.Source : SourceProperty.None;
    }

    // ------------------------------------------------------------------ evaluation

    public MappingContext BuildContext(IReadOnlyList<FsEntry> entries)
    {
        var ctx = new MappingContext();
        foreach (var r in Rules)
        {
            if (r == null || !r.Enabled || !r.AutoRange || r.Source == SourceProperty.None) continue;
            if (ctx.Ranges.ContainsKey(r.Source)) continue;
            double mn = double.MaxValue, mx = double.MinValue;
            foreach (var e in entries)
            {
                var v = GetRaw(r.Source, e);
                if (!double.IsFinite(v)) continue;
                if (v < mn) mn = v;
                if (v > mx) mx = v;
            }
            if (mn > mx) { mn = 0; mx = 1; }
            ctx.Ranges[r.Source] = (mn, mx);
        }
        return ctx;
    }

    public static double GetRaw(SourceProperty s, FsEntry e)
    {
        switch (s)
        {
            case SourceProperty.FileSize: return Math.Max(0, e.EffectiveSize);
            case SourceProperty.RecursiveSize: return Math.Max(0, e.IsDirectory ? e.RecursiveSize : e.Size);
            case SourceProperty.LastModified: return Freshness(e.HasModified, e.Modified, 90);
            case SourceProperty.LastAccessed: return Freshness(e.HasAccessed, e.Accessed, 90);
            case SourceProperty.Created: return Freshness(e.HasCreated, e.Created, 365);
            case SourceProperty.UsageFrequency: return e.UsageCount;
            case SourceProperty.Recency: return e.UsageCount > 0 ? Math.Pow(2, -e.RecencyDays / 7.0) : 0;
            case SourceProperty.FileType: return FileCategory.IndexOf(e.Category);
            case SourceProperty.NameLength: return e.Name.Length;
            case SourceProperty.Hierarchy: return e.IsDirectory ? 0 : 1;
            case SourceProperty.IsArchive: return e.IsArchive ? 1 : 0;
            default: return 0;
        }
    }

    static double Freshness(bool has, DateTime t, double horizonDays)
    {
        if (!has) return 0;
        double age = Math.Max(0, (DateTime.Now - t).TotalDays);
        return Math.Exp(-age / horizonDays * 2.0);
    }

    /// <summary>Normalised 0..1 value for a rule (NaN/infinity safe).</summary>
    public static float Normalise(MappingRule r, FsEntry e, MappingContext ctx)
    {
        if (r.Source == SourceProperty.None) return 0f;
        double raw = Math.Max(0, GetRaw(r.Source, e));
        double mn = r.InMin, mx = r.InMax;
        if (r.AutoRange && ctx != null && ctx.Ranges.TryGetValue(r.Source, out var rg)) { mn = rg.min; mx = rg.max; }
        mn = Math.Max(0, mn);
        if (!double.IsFinite(mx) || mx <= mn) mx = mn + 1;
        double t;
        if (r.Transform == TransformKind.Log)
        {
            double a = Math.Log(1 + mn), b = Math.Log(1 + mx);
            t = b - a < 1e-9 ? 0 : (Math.Log(1 + raw) - a) / (b - a);
        }
        else t = (raw - mn) / (mx - mn);
        if (!double.IsFinite(t)) t = 0;
        t = Math.Clamp(t, 0, 1);
        if (r.Invert) t = 1 - t;
        return (float)t;
    }

    /// <summary>Evaluates a scalar target; returns <paramref name="fallback"/> when no enabled rule applies.</summary>
    public float EvalScalar(VisualProperty target, FsEntry e, MappingContext ctx, float fallback)
    {
        var r = FindRule(target, e.Category);
        if (r == null || !r.Enabled || r.Source == SourceProperty.None) return fallback;
        float t = Normalise(r, e, ctx);
        float v = Mathf.Lerp(r.OutMin, r.OutMax, t);
        return float.IsFinite(v) ? v : fallback;
    }

    public bool TryEvalColor(VisualProperty target, FsEntry e, MappingContext ctx, out Color color)
    {
        color = default;
        var r = FindRule(target, e.Category);
        if (r == null || !r.Enabled || r.Source == SourceProperty.None) return false;
        color = r.ColorLow.Lerp(r.ColorHigh, Normalise(r, e, ctx));
        return true;
    }

    // ------------------------------------------------------------------ persistence

    sealed class RuleDto
    {
        public bool Enabled { get; set; }
        public int Target { get; set; }
        public int Source { get; set; }
        public int Transform { get; set; }
        public double InMin { get; set; }
        public double InMax { get; set; }
        public bool AutoRange { get; set; }
        public float OutMin { get; set; }
        public float OutMax { get; set; }
        public bool Invert { get; set; }
        public string ColorLow { get; set; } = "";
        public string ColorHigh { get; set; } = "";
        public string CategoryFilter { get; set; } = "";
    }

    sealed class ConfigDto
    {
        public int Version { get; set; } = 1;
        public int GroupBy { get; set; }
        public bool AccentOverrideEnabled { get; set; }
        public string AccentOverride { get; set; } = "";
        public List<RuleDto> Rules { get; set; } = new();
    }

    static string _cachedSavePath;

    // First call happens on the main thread (LoadOrDefault); the path is cached so the
    // debounced background save never touches Godot APIs.
    static string SavePath()
    {
        if (_cachedSavePath != null) return _cachedSavePath;
        string dir;
        try { dir = ProjectSettings.GlobalizePath("user://"); }
        catch { dir = Path.Combine(System.Environment.GetFolderPath(System.Environment.SpecialFolder.ApplicationData), "ATLAS"); }
        Directory.CreateDirectory(dir);
        _cachedSavePath = Path.Combine(dir, "atlas_mapping.json");
        return _cachedSavePath;
    }

    System.Threading.Timer _saveTimer;

    void SaveSoon()
    {
        _saveTimer ??= new System.Threading.Timer(_ => Save(), null, System.Threading.Timeout.Infinite, System.Threading.Timeout.Infinite);
        _saveTimer.Change(600, System.Threading.Timeout.Infinite);
    }

    public void Save()
    {
        try
        {
            var dto = new ConfigDto
            {
                GroupBy = (int)GroupBy,
                AccentOverrideEnabled = AccentOverrideEnabled,
                AccentOverride = AccentOverride.ToHtml(true),
            };
            foreach (var r in Rules.ToList())
            {
                if (r == null) continue;
                dto.Rules.Add(new RuleDto
                {
                    Enabled = r.Enabled, Target = (int)r.Target, Source = (int)r.Source, Transform = (int)r.Transform,
                    InMin = r.InMin, InMax = r.InMax, AutoRange = r.AutoRange, OutMin = r.OutMin, OutMax = r.OutMax,
                    Invert = r.Invert, ColorLow = r.ColorLow.ToHtml(true), ColorHigh = r.ColorHigh.ToHtml(true),
                    CategoryFilter = r.CategoryFilter ?? "",
                });
            }
            var path = SavePath();
            var tmp = path + ".tmp";
            File.WriteAllText(tmp, JsonSerializer.Serialize(dto));
            if (File.Exists(path)) File.Replace(tmp, path, null); else File.Move(tmp, path);
        }
        catch (Exception ex) { GD.PushWarning($"[ATLAS] could not save mapping: {ex.Message}"); }
    }

    public static PropertyMappingConfig LoadOrDefault()
    {
        var cfg = CreateDefault();
        try
        {
            var path = SavePath();
            if (!File.Exists(path)) return cfg;
            var dto = JsonSerializer.Deserialize<ConfigDto>(File.ReadAllText(path));
            if (dto == null || dto.Rules == null || dto.Rules.Count == 0) return cfg;
            cfg.GroupBy = Enum.IsDefined(typeof(GroupMode), dto.GroupBy) ? (GroupMode)dto.GroupBy : GroupMode.FileType;
            cfg.AccentOverrideEnabled = dto.AccentOverrideEnabled;
            if (!string.IsNullOrEmpty(dto.AccentOverride)) cfg.AccentOverride = new Color(dto.AccentOverride);
            var rules = new Godot.Collections.Array<MappingRule>();
            foreach (var d in dto.Rules)
            {
                if (!Enum.IsDefined(typeof(VisualProperty), d.Target) || !Enum.IsDefined(typeof(SourceProperty), d.Source)) continue;
                rules.Add(new MappingRule
                {
                    Enabled = d.Enabled, Target = (VisualProperty)d.Target, Source = (SourceProperty)d.Source,
                    Transform = d.Transform == 1 ? TransformKind.Log : TransformKind.Linear,
                    InMin = d.InMin, InMax = d.InMax, AutoRange = d.AutoRange, OutMin = d.OutMin, OutMax = d.OutMax,
                    Invert = d.Invert,
                    ColorLow = string.IsNullOrEmpty(d.ColorLow) ? new Color("24407F") : new Color(d.ColorLow),
                    ColorHigh = string.IsNullOrEmpty(d.ColorHigh) ? new Color("BFEFFF") : new Color(d.ColorHigh),
                    CategoryFilter = d.CategoryFilter ?? "",
                });
            }
            if (rules.Count > 0) cfg.Rules = rules;
        }
        catch (Exception ex) { GD.PushWarning($"[ATLAS] mapping file unreadable, using defaults: {ex.Message}"); }
        return cfg;
    }

    // ------------------------------------------------------------------ labels for the UI

    public static string Label(SourceProperty s) => s switch
    {
        SourceProperty.None => "None",
        SourceProperty.FileSize => "File Size",
        SourceProperty.RecursiveSize => "Folder Size",
        SourceProperty.LastModified => "Last Modified",
        SourceProperty.LastAccessed => "Last Accessed",
        SourceProperty.Created => "Created",
        SourceProperty.UsageFrequency => "Usage Frequency",
        SourceProperty.Recency => "Recent Use",
        SourceProperty.FileType => "File Type",
        SourceProperty.NameLength => "Name Length",
        SourceProperty.Hierarchy => "Hierarchy",
        SourceProperty.IsArchive => "Is Archive",
        _ => s.ToString(),
    };

    public static string Label(GroupMode g) => g switch
    {
        GroupMode.FileType => "File Type",
        GroupMode.Extension => "Extension",
        GroupMode.LastModified => "Last Modified",
        GroupMode.SizeBucket => "File Size",
        _ => "None",
    };

    public static string Label(VisualProperty v) => v switch
    {
        VisualProperty.Scale => "Scale",
        VisualProperty.Height => "Height",
        VisualProperty.BaseColor => "Base Colour",
        VisualProperty.AccentColor => "Accent Colour",
        VisualProperty.GlowIntensity => "Glow",
        VisualProperty.Opacity => "Opacity",
        VisualProperty.RingDistance => "Ring Distance",
        _ => v.ToString(),
    };
}
