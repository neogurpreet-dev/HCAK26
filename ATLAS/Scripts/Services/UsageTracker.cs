using System;
using System.IO;
using Godot;

namespace Atlas;

/// <summary>
/// Facade used by the world generator and MainWorld. All data now lives in <see cref="ActivityStore"/> (local JSON,
/// files + folders + applications, with Usage Frequency and the relationship model). The old public surface
/// (Instance, RecordOpen, Annotate, Flush) is unchanged.
///
/// Per-entry values written by <see cref="Annotate"/>:
///   UsageU      Usage Frequency  U(x) = sum over events exp(-age_days / tau_u), tau_u = 14 days by default (a recency-weighted
///               event count; NOT a percentage). Normalised for visuals by the Property Mapping rule (log + auto range).
///   UsageScore  relevance used to rank / choose hub buildings (unchanged formula, now fed from the store):
///                 FrequencyWeight * log2(1 + openCount) + RecencyWeight * 2^(-daysSinceLastOpen / RecencyHalfLifeDays)
///                 + FilesystemWeight * 2^(-daysSinceModified / FilesystemHalfLifeDays)
/// </summary>
public sealed class UsageTracker
{
    static UsageTracker _instance;
    public static UsageTracker Instance => _instance ??= new UsageTracker();

    public double FrequencyWeight = 1.0;
    public double RecencyWeight = 2.0;
    public double RecencyHalfLifeDays = 7.0;
    public double FilesystemWeight = 1.0;
    public double FilesystemHalfLifeDays = 30.0;

    public ActivityStore Store { get; }

    UsageTracker()
    {
        string dir;
        try { dir = ProjectSettings.GlobalizePath("user://"); }
        catch { dir = Path.Combine(System.Environment.GetFolderPath(System.Environment.SpecialFolder.ApplicationData), "ATLAS"); }
        Store = new ActivityStore(dir);
    }

    // ------------------------------------------------------------------ recording

    /// <summary>Called after ATLAS successfully navigated into a folder or opened a file.</summary>
    public void RecordOpen(string path, bool isDirectory)
    {
        if (string.IsNullOrWhiteSpace(path)) return;
        if (isDirectory)
            Store.RecordEvent(ActivityIds.Folder(path), EntityType.Folder, ActivityKind.FolderOpen, ActivityOrigin.Atlas, path, FolderName(path));
        else
            RecordFileLaunch(path, ActivityOrigin.Atlas);
    }

    /// <summary>
    /// Records a successful file launch and, when Windows tells us which program handles that document, the application
    /// launch too (this is how documents get associated with applications without observing other windows).
    /// </summary>
    public bool RecordFileLaunch(string path, ActivityOrigin origin)
    {
        if (string.IsNullOrWhiteSpace(path)) return false;
        var when = DateTime.UtcNow;
        bool ok = Store.RecordEvent(ActivityIds.File(path), EntityType.File, ActivityKind.FileOpen, origin, path, Path.GetFileName(path), when);

        string exe = WinShell.GetDefaultHandler(path);
        if (exe != null)
        {
            string appId = ActivityIds.App(exe);
            Store.RecordEvent(appId, EntityType.Application, ActivityKind.AppLaunch, origin, exe, WinShell.DisplayName(exe), when);
            // the handler is about to come to the foreground: do not double count that as an independent activation
            Store.SuppressActivation(appId, origin == ActivityOrigin.QuickLaunch ? 12 : 8);
        }
        return ok;
    }

    /// <summary>Records a successful application launch performed by ATLAS (Open All).</summary>
    public bool RecordAppLaunch(string exePath, ActivityOrigin origin)
    {
        if (string.IsNullOrWhiteSpace(exePath)) return false;
        string appId = ActivityIds.App(exePath);
        Store.SuppressActivation(appId, 12);
        return Store.RecordEvent(appId, EntityType.Application, ActivityKind.AppLaunch, origin, exePath, WinShell.DisplayName(exePath));
    }

    static string FolderName(string path)
    {
        var n = Path.GetFileName(path.TrimEnd('\\', '/'));
        return string.IsNullOrEmpty(n) ? path : n;
    }

    // ------------------------------------------------------------------ queries

    static string IdOf(string path, bool isDirectory) => isDirectory ? ActivityIds.Folder(path) : ActivityIds.File(path);

    public int GetCount(string path) => Store.GetCount(ActivityIds.File(path)) + Store.GetCount(ActivityIds.Folder(path));

    /// <summary>Fills UsageCount, RecencyDays, UsageU and UsageScore on the entry.</summary>
    public void Annotate(FsEntry e)
    {
        string id = IdOf(e.Path, e.IsDirectory);
        var now = DateTime.UtcNow;
        e.UsageCount = Store.GetCount(id);
        var last = Store.GetLastUsedUtc(id);
        e.RecencyDays = last.HasValue ? Math.Max(0, (now - last.Value).TotalDays) : 9999;
        e.UsageU = Store.GetUsageFrequency(id, now);

        double s = 0;
        if (e.UsageCount > 0)
        {
            s += FrequencyWeight * Math.Log2(1 + e.UsageCount);
            s += RecencyWeight * Math.Pow(2, -e.RecencyDays / RecencyHalfLifeDays);
        }
        if (e.HasModified)
        {
            double age = Math.Max(0, (DateTime.Now - e.Modified).TotalDays);
            s += FilesystemWeight * Math.Pow(2, -age / FilesystemHalfLifeDays);
        }
        e.UsageScore = double.IsFinite(s) ? s : 0;
    }

    public void Flush() => Store.Flush();
}
