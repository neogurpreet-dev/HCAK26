using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text;

namespace Atlas;

/// <summary>
/// Synthetic-history self-test for Usage Frequency, the relationship model and the activity store.
/// Run it from the command line:   Godot --path D:\HCAK26\ATLAS -- selftest     (results are printed to the console / log and shown as a toast).
/// Pure .NET, no Godot types. The same scenarios exist in a Python reference implementation used to calibrate the thresholds.
/// </summary>
public static class ActivityModelSelfTest
{
    static readonly DateTime Now = new DateTime(2026, 10, 24, 12, 0, 0, DateTimeKind.Utc);
    static readonly ActivitySettings S = new ActivitySettings();

    sealed class Report
    {
        public readonly StringBuilder Text = new StringBuilder();
        public int Passed, Failed;
        public void Check(string name, bool ok, string detail = "")
        {
            if (ok) Passed++; else Failed++;
            Text.Append(ok ? "PASS " : "FAIL ").Append(name);
            if (detail.Length > 0) Text.Append("   ").Append(detail);
            Text.AppendLine();
        }
    }

    // ---------------------------------------------------------------- helpers

    static long At(double days = 0, double minutes = 0, double seconds = 0)
        => Now.AddDays(-days).AddMinutes(-minutes).AddSeconds(-seconds).Ticks;

    static ActivityEvent Ev(string id, long ticks, ActivityOrigin origin = ActivityOrigin.Atlas, bool ok = true, EntityType? type = null)
    {
        var t = type ?? ActivityIds.TypeOf(id);
        var kind = t == EntityType.Application ? ActivityKind.AppActivation : (t == EntityType.Folder ? ActivityKind.FolderOpen : ActivityKind.FileOpen);
        return new ActivityEvent { Id = id, Type = t, Kind = kind, Origin = origin, Ticks = ticks, RunId = 1, Success = ok };
    }

    /// <summary>One session: the ids are opened gapSeconds apart starting at startTicks.</summary>
    static List<ActivityEvent> Session(long startTicks, string[] ids, double gapSeconds = 20)
    {
        var l = new List<ActivityEvent>();
        for (int k = 0; k < ids.Length; k++) l.Add(Ev(ids[k], startTicks + (long)(k * gapSeconds * TimeSpan.TicksPerSecond)));
        return l;
    }

    static List<ActivityEvent> PairEvents(string a, string b, int n, double gap = 30, double startDays = 1, double stepDays = 2)
    {
        var l = new List<ActivityEvent>();
        for (int k = 0; k < n; k++) l.AddRange(Session(At(days: startDays + k * stepDays), new[] { a, b }, gap));
        return l;
    }

    static RelationshipIndex Idx(IEnumerable<ActivityEvent> e) => RelationshipIndex.Build(e, S, Now);

    static List<string> Related(RelationshipIndex ix, string id, GhostSensitivity s)
        => ix.GetRelated(id, s, 8).Select(r => r.Id).ToList();

    // ---------------------------------------------------------------- tests

    public static string Run(out bool allPassed)
    {
        var r = new Report();
        const string A = "file:a", B = "file:b", W = "app:word", C = "app:chrome", G = "app:chatgpt", V = "app:vscode", D = "file:doc";

        // 1 repeated file-file co-usage increases strength
        var scores = new[] { 1, 2, 3, 5, 10 }.Select(n => Idx(PairEvents(A, B, n)).Score(A, B)).ToList();
        bool rising = true;
        for (int i = 1; i < scores.Count; i++) if (!(scores[i] > scores[i - 1])) rising = false;
        r.Check("1 file-file strength grows with repetition", rising, string.Join(", ", scores.Select(x => x.ToString("0.000"))));

        // 2 file-app, 3 app-app
        r.Check("2 file-app relationship created", Related(Idx(PairEvents(A, W, 4)), A, GhostSensitivity.Balanced).SequenceEqual(new[] { W }));
        r.Check("3 app-app relationship created", Related(Idx(PairEvents(C, V, 4)), C, GhostSensitivity.Balanced).SequenceEqual(new[] { V }));

        // 4 assignment workflow: Word + doc + Chrome + ChatGPT six times, Chrome also used alone 30 times
        var assign = new List<ActivityEvent>();
        foreach (var d in new[] { 1, 3, 6, 9, 14, 20 }) assign.AddRange(Session(At(days: d), new[] { W, D, C, G }, 25));
        for (int k = 0; k < 30; k++) assign.Add(Ev(C, At(days: (k * 0.93) % 28, minutes: k * 7)));
        var ixA = Idx(assign);
        var relDoc = Related(ixA, D, GhostSensitivity.Balanced).OrderBy(x => x, StringComparer.Ordinal).ToList();
        r.Check("4 assignment workflow reveals Word, Chrome, ChatGPT (balanced)", relDoc.SequenceEqual(new[] { G, C, W }.OrderBy(x => x, StringComparer.Ordinal)), string.Join(",", relDoc));
        r.Check("4b selecting Word reveals the document", Related(ixA, W, GhostSensitivity.Balanced).Contains(D));

        // 5 closer events weigh more
        double close = Idx(PairEvents(A, B, 5, gap: 10)).Score(A, B), far = Idx(PairEvents(A, B, 5, gap: 200)).Score(A, B);
        r.Check("5 events seconds apart > events minutes apart", close > far, close.ToString("0.000") + " vs " + far.ToString("0.000"));

        // 6 outside the window
        var ix6 = Idx(PairEvents(A, B, 5, gap: 400));
        r.Check("6 events more than a session gap apart give no pair", ix6.Score(A, B) == 0.0 && ix6.Candidates(A).Count == 0);
        var e6 = new List<ActivityEvent>();
        for (int k = 0; k < 5; k++)
        {
            long t0 = At(days: 1 + k * 2);
            long s = TimeSpan.TicksPerSecond;
            e6.Add(Ev(A, t0)); e6.Add(Ev("file:f1", t0 + 200 * s)); e6.Add(Ev("file:f2", t0 + 400 * s)); e6.Add(Ev("file:f3", t0 + 595 * s)); e6.Add(Ev(B, t0 + 600 * s));
        }
        var ix6b = Idx(e6);
        r.Check("6b same session but outside the co-usage window: no A-B pair", ix6b.Score(A, B) == 0.0);
        r.Check("6c neighbours inside the window still pair", ix6b.Score("file:f3", B) > 0.0);

        // 7 recency
        double sNew = Idx(PairEvents(A, B, 5, startDays: 1)).Score(A, B), sOld = Idx(PairEvents(A, B, 5, startDays: 90)).Score(A, B);
        r.Check("7 recent co-usage > old co-usage", sNew > sOld, sNew.ToString("0.000") + " vs " + sOld.ToString("0.000"));

        // 8 unrelated entities
        var e8 = PairEvents("file:p", "file:q", 8);
        for (int k = 0; k < 8; k++) e8.AddRange(Session(At(days: 1 + k * 2, minutes: 180), new[] { "file:r", "file:s" }, 30));
        var ix8 = Idx(e8);
        r.Check("8 unrelated entities get no relationship", ix8.Score("file:p", "file:r") == 0.0 && !ix8.Candidates("file:p").Any(c => c.Id == "file:r"));

        // 9 popular application
        var e9 = new List<ActivityEvent>();
        for (int k = 0; k < 80; k++)
        {
            long t = At(days: k % 25, minutes: k * 11);
            e9.Add(Ev(C, t)); e9.Add(Ev("file:other" + (k % 40), t + 40 * TimeSpan.TicksPerSecond));
        }
        foreach (var d in new[] { 2, 8, 15 }) e9.AddRange(Session(At(days: d, minutes: 3), new[] { C, "file:rare" }, 30));
        var ix9 = Idx(e9);
        r.Check("9a popular app not linked to a rarely-used file (balanced)", Related(ix9, "file:rare", GhostSensitivity.Balanced).Count == 0);
        r.Check("9b popular app has no flood of links", Related(ix9, C, GhostSensitivity.Balanced).Count <= 2);

        // 10 single accident
        foreach (GhostSensitivity sens in Enum.GetValues(typeof(GhostSensitivity)))
            r.Check("10 single accidental co-opening shows nothing (" + sens + ")", Related(Idx(Session(At(days: 1), new[] { "file:x", "file:y" }, 15)), "file:x", sens).Count == 0);

        // 11 sensitivity
        var e11 = PairEvents(A, B, 3);
        e11.AddRange(PairEvents(A, "file:c", 2, startDays: 20));
        e11.AddRange(PairEvents(A, "file:d", 5, gap: 150));
        int before = e11.Count;
        var ix11 = Idx(e11);
        int cs = Related(ix11, A, GhostSensitivity.Strict).Count, cb = Related(ix11, A, GhostSensitivity.Balanced).Count, ce = Related(ix11, A, GhostSensitivity.Exploratory).Count;
        r.Check("11 sensitivity widens eligibility monotonically", cs <= cb && cb <= ce && cs < ce, cs + " / " + cb + " / " + ce);
        r.Check("11b events untouched by sensitivity", e11.Count == before);

        // 12 big sessions
        var e12 = new List<ActivityEvent>();
        for (int k = 0; k < 5; k++)
            for (int i = 0; i < 60; i++) e12.Add(Ev("file:b" + i, At(days: k + 1) + (long)(i * 4.0 * TimeSpan.TicksPerSecond)));
        var ix12 = Idx(e12);
        r.Check("12 unusually large sessions do not create links", Related(ix12, "file:b59", GhostSensitivity.Balanced).Count == 0 && Related(ix12, "file:b59", GhostSensitivity.Exploratory).Count == 0);

        // 13 quick launch ignored
        var e13 = new List<ActivityEvent>();
        for (int k = 0; k < 6; k++) { long t = At(days: k + 1); e13.Add(Ev(A, t, ActivityOrigin.QuickLaunch)); e13.Add(Ev(B, t + TimeSpan.TicksPerSecond, ActivityOrigin.QuickLaunch)); }
        r.Check("13 Open All (QuickLaunch) events cannot create relationships", Idx(e13).Candidates(A).Count == 0);

        // 14 usage frequency
        var hot = Enumerable.Range(0, 50).Select(k => At(days: k * 0.2)).ToList();
        var cold = new List<long> { At(days: 20), At(days: 25) };
        var old = Enumerable.Range(0, 50).Select(k => At(days: 90 + k * 0.1)).ToList();
        double uh = RelationshipIndex.UsageFrequency(hot, Now, 14), uc = RelationshipIndex.UsageFrequency(cold, Now, 14), uo = RelationshipIndex.UsageFrequency(old, Now, 14);
        r.Check("14 usage: 50 recent opens >> 2 old opens; heavy-but-old fades", uh > 10 * uc && uo < uc, uh.ToString("0.00") + " / " + uc.ToString("0.00") + " / " + uo.ToString("0.000"));

        // 15 folder events, 16 failures
        var e15 = new List<ActivityEvent>();
        for (int k = 0; k < 6; k++) { long t = At(days: k + 1); e15.Add(Ev("dir:x", t)); e15.Add(Ev(A, t + 2 * TimeSpan.TicksPerSecond)); }
        r.Check("15 folder events are not part of relationships", Idx(e15).Candidates(A).Count == 0);
        var e16 = new List<ActivityEvent>();
        for (int k = 0; k < 5; k++) { e16.Add(Ev(A, At(days: k + 1), ok: false)); e16.Add(Ev(B, At(days: k + 1) + 2 * TimeSpan.TicksPerSecond, ok: false)); }
        r.Check("16 failed events never form relationships", Idx(e16).Candidates(A).Count == 0);

        // 17 determinism
        var e17 = PairEvents(A, B, 4);
        e17.AddRange(PairEvents(A, "file:c", 4, startDays: 3));
        var fwd = Related(Idx(e17), A, GhostSensitivity.Balanced);
        var rev = Related(Idx(Enumerable.Reverse(e17).ToList()), A, GhostSensitivity.Balanced);
        r.Check("17 ordering is deterministic regardless of input order", fwd.SequenceEqual(rev));

        // 18-20 store: persistence, sensitivity setting, clear history (temp directory)
        string dir = Path.Combine(Path.GetTempPath(), "atlas_selftest_" + Guid.NewGuid().ToString("N"));
        try
        {
            var store = new ActivityStore(dir);
            string fa = ActivityIds.File(Path.Combine(dir, "a.docx")), fb = ActivityIds.App(Path.Combine(dir, "word.exe"));
            for (int k = 0; k < 4; k++)
            {
                var t = DateTime.UtcNow.AddDays(-(k + 1));
                store.RecordEvent(fa, EntityType.File, ActivityKind.FileOpen, ActivityOrigin.Atlas, "a.docx", "a.docx", t);
                store.RecordEvent(fb, EntityType.Application, ActivityKind.AppLaunch, ActivityOrigin.Atlas, "word.exe", "Word", t.AddSeconds(1));
            }
            store.UpdateSettings(s => s.Sensitivity = GhostSensitivity.Exploratory);
            store.Flush();
            var again = new ActivityStore(dir);
            r.Check("18 history persists across restarts", again.EventCount == 8 && again.GetCount(fa) == 4 && again.GetEntity(fb) != null && again.GetEntity(fb).Name == "Word");
            r.Check("19 sensitivity setting persists", again.Settings.Sensitivity == GhostSensitivity.Exploratory);
            r.Check("19b stored history yields the learned link", again.GetRelated(fa).Any(x => x.Id == fb));
            again.UpdateSettings(s => s.TrackActivity = false);
            bool recorded = again.RecordEvent(fa, EntityType.File, ActivityKind.FileOpen, ActivityOrigin.Atlas, "a.docx", "a.docx");
            r.Check("20 tracking switch stops recording", !recorded && again.EventCount == 8);
            again.UpdateSettings(s => s.TrackActivity = true);
            again.ClearHistory();
            var cleared = new ActivityStore(dir);
            r.Check("21 clearing history removes events and learned links, keeps settings", cleared.EventCount == 0 && cleared.GetRelated(fa).Count == 0 && cleared.Settings.Sensitivity == GhostSensitivity.Exploratory);
        }
        catch (Exception ex) { r.Check("store tests ran without exception", false, ex.Message); }
        finally { try { Directory.Delete(dir, true); } catch { } }

        r.Text.AppendLine().Append(r.Passed).Append('/').Append(r.Passed + r.Failed).Append(" passed");
        allPassed = r.Failed == 0;
        return r.Text.ToString();
    }
}
