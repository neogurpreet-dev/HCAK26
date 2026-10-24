using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text.Json;

namespace Atlas;

/// <summary>
/// Local-only activity history + settings. Pure .NET (no Godot types).
///
/// Files (in the ATLAS user data folder, never sent anywhere):
///   atlas_activity.json           entity table + compact event log (history, can be cleared)
///   atlas_activity_settings.json  tracking switches, Ghost Link Sensitivity and all model parameters (kept when history is cleared)
///
/// Stored per event: entity id, kind, origin, UTC time, ATLAS run id. Stored per entity: type, canonical path, display name.
/// Never stored: file contents, window titles, keystrokes, anything about other people.
/// </summary>
public sealed class ActivityStore
{
    const int FormatVersion = 1;
    const int MaxEvents = 30000;
    const double RetentionDays = 240.0;   // exp(-240/30) ~ 3e-4: older events cannot matter any more

    readonly object _lock = new object();
    readonly string _eventsPath;
    readonly string _settingsPath;
    readonly long _runId = DateTime.UtcNow.Ticks;
    readonly List<ActivityEvent> _events = new List<ActivityEvent>();
    readonly Dictionary<string, EntityInfo> _entities = new Dictionary<string, EntityInfo>();
    readonly Dictionary<string, List<long>> _ticksById = new Dictionary<string, List<long>>();
    readonly Dictionary<string, long> _suppressUntil = new Dictionary<string, long>();

    ActivitySettings _settings = new ActivitySettings();
    RelationshipIndex _index;
    int _version;
    int _indexVersion = -1;
    long _indexBuilt;
    System.Threading.Timer _timer;
    // Temporary developer test overlay (see GhostTestFixtures). In memory only; never stored, never written to history.
    volatile IRelationOverlay _overlay;
    bool _eventsDirty;
    bool _settingsDirty;

    /// <summary>Raised on the calling thread after settings changed (sensitivity, tracking switches...).</summary>
    public event Action SettingsChanged;
    /// <summary>Raised after a new event was recorded.</summary>
    public event Action ActivityChanged;
    /// <summary>Raised after the history was cleared.</summary>
    public event Action HistoryCleared;

    public ActivityStore(string directory)
    {
        try { Directory.CreateDirectory(directory); } catch { }
        _eventsPath = Path.Combine(directory, "atlas_activity.json");
        _settingsPath = Path.Combine(directory, "atlas_activity_settings.json");
        LoadSettings();
        LoadEvents();
    }

    /// <summary>True while the developer Ghost Links test mode is on.</summary>
    public bool TestModeActive => _overlay != null;

    /// <summary>
    /// Installs (or, with null, removes) the test overlay. While one is installed the relationship QUERY answers from it for the
    /// fixture entities, and nothing is recorded, so real history, scores and settings cannot change. Removing it restores normal behaviour at once.
    /// </summary>
    public void SetOverlay(IRelationOverlay overlay)
    {
        _overlay = overlay;
        var h = SettingsChanged; // listeners (Ghost Links) re-query
        if (h != null) h();
    }

    public ActivitySettings Settings
    {
        get { lock (_lock) return _settings.Clone(); }
    }

    public int EventCount
    {
        get { lock (_lock) return _events.Count; }
    }

    // ------------------------------------------------------------------ settings

    public void UpdateSettings(Action<ActivitySettings> mutate)
    {
        lock (_lock)
        {
            mutate(_settings);
            _settingsDirty = true;
            _version++; // thresholds / parameters changed: cached relationship index is stale
        }
        ScheduleSave();
        var h = SettingsChanged;
        if (h != null) h();
    }

    // ------------------------------------------------------------------ recording

    /// <summary>Records one SUCCESSFUL event. Returns false when tracking is switched off or the id is empty.</summary>
    public bool RecordEvent(string id, EntityType type, ActivityKind kind, ActivityOrigin origin, string path, string name, DateTime? whenUtc = null)
    {
        if (string.IsNullOrEmpty(id)) return false;
        if (_overlay != null) return false; // test mode: real history is never modified
        lock (_lock)
        {
            if (!_settings.TrackActivity) return false;
            long t = (whenUtc ?? DateTime.UtcNow).Ticks;

            EntityInfo info;
            if (!_entities.TryGetValue(id, out info)) { info = new EntityInfo { Id = id, Type = type }; _entities[id] = info; }
            if (!string.IsNullOrEmpty(path)) info.Path = path;
            if (!string.IsNullOrEmpty(name)) info.Name = name;
            info.Type = type;

            AddEventLocked(new ActivityEvent { Id = id, Type = type, Kind = kind, Origin = origin, Ticks = t, RunId = _runId, Success = true });
            TrimLocked();
            _eventsDirty = true;
            _version++;
        }
        ScheduleSave();
        var h = ActivityChanged;
        if (h != null) h();
        return true;
    }

    void AddEventLocked(ActivityEvent e)
    {
        _events.Add(e);
        List<long> l;
        if (!_ticksById.TryGetValue(e.Id, out l)) { l = new List<long>(); _ticksById[e.Id] = l; }
        l.Add(e.Ticks);
    }

    void TrimLocked()
    {
        long cutoff = DateTime.UtcNow.AddDays(-RetentionDays).Ticks;
        bool tooOld = _events.Count > 0 && _events.Any(e => e.Ticks < cutoff);
        if (_events.Count <= MaxEvents && !tooOld) return;

        var keep = _events.Where(e => e.Ticks >= cutoff).OrderBy(e => e.Ticks).ToList();
        if (keep.Count > MaxEvents) keep = keep.Skip(keep.Count - MaxEvents).ToList();
        _events.Clear();
        _ticksById.Clear();
        foreach (var e in keep) AddEventLocked(e);
        foreach (var id in _entities.Keys.Where(k => !_ticksById.ContainsKey(k)).ToList()) _entities.Remove(id);
    }

    /// <summary>Foreground activations of this program are ignored until the given moment (used right after Open All).</summary>
    public void SuppressActivation(string appId, double seconds)
    {
        lock (_lock) _suppressUntil[appId] = DateTime.UtcNow.AddSeconds(seconds).Ticks;
    }

    public bool IsActivationSuppressed(string appId)
    {
        lock (_lock)
        {
            long until;
            return _suppressUntil.TryGetValue(appId, out until) && DateTime.UtcNow.Ticks < until;
        }
    }

    // ------------------------------------------------------------------ queries

    public int GetCount(string id)
    {
        lock (_lock)
        {
            List<long> l;
            return _ticksById.TryGetValue(id, out l) ? l.Count : 0;
        }
    }

    /// <summary>Time of the most recent event of an entity, or null.</summary>
    public DateTime? GetLastUsedUtc(string id)
    {
        lock (_lock)
        {
            List<long> l;
            if (!_ticksById.TryGetValue(id, out l) || l.Count == 0) return null;
            return new DateTime(l.Max(), DateTimeKind.Utc);
        }
    }

    /// <summary>Usage Frequency U(x) = sum exp(-age_days / tau_u).</summary>
    public double GetUsageFrequency(string id, DateTime nowUtc)
    {
        lock (_lock)
        {
            List<long> l;
            if (!_ticksById.TryGetValue(id, out l)) return 0.0;
            return RelationshipIndex.UsageFrequency(l, nowUtc, _settings.UsageTauDays);
        }
    }

    public EntityInfo GetEntity(string id)
    {
        var ov = _overlay;
        if (ov != null) { var injected = ov.GetEntity(id); if (injected != null) return injected; }
        lock (_lock)
        {
            EntityInfo e;
            return _entities.TryGetValue(id, out e) ? new EntityInfo { Id = e.Id, Type = e.Type, Path = e.Path, Name = e.Name } : null;
        }
    }

    RelationshipIndex GetIndex()
    {
        lock (_lock)
        {
            long now = DateTime.UtcNow.Ticks;
            // recency weights move with the clock, so rebuild at least once a minute even if nothing changed
            if (_index == null || _indexVersion != _version || now - _indexBuilt > TimeSpan.TicksPerMinute)
            {
                _index = RelationshipIndex.Build(_events, _settings, DateTime.UtcNow);
                _indexVersion = _version;
                _indexBuilt = now;
            }
            return _index;
        }
    }

    /// <summary>Entities related to <paramref name="id"/> at the CURRENT sensitivity (history is untouched by sensitivity).</summary>
    public List<RelatedEntity> GetRelated(string id)
    {
        var ov = _overlay;
        if (ov != null && ov.TryGetRelated(id, out var injected)) return injected; // test fixtures answer; production scoring is not touched
        ActivitySettings s;
        lock (_lock) s = _settings.Clone();
        return GetIndex().GetRelated(id, s.Sensitivity, s.MaxRelated);
    }

    /// <summary>All candidates regardless of thresholds (diagnostics and tests).</summary>
    public List<RelatedEntity> GetCandidates(string id)
    {
        return GetIndex().Candidates(id);
    }

    // ------------------------------------------------------------------ clearing

    /// <summary>Deletes all recorded events, entities and learned relationships. Settings are kept.</summary>
    public void ClearHistory()
    {
        lock (_lock)
        {
            _events.Clear();
            _entities.Clear();
            _ticksById.Clear();
            _suppressUntil.Clear();
            _index = null;
            _version++;
            _eventsDirty = true;
        }
        Flush();
        var h = HistoryCleared;
        if (h != null) h();
    }

    // ------------------------------------------------------------------ persistence

    sealed class EntityDto
    {
        public string Id { get; set; } = "";
        public int T { get; set; }
        public string P { get; set; } = "";
        public string N { get; set; } = "";
    }

    sealed class EventDto
    {
        public int E { get; set; }      // index into Entities
        public int K { get; set; }      // ActivityKind
        public int O { get; set; }      // ActivityOrigin
        public long T { get; set; }     // UTC ticks
        public long R { get; set; }     // run id
    }

    sealed class StoreDto
    {
        public int Version { get; set; } = FormatVersion;
        public List<EntityDto> Entities { get; set; } = new List<EntityDto>();
        public List<EventDto> Events { get; set; } = new List<EventDto>();
    }

    void LoadSettings()
    {
        try
        {
            if (!File.Exists(_settingsPath)) return;
            var s = JsonSerializer.Deserialize<ActivitySettings>(File.ReadAllText(_settingsPath));
            if (s == null) return;
            if (!Enum.IsDefined(typeof(GhostSensitivity), s.Sensitivity)) s.Sensitivity = GhostSensitivity.Balanced;
            s.MaxRelated = Math.Clamp(s.MaxRelated, 1, 12);
            if (s.UsageTauDays <= 0) s.UsageTauDays = 14;
            if (s.RecencyTauDays <= 0) s.RecencyTauDays = 30;
            if (s.GapLambdaSeconds <= 0) s.GapLambdaSeconds = 120;
            if (s.SessionGapMinutes <= 0) s.SessionGapMinutes = 5;
            if (s.CoUsageWindowMinutes <= 0) s.CoUsageWindowMinutes = 5;
            if (s.SessionSizeSoftCap < 2) s.SessionSizeSoftCap = 10;
            if (s.SessionEntityHardCap < s.SessionSizeSoftCap) s.SessionEntityHardCap = 40;
            if (s.SmoothingK < 0) s.SmoothingK = 1;
            _settings = s;
        }
        catch (Exception)
        {
            try { File.Move(_settingsPath, _settingsPath + ".corrupt-" + DateTime.UtcNow.Ticks, true); } catch { }
            _settings = new ActivitySettings();
        }
    }

    void LoadEvents()
    {
        try
        {
            if (!File.Exists(_eventsPath)) return;
            var dto = JsonSerializer.Deserialize<StoreDto>(File.ReadAllText(_eventsPath));
            if (dto == null || dto.Version > FormatVersion) return;

            var ids = new List<string>();
            foreach (var e in dto.Entities)
            {
                ids.Add(e.Id);
                if (string.IsNullOrEmpty(e.Id)) continue;
                var type = Enum.IsDefined(typeof(EntityType), e.T) ? (EntityType)e.T : ActivityIds.TypeOf(e.Id);
                _entities[e.Id] = new EntityInfo { Id = e.Id, Type = type, Path = e.P ?? "", Name = e.N ?? "" };
            }
            foreach (var e in dto.Events)
            {
                if (e.E < 0 || e.E >= ids.Count || string.IsNullOrEmpty(ids[e.E])) continue;
                if (!Enum.IsDefined(typeof(ActivityKind), e.K) || !Enum.IsDefined(typeof(ActivityOrigin), e.O)) continue;
                var id = ids[e.E];
                AddEventLocked(new ActivityEvent
                {
                    Id = id, Type = _entities.ContainsKey(id) ? _entities[id].Type : ActivityIds.TypeOf(id),
                    Kind = (ActivityKind)e.K, Origin = (ActivityOrigin)e.O, Ticks = e.T, RunId = e.R, Success = true,
                });
            }
            TrimLocked();
        }
        catch (Exception)
        {
            try { File.Move(_eventsPath, _eventsPath + ".corrupt-" + DateTime.UtcNow.Ticks, true); } catch { }
            _events.Clear(); _entities.Clear(); _ticksById.Clear();
        }
    }

    void ScheduleSave()
    {
        lock (_lock)
        {
            if (_timer == null) _timer = new System.Threading.Timer(_ => Flush(), null, System.Threading.Timeout.Infinite, System.Threading.Timeout.Infinite);
            _timer.Change(2500, System.Threading.Timeout.Infinite);
        }
    }

    /// <summary>Writes pending changes now (temp file + atomic replace).</summary>
    public void Flush()
    {
        string eventsJson = null, settingsJson = null;
        lock (_lock)
        {
            if (_eventsDirty)
            {
                var dto = new StoreDto();
                var index = new Dictionary<string, int>();
                foreach (var kv in _entities)
                {
                    index[kv.Key] = dto.Entities.Count;
                    dto.Entities.Add(new EntityDto { Id = kv.Key, T = (int)kv.Value.Type, P = kv.Value.Path, N = kv.Value.Name });
                }
                foreach (var e in _events)
                {
                    int ei;
                    if (!index.TryGetValue(e.Id, out ei)) continue;
                    dto.Events.Add(new EventDto { E = ei, K = (int)e.Kind, O = (int)e.Origin, T = e.Ticks, R = e.RunId });
                }
                eventsJson = JsonSerializer.Serialize(dto);
                _eventsDirty = false;
            }
            if (_settingsDirty)
            {
                settingsJson = JsonSerializer.Serialize(_settings);
                _settingsDirty = false;
            }
        }
        if (eventsJson != null && !WriteAtomic(_eventsPath, eventsJson)) lock (_lock) _eventsDirty = true;
        if (settingsJson != null && !WriteAtomic(_settingsPath, settingsJson)) lock (_lock) _settingsDirty = true;
    }

    static bool WriteAtomic(string path, string text)
    {
        try
        {
            var tmp = path + ".tmp";
            File.WriteAllText(tmp, text);
            if (File.Exists(path)) File.Replace(tmp, path, null); else File.Move(tmp, path);
            return true;
        }
        catch (Exception) { return false; }
    }
}
