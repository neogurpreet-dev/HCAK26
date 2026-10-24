using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;

namespace Atlas;

/// <summary>One related item as shown on the Ghost Links platform.</summary>
public sealed class GhostEntity
{
    public string Id = "";
    public EntityType Type;
    public string Path = "";
    public string Name = "";
    public double Score;
    public int Sessions;
}

public sealed class LaunchOutcome
{
    public int Opened;
    public int Skipped;
    public readonly List<string> Failed = new List<string>();
    public bool Throttled;

    public string Summary()
    {
        if (Throttled) return "Open All is already running.";
        if (Opened == 0 && Failed.Count == 0) return "Nothing to open.";
        string s = Opened == 1 ? "Opened 1 item" : "Opened " + Opened + " items";
        if (Skipped > 0) s += ", skipped " + Skipped;
        if (Failed.Count > 0) s += ". Could not open: " + string.Join(", ", Failed.Take(3)) + (Failed.Count > 3 ? " +" + (Failed.Count - 3) + " more" : "");
        return s + (Failed.Count > 0 ? "" : ".");
    }
}

/// <summary>
/// Opens the items on the active Ghost Links platform (and only those). Uses no Godot types directly.
///
/// Safety rules:
///  * Files go through ATLAS's normal file-opening path (ShellLite.OpenPath, default application) - but never when the file
///    is itself executable or a script (exe, bat, cmd, ps1, js, vbs, py, lnk, msi ...). Those must be opened one at a time.
///  * Applications are started from their executable path only: absolute path, must be a *.exe that still exists, no arguments,
///    no shell string is ever built from a path.
///  * Items that no longer exist are skipped, duplicates are collapsed, the selected file itself is excluded, and an
///    application is not started separately when it is the default handler of a document that is being opened anyway.
///  * Each launch is attempted independently; one failure never stops the others.
///  * Launches are recorded with origin QuickLaunch: they count towards Usage Frequency but cannot create or strengthen
///    relationships, so Open All can never feed back into the links it was launched from.
/// </summary>
public static class QuickLaunch
{
    static readonly HashSet<string> BlockedFileExtensions = new HashSet<string>(StringComparer.OrdinalIgnoreCase)
    {
        "exe", "com", "scr", "msi", "msp", "bat", "cmd", "ps1", "psm1", "vbs", "vbe", "js", "jse", "wsf", "wsh", "hta",
        "cpl", "jar", "lnk", "reg", "pif", "dll", "sys", "appx", "msix", "py", "pyw", "sh", "bash",
    };

    static DateTime _lastRun = DateTime.MinValue;

    public static bool IsAvailable(GhostEntity g)
    {
        try
        {
            if (g == null || string.IsNullOrWhiteSpace(g.Path) || !System.IO.Path.IsPathRooted(g.Path)) return false;
            if (g.Type == EntityType.Application)
                return g.Path.EndsWith(".exe", StringComparison.OrdinalIgnoreCase) && File.Exists(g.Path);
            if (g.Type == EntityType.File) return File.Exists(g.Path);
            return false;
        }
        catch (Exception) { return false; }
    }

    public static LaunchOutcome OpenAll(IReadOnlyList<GhostEntity> items, string selectedPath)
    {
        var outcome = new LaunchOutcome();
        var now = DateTime.UtcNow;
        if ((now - _lastRun).TotalSeconds < 1.5) { outcome.Throttled = true; return outcome; }
        _lastRun = now;
        if (items == null) return outcome;

        string selectedId = string.IsNullOrEmpty(selectedPath) ? "" : ActivityIds.File(selectedPath);
        var seen = new HashSet<string>(StringComparer.Ordinal);
        var files = new List<GhostEntity>();
        var apps = new List<GhostEntity>();

        foreach (var g in items)
        {
            if (g == null || string.IsNullOrEmpty(g.Id)) continue;
            if (!seen.Add(g.Id)) continue;                 // duplicate
            if (g.Id == selectedId) continue;              // never re-open the selected item itself
            if (!IsAvailable(g)) { outcome.Skipped++; continue; }   // deleted / unavailable / failed validation
            if (g.Type == EntityType.Application) apps.Add(g);
            else if (g.Type == EntityType.File)
            {
                string ext = (System.IO.Path.GetExtension(g.Path) ?? "").TrimStart('.');
                if (BlockedFileExtensions.Contains(ext)) { outcome.Skipped++; continue; }
                files.Add(g);
            }
        }

        // an app that is the default handler of a document we are opening is started by that document
        var handledByDocument = new HashSet<string>(StringComparer.Ordinal);
        foreach (var f in files)
        {
            string exe = WinShell.GetDefaultHandler(f.Path);
            if (exe != null) handledByDocument.Add(ActivityIds.App(exe));
        }

        var tracker = UsageTracker.Instance;
        foreach (var a in apps)
        {
            if (handledByDocument.Contains(a.Id)) { outcome.Skipped++; continue; }
            string err = ShellLite.LaunchExecutable(a.Path);
            if (err == null) { outcome.Opened++; tracker.RecordAppLaunch(a.Path, ActivityOrigin.QuickLaunch); }
            else outcome.Failed.Add(Label(a));
        }
        foreach (var f in files)
        {
            string err = ShellLite.OpenPath(f.Path);
            if (err == null) { outcome.Opened++; tracker.RecordFileLaunch(f.Path, ActivityOrigin.QuickLaunch); }
            else outcome.Failed.Add(Label(f));
        }
        return outcome;
    }

    static string Label(GhostEntity g) => string.IsNullOrEmpty(g.Name) ? System.IO.Path.GetFileName(g.Path) : g.Name;
}
