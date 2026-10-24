using System;
using System.Collections.Generic;
using System.Linq;

namespace Atlas;

/// <summary>One qualifying (or candidate) relationship from the point of view of a selected entity.</summary>
public sealed class RelatedEntity
{
    public string Id = "";
    public EntityType Type;
    /// <summary>Normalised association S(A,B). A unitless ranking score, deliberately not shown as a percentage.</summary>
    public double Score;
    /// <summary>Raw recency-weighted co-usage R(A,B).</summary>
    public double CoUsage;
    /// <summary>Number of distinct sessions in which the pair was used together.</summary>
    public int Sessions;
}

/// <summary>
/// The relationship model. Same maths for file-file, file-application and application-application pairs.
///
///  1. Sessions     : successful events (QuickLaunch and folder events excluded) are sorted by time; a gap longer than
///                    SessionGapMinutes starts a new session.
///  2. Pair weight  : inside a session, for entities A and B take the closest pair of their events, gap dt (seconds).
///                    If dt &lt;= CoUsageWindowMinutes:   w_s = exp(-dt / lambda)     else the pair gets nothing from that session.
///                    Each pair is counted at most once per session (closest events only, so repeats cannot inflate it).
///  3. Size damping : a session with n distinct entities contributes  f_s = min(1, SoftCap / n)  to every pair in it;
///                    pairs are only formed among the HardCap most recent entities of a session.
///  4. Recency      : C_s(A,B) = w_s * f_s    (the contribution of ONE session)
///                    R(A,B)   = sum_s C_s(A,B) * exp(-d_s / tau)       d_s = age of session s in days, tau = RecencyTauDays
///  5. Normalise    : N(A) = sum over sessions containing A of exp(-d_s / tau)   (recency-weighted presence)
///                    S(A,B) = R(A,B) / sqrt( (N(A)+k) * (N(B)+k) )
///                    Dividing by both entities' own activity stops globally popular programs (a browser) from being linked
///                    to everything, and the +k smoothing stops rare pairs from scoring misleadingly high.
///  6. Eligibility  : B is related to A for a sensitivity if  sessions(A,B) >= max(2, MinSessions)  and  S(A,B) >= MinScore.
/// </summary>
public sealed class RelationshipIndex
{
    sealed class Acc
    {
        public double R;
        public int Sessions;
    }

    readonly ActivitySettings _s;
    readonly Dictionary<string, Dictionary<string, Acc>> _pairs = new Dictionary<string, Dictionary<string, Acc>>();
    readonly Dictionary<string, double> _presence = new Dictionary<string, double>();
    readonly Dictionary<string, EntityType> _types = new Dictionary<string, EntityType>();

    public int SessionCount { get; private set; }
    public int EventsUsed { get; private set; }

    RelationshipIndex(ActivitySettings s) { _s = s; }

    public static bool CountsForRelationships(ActivityEvent e)
    {
        return e.Success
            && e.Origin != ActivityOrigin.QuickLaunch
            && e.Type != EntityType.Folder
            && e.Kind != ActivityKind.FolderOpen;
    }

    public static RelationshipIndex Build(IEnumerable<ActivityEvent> events, ActivitySettings settings, DateTime nowUtc)
    {
        var idx = new RelationshipIndex(settings);
        var list = events.Where(CountsForRelationships).OrderBy(e => e.Ticks).ToList();
        idx.EventsUsed = list.Count;

        long gap = (long)(settings.SessionGapMinutes * TimeSpan.TicksPerMinute);
        var current = new List<ActivityEvent>();
        long prev = 0;
        foreach (var e in list)
        {
            if (current.Count > 0 && e.Ticks - prev > gap)
            {
                idx.AddSession(current, nowUtc);
                current = new List<ActivityEvent>();
            }
            current.Add(e);
            prev = e.Ticks;
        }
        if (current.Count > 0) idx.AddSession(current, nowUtc);
        return idx;
    }

    void AddSession(List<ActivityEvent> session, DateTime nowUtc)
    {
        SessionCount++;

        var times = new Dictionary<string, List<long>>();
        long end = 0;
        foreach (var e in session)
        {
            List<long> l;
            if (!times.TryGetValue(e.Id, out l)) { l = new List<long>(); times[e.Id] = l; }
            l.Add(e.Ticks);
            _types[e.Id] = e.Type;
            if (e.Ticks > end) end = e.Ticks;
        }
        int n = times.Count;

        double ageDays = Math.Max(0.0, (nowUtc.Ticks - end) / (double)TimeSpan.TicksPerDay);
        double omega = Math.Exp(-ageDays / _s.RecencyTauDays);

        foreach (var id in times.Keys)
        {
            double p;
            _presence.TryGetValue(id, out p);
            _presence[id] = p + omega;
        }

        double f = n <= _s.SessionSizeSoftCap ? 1.0 : (double)_s.SessionSizeSoftCap / n;

        IEnumerable<string> chosen = times.Keys;
        if (n > _s.SessionEntityHardCap)
        {
            chosen = times
                .OrderByDescending(kv => kv.Value[kv.Value.Count - 1])
                .ThenBy(kv => kv.Key, StringComparer.Ordinal)
                .Take(_s.SessionEntityHardCap)
                .Select(kv => kv.Key);
        }
        var ids = chosen.OrderBy(x => x, StringComparer.Ordinal).ToArray();

        double windowSec = _s.CoUsageWindowMinutes * 60.0;
        for (int i = 0; i < ids.Length; i++)
        {
            for (int j = i + 1; j < ids.Length; j++)
            {
                double gapSec = MinGapSeconds(times[ids[i]], times[ids[j]]);
                if (gapSec > windowSec) continue;
                double w = Math.Exp(-gapSec / _s.GapLambdaSeconds);
                double c = w * f * omega;
                Add(ids[i], ids[j], c);
                Add(ids[j], ids[i], c);
            }
        }
    }

    void Add(string a, string b, double c)
    {
        Dictionary<string, Acc> row;
        if (!_pairs.TryGetValue(a, out row)) { row = new Dictionary<string, Acc>(); _pairs[a] = row; }
        Acc acc;
        if (!row.TryGetValue(b, out acc)) { acc = new Acc(); row[b] = acc; }
        acc.R += c;
        acc.Sessions++;
    }

    /// <summary>Smallest |ta - tb| over two ascending tick lists, in seconds.</summary>
    static double MinGapSeconds(List<long> a, List<long> b)
    {
        int i = 0, j = 0;
        long best = long.MaxValue;
        while (i < a.Count && j < b.Count)
        {
            long d = Math.Abs(a[i] - b[j]);
            if (d < best) best = d;
            if (a[i] < b[j]) i++; else j++;
        }
        return best / (double)TimeSpan.TicksPerSecond;
    }

    // ------------------------------------------------------------------ queries

    public double Presence(string id)
    {
        double p;
        return _presence.TryGetValue(id, out p) ? p : 0.0;
    }

    /// <summary>S(A,B); 0 when the two were never used together inside the co-usage window.</summary>
    public double Score(string a, string b)
    {
        Dictionary<string, Acc> row;
        Acc acc;
        if (!_pairs.TryGetValue(a, out row) || !row.TryGetValue(b, out acc)) return 0.0;
        double denom = Math.Sqrt((Presence(a) + _s.SmoothingK) * (Presence(b) + _s.SmoothingK));
        return denom > 0 ? acc.R / denom : 0.0;
    }

    /// <summary>Every entity ever used together with <paramref name="id"/>, strongest first (diagnostics, no thresholds).</summary>
    public List<RelatedEntity> Candidates(string id)
    {
        var result = new List<RelatedEntity>();
        Dictionary<string, Acc> row;
        if (!_pairs.TryGetValue(id, out row)) return result;
        foreach (var kv in row)
        {
            EntityType t;
            _types.TryGetValue(kv.Key, out t);
            result.Add(new RelatedEntity { Id = kv.Key, Type = t, Score = Score(id, kv.Key), CoUsage = kv.Value.R, Sessions = kv.Value.Sessions });
        }
        return result
            .OrderByDescending(r => r.Score)
            .ThenByDescending(r => r.Sessions)
            .ThenBy(r => r.Id, StringComparer.Ordinal)
            .ToList();
    }

    /// <summary>Entities that qualify at the given sensitivity. Deterministic order (score, sessions, id).</summary>
    public List<RelatedEntity> GetRelated(string id, GhostSensitivity sensitivity, int max)
    {
        int minSessions = _s.MinSessionsFor(sensitivity);
        double minScore = _s.MinScoreFor(sensitivity);
        return Candidates(id)
            .Where(r => r.Sessions >= minSessions && r.Score >= minScore)
            .Take(Math.Max(0, max))
            .ToList();
    }

    // ------------------------------------------------------------------ usage frequency

    /// <summary>U(x) = sum over events of exp(-age_days / tau_u). Pass only the events of one entity.</summary>
    public static double UsageFrequency(IEnumerable<long> eventTicksUtc, DateTime nowUtc, double tauDays)
    {
        double u = 0;
        foreach (var t in eventTicksUtc)
        {
            double age = Math.Max(0.0, (nowUtc.Ticks - t) / (double)TimeSpan.TicksPerDay);
            u += Math.Exp(-age / tauDays);
        }
        return double.IsFinite(u) ? u : 0.0;
    }
}
