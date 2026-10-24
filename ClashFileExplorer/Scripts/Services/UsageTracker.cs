using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text.Json;
using Godot;

namespace ClashExplorer.Services;

// ------------------------------------------------------------------ persisted data model

/// <summary>Interaction statistics for one file or folder path.</summary>
public sealed class PathUsage
{
    public bool IsFolder { get; set; }
    /// <summary>Explicit opens: file opened, or folder double-clicked.</summary>
    public int OpenCount { get; set; }
    /// <summary>Directory loads that were not an explicit open (back, up, typed path, startup).</summary>
    public int VisitCount { get; set; }
    public DateTime FirstUtc { get; set; }
    public DateTime LastUtc { get; set; }
}

/// <summary>Two paths that were used within the co-access window of each other.</summary>
public sealed class PairUsage
{
    public string A { get; set; } = "";
    public string B { get; set; } = "";
    public int Count { get; set; }
    /// <summary>Number of distinct app sessions in which the pair was observed.</summary>
    public int Sessions { get; set; }
    public int LastSession { get; set; }
    public DateTime FirstUtc { get; set; }
    public DateTime LastUtc { get; set; }
}

/// <summary>Directed navigation from one directory to another.</summary>
public sealed class TransitionUsage
{
    public string From { get; set; } = "";
    public string To { get; set; } = "";
    public int Count { get; set; }
    public DateTime LastUtc { get; set; }
}

public sealed class UsageData
{
    public int Version { get; set; } = UsageTracker.CurrentVersion;
    public int SessionCounter { get; set; }
    public Dictionary<string, PathUsage> Paths { get; set; } = new();
    public Dictionary<string, PairUsage> Pairs { get; set; } = new();
    public Dictionary<string, TransitionUsage> Transitions { get; set; } = new();
    public List<string> RecentDirectories { get; set; } = new();
}

// ------------------------------------------------------------------ query results

public readonly record struct UsageSnapshot(int Count, int OpenCount, int VisitCount, DateTime? LastUtc);

/// <summary>A path that is statistically related to a path (or directory) being viewed.</summary>
public sealed class RelatedPath
{
    /// <summary>The related item, usually located outside the current directory.</summary>
    public string Path { get; init; } = "";
    /// <summary>The item in the current view that it is related to.</summary>
    public string AnchorPath { get; init; } = "";
    public double Score { get; init; }
    public int Count { get; init; }
    public int Sessions { get; init; }
    public DateTime LastUtc { get; init; }
}

// ------------------------------------------------------------------ tracker

/// <summary>
/// Lightweight local usage history stored as JSON in user:// (local app data, never uploaded).
/// Thread-safe: every public method can be called from any thread. Writes are debounced, atomic
/// (temp file, then replace, with a .bak) and survive missing, outdated or corrupt files.
/// Call Dispose() on shutdown to flush.
///
/// Where to call it:
///   RecordFileOpened   - user opens a file
///   RecordFolderOpened - user double-clicks a folder (explicit open)
///   RecordDirectoryVisit - a directory is loaded some other way (back, up, typed path, startup)
///   RecordNavigation   - after every directory change, with the previous and new directory
/// </summary>
public sealed class UsageTracker : IDisposable
{
    public const int CurrentVersion = 1;
    private const int SaveDelayMs = 3000;
    private const int MaxRecentDirectories = 30;

    // ---- configurable behaviour
    public TimeSpan CoAccessWindow { get; set; } = TimeSpan.FromMinutes(10);
    /// <summary>Minimum co-access count before a relationship is reported.</summary>
    public int MinObservations { get; set; } = 3;
    /// <summary>Minimum number of distinct sessions before a relationship is reported.</summary>
    public int MinSessions { get; set; } = 2;
    public double RelationHalfLifeDays { get; set; } = 45.0;
    public int RetentionDays { get; set; } = 365;
    public int MaxPaths { get; set; } = 5000;
    public int MaxPairs { get; set; } = 5000;
    public int MaxTransitions { get; set; } = 2000;

    private static readonly JsonSerializerOptions JsonOptions = new()
    {
        WriteIndented = false,
        PropertyNameCaseInsensitive = true
    };

    private readonly object _lock = new();
    private readonly object _ioLock = new();
    private readonly string _filePath;
    private readonly Action<string> _log;
    private readonly List<(string Item, DateTime Utc)> _recent = new();
    private readonly System.Threading.Timer _saveTimer;
    private UsageData _data;
    private readonly int _session;
    private bool _dirty;
    private bool _disposed;

    public string FilePath => _filePath;

    /// <param name="filePath">Defaults to user://usage_history.json (resolved to a real path).</param>
    /// <param name="log">Defaults to GD.PushWarning.</param>
    public UsageTracker(string? filePath = null, Action<string>? log = null)
    {
        _log = log ?? (msg => GD.PushWarning("[UsageTracker] " + msg));
        _filePath = filePath ?? ProjectSettings.GlobalizePath("user://usage_history.json");
        _data = LoadFromDisk();
        _data.SessionCounter++;
        _session = _data.SessionCounter;
        _saveTimer = new System.Threading.Timer(_ => SaveNow(), null,
            System.Threading.Timeout.Infinite, System.Threading.Timeout.Infinite);
        MarkDirty();
    }

    // ================================================================ recording

    public void RecordFileOpened(string path) => RecordOpen(path, isFolder: false);

    public void RecordFolderOpened(string path) => RecordOpen(path, isFolder: true);

    public void RecordDirectoryVisit(string path)
    {
        string p = NormalizePath(path);
        if (p.Length == 0) return;
        DateTime now = DateTime.UtcNow;
        lock (_lock)
        {
            if (_disposed) return;
            PathUsage usage = GetOrAddPath(p, true, now);
            usage.VisitCount++;
            usage.LastUtc = now;
            PushRecentDirectory(p);
            MarkDirty();
        }
    }

    public void RecordNavigation(string fromDirectory, string toDirectory)
    {
        string from = NormalizePath(fromDirectory);
        string to = NormalizePath(toDirectory);
        if (from.Length == 0 || to.Length == 0 || string.Equals(from, to, StringComparison.OrdinalIgnoreCase)) return;
        DateTime now = DateTime.UtcNow;
        string key = from + ">" + to;
        lock (_lock)
        {
            if (_disposed) return;
            if (!_data.Transitions.TryGetValue(key, out TransitionUsage? t))
            {
                t = new TransitionUsage { From = from, To = to };
                _data.Transitions[key] = t;
            }
            t.Count++;
            t.LastUtc = now;
            MarkDirty();
        }
    }

    private void RecordOpen(string path, bool isFolder)
    {
        string p = NormalizePath(path);
        if (p.Length == 0) return;
        DateTime now = DateTime.UtcNow;
        lock (_lock)
        {
            if (_disposed) return;
            PathUsage usage = GetOrAddPath(p, isFolder, now);
            usage.OpenCount++;
            usage.LastUtc = now;
            if (isFolder) PushRecentDirectory(p);
            RecordCoAccessLocked(p, now);
            MarkDirty();
        }
    }

    private PathUsage GetOrAddPath(string p, bool isFolder, DateTime now)
    {
        if (!_data.Paths.TryGetValue(p, out PathUsage? usage))
        {
            usage = new PathUsage { IsFolder = isFolder, FirstUtc = now, LastUtc = now };
            _data.Paths[p] = usage;
        }
        return usage;
    }

    private void PushRecentDirectory(string p)
    {
        _data.RecentDirectories.RemoveAll(x => string.Equals(x, p, StringComparison.OrdinalIgnoreCase));
        _data.RecentDirectories.Insert(0, p);
        if (_data.RecentDirectories.Count > MaxRecentDirectories)
        {
            _data.RecentDirectories.RemoveRange(MaxRecentDirectories, _data.RecentDirectories.Count - MaxRecentDirectories);
        }
    }

    private void RecordCoAccessLocked(string p, DateTime now)
    {
        _recent.RemoveAll(e => now - e.Utc > CoAccessWindow);
        var seen = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        foreach ((string item, DateTime _) in _recent)
        {
            if (string.Equals(item, p, StringComparison.OrdinalIgnoreCase)) continue;
            if (!seen.Add(item)) continue;
            BumpPairLocked(p, item, now);
        }
        _recent.Add((p, now));
        if (_recent.Count > 64) _recent.RemoveRange(0, _recent.Count - 64);
    }

    private void BumpPairLocked(string x, string y, DateTime now)
    {
        string a = x, b = y;
        if (string.Compare(a, b, StringComparison.OrdinalIgnoreCase) > 0) (a, b) = (b, a);
        string key = a + "|" + b;
        if (!_data.Pairs.TryGetValue(key, out PairUsage? pair))
        {
            pair = new PairUsage { A = a, B = b, FirstUtc = now };
            _data.Pairs[key] = pair;
        }
        pair.Count++;
        if (pair.LastSession != _session)
        {
            pair.Sessions++;
            pair.LastSession = _session;
        }
        pair.LastUtc = now;
    }

    // ================================================================ queries

    public UsageSnapshot GetSnapshot(string path)
    {
        string p = NormalizePath(path);
        lock (_lock)
        {
            return SnapshotLocked(p);
        }
    }

    /// <summary>Bulk lookup under a single lock; used by the WorldGenerator for a whole directory.</summary>
    public Dictionary<string, UsageSnapshot> GetSnapshots(IEnumerable<string> paths)
    {
        var result = new Dictionary<string, UsageSnapshot>(StringComparer.OrdinalIgnoreCase);
        lock (_lock)
        {
            foreach (string path in paths)
            {
                string p = NormalizePath(path);
                if (p.Length == 0) continue;
                result[path] = SnapshotLocked(p);
            }
        }
        return result;
    }

    private UsageSnapshot SnapshotLocked(string p)
    {
        if (p.Length == 0 || !_data.Paths.TryGetValue(p, out PathUsage? u))
        {
            return new UsageSnapshot(0, 0, 0, null);
        }
        return new UsageSnapshot(u.OpenCount + u.VisitCount, u.OpenCount, u.VisitCount, u.LastUtc);
    }

    public IReadOnlyList<string> GetRecentDirectories(int max = 10)
    {
        lock (_lock)
        {
            return _data.RecentDirectories.Take(Math.Max(0, max)).ToList();
        }
    }

    /// <summary>
    /// Items related to one path, strongest first. A relationship is reported only when it has
    /// at least MinObservations co-accesses across at least MinSessions sessions.
    /// </summary>
    public List<RelatedPath> GetRelated(string path, int max = 8)
    {
        string p = NormalizePath(path);
        var result = new List<RelatedPath>();
        if (p.Length == 0) return result;
        DateTime now = DateTime.UtcNow;
        lock (_lock)
        {
            foreach (PairUsage pair in _data.Pairs.Values)
            {
                bool isA = string.Equals(pair.A, p, StringComparison.OrdinalIgnoreCase);
                bool isB = string.Equals(pair.B, p, StringComparison.OrdinalIgnoreCase);
                if (!isA && !isB) continue;
                if (!PassesThreshold(pair)) continue;
                result.Add(new RelatedPath
                {
                    Path = isA ? pair.B : pair.A,
                    AnchorPath = p,
                    Score = ScorePair(pair, now),
                    Count = pair.Count,
                    Sessions = pair.Sessions,
                    LastUtc = pair.LastUtc
                });
            }
        }
        return result.OrderByDescending(r => r.Score).Take(Math.Max(0, max)).ToList();
    }

    /// <summary>
    /// Items outside a directory that are related to items inside it (candidates for ghost files).
    /// One result per outside item, keeping its strongest link.
    /// </summary>
    public List<RelatedPath> GetRelatedForDirectory(string directory, int max = 8)
    {
        string dir = NormalizePath(directory);
        var best = new Dictionary<string, RelatedPath>(StringComparer.OrdinalIgnoreCase);
        if (dir.Length == 0) return best.Values.ToList();
        DateTime now = DateTime.UtcNow;
        lock (_lock)
        {
            foreach (PairUsage pair in _data.Pairs.Values)
            {
                if (!PassesThreshold(pair)) continue;
                bool aInside = IsDirectChild(pair.A, dir);
                bool bInside = IsDirectChild(pair.B, dir);
                if (aInside == bInside) continue; // both inside or both outside: not a ghost
                string anchor = aInside ? pair.A : pair.B;
                string other = aInside ? pair.B : pair.A;
                double score = ScorePair(pair, now);
                if (best.TryGetValue(other, out RelatedPath? existing) && existing.Score >= score) continue;
                best[other] = new RelatedPath
                {
                    Path = other,
                    AnchorPath = anchor,
                    Score = score,
                    Count = pair.Count,
                    Sessions = pair.Sessions,
                    LastUtc = pair.LastUtc
                };
            }
        }
        return best.Values.OrderByDescending(r => r.Score).Take(Math.Max(0, max)).ToList();
    }

    /// <summary>Directories most often navigated to from the given directory.</summary>
    public List<(string Directory, int Count)> GetTransitionsFrom(string directory, int max = 5)
    {
        string dir = NormalizePath(directory);
        lock (_lock)
        {
            return _data.Transitions.Values
                .Where(t => string.Equals(t.From, dir, StringComparison.OrdinalIgnoreCase))
                .OrderByDescending(t => t.Count)
                .Take(Math.Max(0, max))
                .Select(t => (t.To, t.Count))
                .ToList();
        }
    }

    private bool PassesThreshold(PairUsage pair) =>
        pair.Count >= MinObservations && pair.Sessions >= MinSessions;

    /// <summary>
    /// Transparent relation score:
    ///   ln(1 + count) * (0.5 + 0.5 * min(sessions, 5) / 5) * 0.5 ^ (daysSinceLast / halfLife)
    /// </summary>
    private double ScorePair(PairUsage pair, DateTime now)
    {
        double strength = Math.Log(1.0 + pair.Count);
        double spread = 0.5 + 0.5 * Math.Min(pair.Sessions, 5) / 5.0;
        double days = Math.Max(0.0, (now - pair.LastUtc).TotalDays);
        double decay = Math.Pow(0.5, days / Math.Max(1.0, RelationHalfLifeDays));
        double score = strength * spread * decay;
        return double.IsFinite(score) ? score : 0.0;
    }

    private static bool IsDirectChild(string path, string directory)
    {
        string? parent = System.IO.Path.GetDirectoryName(path);
        return parent != null && string.Equals(parent, directory, StringComparison.OrdinalIgnoreCase);
    }

    // ================================================================ maintenance

    /// <summary>Moves history from an old path to a new one (file or folder rename/move).</summary>
    public void NotifyRenamed(string oldPath, string newPath)
    {
        string o = NormalizePath(oldPath);
        string n = NormalizePath(newPath);
        if (o.Length == 0 || n.Length == 0 || string.Equals(o, n, StringComparison.OrdinalIgnoreCase)) return;

        lock (_lock)
        {
            if (_disposed) return;

            var paths = new Dictionary<string, PathUsage>(StringComparer.OrdinalIgnoreCase);
            foreach (KeyValuePair<string, PathUsage> kv in _data.Paths)
            {
                string key = Remap(kv.Key, o, n);
                if (paths.TryGetValue(key, out PathUsage? existing))
                {
                    existing.OpenCount += kv.Value.OpenCount;
                    existing.VisitCount += kv.Value.VisitCount;
                    if (kv.Value.LastUtc > existing.LastUtc) existing.LastUtc = kv.Value.LastUtc;
                    if (kv.Value.FirstUtc < existing.FirstUtc) existing.FirstUtc = kv.Value.FirstUtc;
                }
                else
                {
                    paths[key] = kv.Value;
                }
            }
            _data.Paths = paths;

            var pairs = new Dictionary<string, PairUsage>(StringComparer.OrdinalIgnoreCase);
            foreach (PairUsage pair in _data.Pairs.Values)
            {
                string a = Remap(pair.A, o, n);
                string b = Remap(pair.B, o, n);
                if (string.Equals(a, b, StringComparison.OrdinalIgnoreCase)) continue;
                if (string.Compare(a, b, StringComparison.OrdinalIgnoreCase) > 0) (a, b) = (b, a);
                pair.A = a;
                pair.B = b;
                pairs[a + "|" + b] = pair;
            }
            _data.Pairs = pairs;

            var transitions = new Dictionary<string, TransitionUsage>(StringComparer.OrdinalIgnoreCase);
            foreach (TransitionUsage t in _data.Transitions.Values)
            {
                t.From = Remap(t.From, o, n);
                t.To = Remap(t.To, o, n);
                if (string.Equals(t.From, t.To, StringComparison.OrdinalIgnoreCase)) continue;
                transitions[t.From + ">" + t.To] = t;
            }
            _data.Transitions = transitions;

            for (int i = 0; i < _data.RecentDirectories.Count; i++)
            {
                _data.RecentDirectories[i] = Remap(_data.RecentDirectories[i], o, n);
            }
            _recent.Clear();
            MarkDirty();
        }
    }

    /// <summary>Erases all history (privacy reset) and saves the empty file.</summary>
    public void ClearAll()
    {
        lock (_lock)
        {
            _data = NewData();
            _data.SessionCounter = _session;
            _recent.Clear();
            MarkDirty();
        }
        SaveNow();
    }

    /// <summary>Writes pending changes immediately.</summary>
    public void Flush() => SaveNow();

    public void Dispose()
    {
        lock (_lock)
        {
            if (_disposed) return;
        }
        SaveNow();
        lock (_lock)
        {
            _disposed = true;
        }
        _saveTimer.Dispose();
    }

    private static string Remap(string path, string oldPrefix, string newPrefix)
    {
        if (string.Equals(path, oldPrefix, StringComparison.OrdinalIgnoreCase)) return newPrefix;
        string withSep = oldPrefix.EndsWith('\\') ? oldPrefix : oldPrefix + "\\";
        if (path.StartsWith(withSep, StringComparison.OrdinalIgnoreCase))
        {
            string nb = newPrefix.EndsWith('\\') ? newPrefix : newPrefix + "\\";
            return nb + path.Substring(withSep.Length);
        }
        return path;
    }

    /// <summary>Absolute path without trailing separator (except roots). Empty string when invalid.</summary>
    public static string NormalizePath(string? path)
    {
        if (string.IsNullOrWhiteSpace(path)) return "";
        try
        {
            string full = System.IO.Path.GetFullPath(path.Trim());
            string root = System.IO.Path.GetPathRoot(full) ?? "";
            if (full.Length > root.Length) full = full.TrimEnd('\\', '/');
            return full;
        }
        catch (Exception)
        {
            return "";
        }
    }

    // ================================================================ persistence

    private static UsageData NewData() => new();

    private void MarkDirty()
    {
        _dirty = true;
        if (!_disposed) _saveTimer?.Change(SaveDelayMs, System.Threading.Timeout.Infinite);
    }

    private UsageData LoadFromDisk()
    {
        foreach (string candidate in new[] { _filePath, _filePath + ".bak" })
        {
            try
            {
                if (!File.Exists(candidate)) continue;
                string json = File.ReadAllText(candidate);
                UsageData? loaded = JsonSerializer.Deserialize<UsageData>(json, JsonOptions);
                if (loaded == null) throw new InvalidDataException("Empty usage file.");
                return Sanitize(loaded);
            }
            catch (Exception ex)
            {
                _log($"Could not read {candidate}: {ex.Message}");
                try
                {
                    File.Move(candidate, candidate + ".corrupt-" + DateTime.UtcNow.ToString("yyyyMMddHHmmss"));
                }
                catch (Exception)
                {
                    // Ignore: the corrupt file will simply be overwritten on the next save.
                }
            }
        }
        return NewData();
    }

    /// <summary>
    /// Rebuilds collections with case-insensitive keys and drops invalid entries. Missing
    /// fields from older files keep their defaults, so older versions load unchanged.
    /// </summary>
    private static UsageData Sanitize(UsageData raw)
    {
        var data = new UsageData
        {
            Version = CurrentVersion,
            SessionCounter = Math.Max(0, raw.SessionCounter)
        };

        if (raw.Paths != null)
        {
            foreach (KeyValuePair<string, PathUsage> kv in raw.Paths)
            {
                if (string.IsNullOrEmpty(kv.Key) || kv.Value == null) continue;
                kv.Value.OpenCount = Math.Max(0, kv.Value.OpenCount);
                kv.Value.VisitCount = Math.Max(0, kv.Value.VisitCount);
                data.Paths[kv.Key] = kv.Value;
            }
        }
        data.Paths = new Dictionary<string, PathUsage>(data.Paths, StringComparer.OrdinalIgnoreCase);

        var pairs = new Dictionary<string, PairUsage>(StringComparer.OrdinalIgnoreCase);
        if (raw.Pairs != null)
        {
            foreach (PairUsage pair in raw.Pairs.Values)
            {
                if (pair == null || string.IsNullOrEmpty(pair.A) || string.IsNullOrEmpty(pair.B)) continue;
                pair.Count = Math.Max(0, pair.Count);
                pair.Sessions = Math.Max(0, pair.Sessions);
                pairs[pair.A + "|" + pair.B] = pair;
            }
        }
        data.Pairs = pairs;

        var transitions = new Dictionary<string, TransitionUsage>(StringComparer.OrdinalIgnoreCase);
        if (raw.Transitions != null)
        {
            foreach (TransitionUsage t in raw.Transitions.Values)
            {
                if (t == null || string.IsNullOrEmpty(t.From) || string.IsNullOrEmpty(t.To)) continue;
                t.Count = Math.Max(0, t.Count);
                transitions[t.From + ">" + t.To] = t;
            }
        }
        data.Transitions = transitions;

        if (raw.RecentDirectories != null)
        {
            data.RecentDirectories = raw.RecentDirectories.Where(s => !string.IsNullOrEmpty(s))
                .Take(MaxRecentDirectories).ToList();
        }
        return data;
    }

    private void PruneLocked()
    {
        DateTime now = DateTime.UtcNow;
        DateTime cutoff = now.AddDays(-Math.Max(1, RetentionDays));

        foreach (string key in _data.Paths.Where(kv => kv.Value.LastUtc < cutoff).Select(kv => kv.Key).ToList())
            _data.Paths.Remove(key);
        foreach (string key in _data.Pairs.Where(kv => kv.Value.LastUtc < cutoff).Select(kv => kv.Key).ToList())
            _data.Pairs.Remove(key);
        foreach (string key in _data.Transitions.Where(kv => kv.Value.LastUtc < cutoff).Select(kv => kv.Key).ToList())
            _data.Transitions.Remove(key);

        if (_data.Paths.Count > MaxPaths)
        {
            foreach (string key in _data.Paths
                         .OrderBy(kv => kv.Value.OpenCount + kv.Value.VisitCount)
                         .ThenBy(kv => kv.Value.LastUtc)
                         .Take(_data.Paths.Count - MaxPaths)
                         .Select(kv => kv.Key).ToList())
                _data.Paths.Remove(key);
        }
        if (_data.Pairs.Count > MaxPairs)
        {
            foreach (string key in _data.Pairs
                         .OrderBy(kv => kv.Value.Count)
                         .ThenBy(kv => kv.Value.LastUtc)
                         .Take(_data.Pairs.Count - MaxPairs)
                         .Select(kv => kv.Key).ToList())
                _data.Pairs.Remove(key);
        }
        if (_data.Transitions.Count > MaxTransitions)
        {
            foreach (string key in _data.Transitions
                         .OrderBy(kv => kv.Value.Count)
                         .ThenBy(kv => kv.Value.LastUtc)
                         .Take(_data.Transitions.Count - MaxTransitions)
                         .Select(kv => kv.Key).ToList())
                _data.Transitions.Remove(key);
        }
    }

    private void SaveNow()
    {
        string json;
        lock (_lock)
        {
            if (!_dirty) return;
            PruneLocked();
            _data.Version = CurrentVersion;
            json = JsonSerializer.Serialize(_data, JsonOptions);
            _dirty = false;
        }

        lock (_ioLock)
        {
            try
            {
                string? dir = System.IO.Path.GetDirectoryName(_filePath);
                if (!string.IsNullOrEmpty(dir)) Directory.CreateDirectory(dir);

                string tmp = _filePath + ".tmp";
                using (var stream = new FileStream(tmp, FileMode.Create, System.IO.FileAccess.Write, FileShare.None))
                using (var writer = new StreamWriter(stream, new System.Text.UTF8Encoding(false)))
                {
                    writer.Write(json);
                    writer.Flush();
                    stream.Flush(true);
                }

                if (File.Exists(_filePath)) File.Replace(tmp, _filePath, _filePath + ".bak", true);
                else File.Move(tmp, _filePath);
            }
            catch (Exception ex)
            {
                lock (_lock)
                {
                    _dirty = true; // retry on the next change or flush
                }
                _log("Save failed: " + ex.Message);
            }
        }
    }
}
