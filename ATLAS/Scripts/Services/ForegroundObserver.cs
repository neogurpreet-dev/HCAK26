using System;
using Godot;

namespace Atlas;

/// <summary>
/// Optional, opt-in observer: once per second it asks Windows which program owns the foreground window and, when that
/// program has been in front for ForegroundDwellSeconds, records one AppActivation event for it.
///
/// Privacy: only the executable path of the foreground program is read. Window titles, document names, keystrokes and
/// screen contents are never read. Nothing is recorded unless BOTH "Activity tracking" and "Observe app activation outside ATLAS"
/// are on. ATLAS itself and Windows shell hosts (Explorer, Start, Search...) are ignored.
/// Limits: elevated programs and some Store apps cannot be identified; a browser is one application (ATLAS cannot tell
/// which website or web app is open in it).
/// </summary>
public partial class ForegroundObserver : Node
{
    public ActivityStore Store { get; set; }

    const double PollSeconds = 1.0;

    double _acc;
    string _selfExe;
    string _candidateId;
    string _candidateExe;
    double _candidateSince;
    string _lastRecordedId;
    double _clock;

    public override void _Ready()
    {
        _selfExe = WinShell.CurrentProcessExe();
        SetProcess(OperatingSystem.IsWindows());
    }

    public override void _Process(double delta)
    {
        _clock += delta;
        _acc += delta;
        if (_acc < PollSeconds) return;
        _acc = 0;
        Poll();
    }

    void Poll()
    {
        if (Store == null) return;
        var s = Store.Settings;
        if (!s.TrackActivity || !s.ObserveExternalApps)
        {
            _candidateId = null; _candidateExe = null; _lastRecordedId = null;
            return;
        }

        string exe = WinShell.GetForegroundExe();
        bool ignored = exe == null
            || WinShell.IsIgnoredProgram(exe)
            || (_selfExe != null && string.Equals(exe, _selfExe, StringComparison.OrdinalIgnoreCase));
        if (ignored)
        {
            // ATLAS (or the shell) is in front: the next return to a program counts as a fresh activation
            _candidateId = null; _candidateExe = null; _lastRecordedId = null;
            return;
        }

        string id = ActivityIds.App(exe);
        if (id != _candidateId)
        {
            _candidateId = id;
            _candidateExe = exe;
            _candidateSince = _clock;
            return;
        }

        if (id != _lastRecordedId && _clock - _candidateSince >= s.ForegroundDwellSeconds)
        {
            _lastRecordedId = id;
            if (Store.IsActivationSuppressed(id)) return; // ATLAS launched it a moment ago and already recorded that
            Store.RecordEvent(id, EntityType.Application, ActivityKind.AppActivation, ActivityOrigin.External, _candidateExe, WinShell.DisplayName(_candidateExe));
        }
    }
}
