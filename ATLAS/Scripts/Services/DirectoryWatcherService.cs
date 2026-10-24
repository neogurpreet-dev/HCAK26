using System;
using System.IO;
using System.Threading;

namespace Atlas;

/// <summary>
/// FileSystemWatcher wrapper that debounces / coalesces bursts of events.
/// <see cref="Changed"/> fires on a thread-pool thread: subscribers must marshal to the main thread
/// (the world generator uses Callable.From(...).CallDeferred()). Never touches Godot objects.
/// </summary>
public sealed class DirectoryWatcherService : IDisposable
{
    readonly object _lock = new();
    FileSystemWatcher _watcher;
    System.Threading.Timer _timer;
    bool _needsFullRescan;
    bool _disposed;

    public int DebounceMs { get; set; } = 400;

    /// <summary>Argument: true when the watcher overflowed/errored and a full rescan is required.</summary>
    public event Action<bool> Changed;

    public bool Start(string directory)
    {
        Stop();
        try
        {
            if (!Directory.Exists(directory)) return false;
            var w = new FileSystemWatcher(directory)
            {
                NotifyFilter = NotifyFilters.FileName | NotifyFilters.DirectoryName | NotifyFilters.LastWrite | NotifyFilters.Size | NotifyFilters.CreationTime,
                IncludeSubdirectories = false,
                InternalBufferSize = 64 * 1024,
            };
            w.Created += OnEvent;
            w.Deleted += OnEvent;
            w.Renamed += OnRenamed;
            w.Changed += OnEvent;
            w.Error += OnError;
            w.EnableRaisingEvents = true;
            lock (_lock)
            {
                if (_disposed) { w.Dispose(); return false; }
                _watcher = w;
                _timer = new System.Threading.Timer(_ => Fire(), null, Timeout.Infinite, Timeout.Infinite);
            }
            return true;
        }
        catch (Exception)
        {
            Stop();
            return false;
        }
    }

    public void Stop()
    {
        lock (_lock)
        {
            if (_watcher != null)
            {
                _watcher.EnableRaisingEvents = false;
                _watcher.Created -= OnEvent;
                _watcher.Deleted -= OnEvent;
                _watcher.Renamed -= OnRenamed;
                _watcher.Changed -= OnEvent;
                _watcher.Error -= OnError;
                _watcher.Dispose();
                _watcher = null;
            }
            _timer?.Dispose();
            _timer = null;
            _needsFullRescan = false;
        }
    }

    void OnEvent(object sender, FileSystemEventArgs e) => Schedule(false);
    void OnRenamed(object sender, RenamedEventArgs e) => Schedule(false);
    void OnError(object sender, ErrorEventArgs e) => Schedule(true);

    void Schedule(bool fullRescan)
    {
        lock (_lock)
        {
            if (_timer == null) return;
            if (fullRescan) _needsFullRescan = true;
            try { _timer.Change(DebounceMs, Timeout.Infinite); } catch (ObjectDisposedException) { }
        }
    }

    void Fire()
    {
        bool full;
        lock (_lock)
        {
            if (_timer == null) return;
            full = _needsFullRescan;
            _needsFullRescan = false;
        }
        try { Changed?.Invoke(full); } catch { /* subscriber errors must not kill the timer thread */ }
    }

    public void Dispose()
    {
        lock (_lock) _disposed = true;
        Stop();
    }
}
