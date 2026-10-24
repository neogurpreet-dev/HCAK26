using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text.Json;
using Godot;

namespace Atlas;

public sealed class UsageRecord
{
    public string Path { get; set; } = "";
    public int Count { get; set; }
    public long FirstTicks { get; set; }
    public long LastTicks { get; set; }
    public bool IsDir { get; set; }
}

public sealed class PairRecord
{
    public string A { get; set; } = "";
    public string B { get; set; } = "";
    public int Count { get; set; }
    public int Sessions { get; set; }
    public long LastTicks { get; set; }
    public long LastSession { get; set; }
}

public sealed class UsageData
{
    public int Version { get; set; } = 1;
    public Dictionary<string, UsageRecord> Records { get; set; } = new();
    public Dictionary<string, PairRecord> Pairs { get; set; } = new();
}

public readonly struct RelatedItem
{
    public readonly string Path;
    public readonly double Strength;
    public readonly int Count;
    public readonly int Sessions;
    public RelatedItem(string path, double strength, int count, int sessions)
    { Path = path; Strength = strength; Count = count; Sessions = sessions; }
}

/// <summary>
/// Local-only JSON usage tracker. Stores open frequency/recency per path and co-access pairs
/// (used later for Ghost Files). Data never leaves the machine.
///
/// Relevance score (documented, weights configurable):
///   score = FrequencyWeight * log2(1 + openCount)
///         + RecencyWeight   * 2^(-daysSinceLastOpen / RecencyHalfLifeDays)   (only if opened before)
///         + FilesystemWeight* 2^(-daysSinceModified / FilesystemHalfLifeDays)
/// The filesystem term is deliberately the smallest: timestamps can be stale or unreliable on Windows.
/// </summary>
public sealed class UsageTracker
{
    const int CurrentVersion = 1;
    const int MaxRecords = 5000;
    const int MaxPairs = 5000;

    static UsageTracker _instance;
    public static UsageTracker Instance => _instance ??= new UsageTracker();

    public double FrequencyWeight = 1.0;
    public double RecencyWeight = 2.0;
    public double RecencyHalfLifeDays = 7.0;
    public double FilesystemWeight = 1.0;
    public double FilesystemHalfLifeDays = 30.0;
    /// <summary>Files opened within this window of each other count as "used together".</summary>
    public TimeSpan CoAccessWindow = TimeSpan.FromMinutes(2);

    readonly object _lock = new();
    readonly string _filePath;
    readonly long _sessionStamp = DateTime.UtcNow.Ticks;
    readonly List<(string key, long ticks)> _recent = new();
    UsageData _data = new();
    System.Threading.Timer _saveTimer;
    bool _dirty;

    UsageTracker()
    {
        string dir;
        try { dir = ProjectSettings.GlobalizePath("user://"); }
        catch { dir = Path.Combine(System.Environment.GetFolderPath(System.Environment.SpecialFolder.ApplicationData), "ATLAS"); }
        try { Directory.CreateDirectory(dir); } catch { }
        _filePath = Path.Combine(dir, "atlas_usage.json");
        Load();
    }

    static string Key(string path)
    {
        try { path = Path.GetFullPath(path); } catch { }
        if (path.Length > 3) path = path.TrimEnd('\\', '/');
        return path.ToLowerInvariant();
    }

    // ------------------------------------------------------------------ recording

    public void RecordOpen(string path, bool isDirectory)
    {
        if (string.IsNullOrWhiteSpace(path)) return;
        var key = Key(path);
        long now = DateTime.UtcNow.Ticks;
        lock (_lock)
        {
            if (!_data.Records.TryGetValue(key, out var r))
            {
                r = new UsageRecord { Path = path, FirstTicks = now, IsDir = isDirectory };
                _data.Records[key] = r;
            }
            r.Count++;
            r.LastTicks = now;
            r.Path = path;

            if (!isDirectory)
            {
                foreach (var (otherKey, t) in _recent)
                {
                    if (otherKey == key || now - t > CoAccessWindow.Ticks) continue;
                    var (a, b) = string.CompareOrdinal(otherKey, key) < 0 ? (otherKey, key) : (key, otherKey);
                    var pk = a + "|" + b;
                    if (!_data.Pairs.TryGetValue(pk, out var p))
                    {
                        p = new PairRecord { A = _data.Records.TryGetValue(a, out var ra) ? ra.Path : a, B = _data.Records.TryGetValue(b, out var rb) ? rb.Path : b };
                        _data.Pairs[pk] = p;
                    }
                    p.Count++;
                    p.LastTicks = now;
                    if (p.LastSession != _sessionStamp) { p.Sessions++; p.LastSession = _sessionStamp; }
                }
                _recent.Add((key, now));
                _recent.RemoveAll(x => now - x.ticks > CoAccessWindow.Ticks);
            }
            Trim();
            _dirty = true;
        }
        ScheduleSave();
    }

    // ------------------------------------------------------------------ queries

    public int GetCount(string path)
    {
        lock (_lock) return _data.Records.TryGetValue(Key(path), out var r) ? r.Count : 0;
    }

    public double GetRecencyDays(string path)
    {
        lock (_lock)
        {
            if (!_data.Records.TryGetValue(Key(path), out var r)) return 9999;
            return Math.Max(0, (DateTime.UtcNow - new DateTime(r.LastTicks, DateTimeKind.Utc)).TotalDays);
        }
    }

    /// <summary>Fills UsageCount, RecencyDays and UsageScore on the entry.</summary>
    public void Annotate(FsEntry e)
    {
        e.UsageCount = GetCount(e.Path);
        e.RecencyDays = GetRecencyDays(e.Path);
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

    /// <summary>Files frequently opened together with <paramref name="path"/> (basis for Ghost Files, Part 2).</summary>
    public List<RelatedItem> GetRelated(string path, int minCount = 3, int minSessions = 2, int limit = 8)
    {
        var key = Key(path);
        var result = new List<RelatedItem>();
        lock (_lock)
        {
            foreach (var p in _data.Pairs.Values)
            {
                if (p.Count < minCount || p.Sessions < minSessions) continue;
                string other;
                if (Key(p.A) == key) other = p.B; else if (Key(p.B) == key) other = p.A; else continue;
                double ageDays = Math.Max(0, (DateTime.UtcNow - new DateTime(p.LastTicks, DateTimeKind.Utc)).TotalDays);
                double strength = p.Count * Math.Pow(2, -ageDays / 30.0);
                result.Add(new RelatedItem(other, strength, p.Count, p.Sessions));
            }
        }
        return result.OrderByDescending(r => r.Strength).Take(limit).ToList();
    }

    // ------------------------------------------------------------------ persistence

    void Trim()
    {
        if (_data.Records.Count > MaxRecords)
        {
            foreach (var k in _data.Records.OrderBy(kv => kv.Value.LastTicks).Take(MaxRecords / 10).Select(kv => kv.Key).ToList())
                _data.Records.Remove(k);
        }
        if (_data.Pairs.Count > MaxPairs)
        {
            foreach (var k in _data.Pairs.OrderBy(kv => kv.Value.Count).ThenBy(kv => kv.Value.LastTicks).Take(MaxPairs / 10).Select(kv => kv.Key).ToList())
                _data.Pairs.Remove(k);
        }
    }

    void Load()
    {
        try
        {
            if (!File.Exists(_filePath)) return;
            var json = File.ReadAllText(_filePath);
            var data = JsonSerializer.Deserialize<UsageData>(json);
            if (data == null) return;
            if (data.Version > CurrentVersion)
            {
                GD.PushWarning("[ATLAS] usage file is from a newer version; starting a fresh history.");
                return;
            }
            data.Records ??= new();
            data.Pairs ??= new();
            _data = data;
        }
        catch (Exception ex)
        {
            GD.PushWarning($"[ATLAS] could not read usage history ({ex.Message}); keeping a backup and starting fresh.");
            try { File.Move(_filePath, _filePath + ".corrupt-" + DateTime.UtcNow.Ticks, true); } catch { }
            _data = new UsageData();
        }
    }

    void ScheduleSave()
    {
        lock (_lock)
        {
            _saveTimer ??= new System.Threading.Timer(_ => Flush(), null, System.Threading.Timeout.Infinite, System.Threading.Timeout.Infinite);
            _saveTimer.Change(2000, System.Threading.Timeout.Infinite);
        }
    }

    /// <summary>Writes pending changes now (safe write: temp file then atomic replace).</summary>
    public void Flush()
    {
        string json;
        lock (_lock)
        {
            if (!_dirty) return;
            _data.Version = CurrentVersion;
            json = JsonSerializer.Serialize(_data);
            _dirty = false;
        }
        try
        {
            var tmp = _filePath + ".tmp";
            File.WriteAllText(tmp, json);
            if (File.Exists(_filePath)) File.Replace(tmp, _filePath, _filePath + ".bak");
            else File.Move(tmp, _filePath);
        }
        catch (Exception ex)
        {
            GD.PushWarning($"[ATLAS] could not save usage history: {ex.Message}");
            lock (_lock) _dirty = true;
        }
    }
}
