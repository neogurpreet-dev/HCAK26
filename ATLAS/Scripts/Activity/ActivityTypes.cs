using System;
using System.IO;

namespace Atlas;

// ---------------------------------------------------------------------------------------------------------------------
// Activity data model. Pure .NET (no Godot types) so the scoring model can be unit-tested outside the engine.
// ---------------------------------------------------------------------------------------------------------------------

public enum EntityType { File = 0, Folder = 1, Application = 2 }

/// <summary>
/// What was observed. File/folder/app-launch events are produced by ATLAS itself (a launch ATLAS performed successfully).
/// AppActivation is produced by the optional foreground observer and means "this program became the foreground program
/// and stayed there for a few seconds" - it says nothing about which document was open inside it.
/// </summary>
public enum ActivityKind { FileOpen = 0, FolderOpen = 1, AppLaunch = 2, AppActivation = 3 }

/// <summary>
/// Where an event came from. QuickLaunch events (Open All) count towards Usage Frequency but are ignored by the
/// relationship model, so pressing Open All can never strengthen the very links it was launched from.
/// </summary>
public enum ActivityOrigin { Atlas = 0, External = 1, QuickLaunch = 2 }

public enum GhostSensitivity { Strict = 0, Balanced = 1, Exploratory = 2 }

public sealed class ActivityEvent
{
    /// <summary>Stable entity identifier, see <see cref="ActivityIds"/>.</summary>
    public string Id = "";
    public EntityType Type;
    public ActivityKind Kind;
    public ActivityOrigin Origin;
    /// <summary>UTC ticks.</summary>
    public long Ticks;
    /// <summary>Identifies the ATLAS run that recorded the event (optional session identifier).</summary>
    public long RunId;
    /// <summary>Only successful launches/activations are ever stored; failures are never recorded.</summary>
    public bool Success = true;
}

/// <summary>Descriptive data kept per entity so related items can be shown and launched later.</summary>
public sealed class EntityInfo
{
    public string Id = "";
    public EntityType Type;
    /// <summary>Canonical absolute path (file/folder) or executable path (application).</summary>
    public string Path = "";
    public string Name = "";
}

/// <summary>Stable identifiers: type prefix + canonical lower-case path. Display names are never used for identity.</summary>
public static class ActivityIds
{
    public static string File(string path) => "file:" + Canon(path);
    public static string Folder(string path) => "dir:" + Canon(path);
    public static string App(string exePath) => "app:" + Canon(exePath);

    public static string Canon(string path)
    {
        if (string.IsNullOrWhiteSpace(path)) return "";
        try { path = System.IO.Path.GetFullPath(path); } catch { }
        if (path.Length > 3) path = path.TrimEnd('\\', '/');
        return path.ToLowerInvariant();
    }

    public static EntityType TypeOf(string id)
    {
        if (id.StartsWith("app:", StringComparison.Ordinal)) return EntityType.Application;
        if (id.StartsWith("dir:", StringComparison.Ordinal)) return EntityType.Folder;
        return EntityType.File;
    }
}

/// <summary>
/// Every tunable of Usage Frequency and of the relationship model, with the documented defaults.
/// Serialised to atlas_activity_settings.json. Only properties (no computed ones) so System.Text.Json round-trips it.
/// </summary>
public sealed class ActivitySettings
{
    public int Version { get; set; } = 1;

    // ---- privacy / tracking controls
    /// <summary>Master switch. Off = nothing new is recorded at all.</summary>
    public bool TrackActivity { get; set; } = true;
    /// <summary>Opt-in: also note which program is in the foreground (executable path only, never window titles).</summary>
    public bool ObserveExternalApps { get; set; } = false;

    // ---- Ghost Links
    public GhostSensitivity Sensitivity { get; set; } = GhostSensitivity.Balanced;
    public int MaxRelated { get; set; } = 8;

    // ---- Usage Frequency  U(x) = sum over events exp(-age_days / UsageTauDays)
    public double UsageTauDays { get; set; } = 14.0;

    // ---- relationship model
    /// <summary>A pause longer than this ends an activity session.</summary>
    public double SessionGapMinutes { get; set; } = 5.0;
    /// <summary>Two entities only count as used together if their closest events are at most this far apart.</summary>
    public double CoUsageWindowMinutes { get; set; } = 5.0;
    /// <summary>lambda: w = exp(-gap_seconds / lambda). Gaps of seconds weigh far more than gaps of minutes.</summary>
    public double GapLambdaSeconds { get; set; } = 120.0;
    /// <summary>tau: recency decay of a session, exp(-session_age_days / tau).</summary>
    public double RecencyTauDays { get; set; } = 30.0;
    /// <summary>Sessions touching more than this many distinct entities are down-weighted by SoftCap / n.</summary>
    public int SessionSizeSoftCap { get; set; } = 10;
    /// <summary>Pairs are only formed among the most recent entities of a session beyond this size.</summary>
    public int SessionEntityHardCap { get; set; } = 40;
    /// <summary>Additive smoothing k in the normalisation sqrt((N(A)+k)(N(B)+k)).</summary>
    public double SmoothingK { get; set; } = 1.0;

    // ---- foreground observer
    public double ForegroundDwellSeconds { get; set; } = 3.0;

    // ---- eligibility thresholds per sensitivity (score = normalised association, NOT a percentage)
    public double StrictMinScore { get; set; } = 0.45;
    public double BalancedMinScore { get; set; } = 0.25;
    public double ExploratoryMinScore { get; set; } = 0.15;
    public int StrictMinSessions { get; set; } = 4;
    public int BalancedMinSessions { get; set; } = 3;
    public int ExploratoryMinSessions { get; set; } = 2;

    /// <summary>Absolute floor: one co-occurrence can never create a link, whatever the sensitivity.</summary>
    public const int HardMinSessions = 2;

    public double MinScoreFor(GhostSensitivity s)
    {
        switch (s)
        {
            case GhostSensitivity.Strict: return StrictMinScore;
            case GhostSensitivity.Exploratory: return ExploratoryMinScore;
            default: return BalancedMinScore;
        }
    }

    public int MinSessionsFor(GhostSensitivity s)
    {
        int n;
        switch (s)
        {
            case GhostSensitivity.Strict: n = StrictMinSessions; break;
            case GhostSensitivity.Exploratory: n = ExploratoryMinSessions; break;
            default: n = BalancedMinSessions; break;
        }
        return Math.Max(HardMinSessions, n);
    }

    public ActivitySettings Clone() => (ActivitySettings)MemberwiseClone();
}
