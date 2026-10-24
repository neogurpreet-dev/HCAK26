using System;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.IO;
using System.Threading;
using System.Threading.Tasks;

namespace ClashExplorer.Services;

/// <summary>
/// Asynchronous recursive folder-size calculation with caching, request de-duplication,
/// bounded concurrency and cancellation. Contains no Godot types, so it is safe on any thread.
///
/// Safety rules: symbolic links and junctions are never followed (no cycles, no double counting),
/// inaccessible entries are skipped, and a hard entry limit stops runaway scans (the partial
/// total is returned).
/// </summary>
public sealed class FolderSizeCalculator : IDisposable
{
    private sealed record CacheEntry(long Size, DateTime FolderWriteUtc, DateTime ComputedUtc);

    private readonly ConcurrentDictionary<string, CacheEntry> _cache = new(StringComparer.OrdinalIgnoreCase);
    private readonly ConcurrentDictionary<string, Task<long>> _inflight = new(StringComparer.OrdinalIgnoreCase);
    private readonly SemaphoreSlim _gate;
    private readonly object _ctsLock = new();
    private CancellationTokenSource _cts = new();
    private bool _disposed;

    /// <summary>A cached size stays valid while the folder's own last-write time is unchanged and it is younger than this.</summary>
    public TimeSpan CacheLifetime { get; set; } = TimeSpan.FromMinutes(10);

    /// <summary>Upper bound on entries visited for a single folder.</summary>
    public int MaxEntriesScanned { get; set; } = 3_000_000;

    public FolderSizeCalculator(int maxConcurrency = 2)
    {
        _gate = new SemaphoreSlim(Math.Max(1, maxConcurrency));
    }

    public bool TryGetCached(string path, DateTime folderWriteUtc, out long size)
    {
        if (_cache.TryGetValue(path, out CacheEntry? entry)
            && entry.FolderWriteUtc == folderWriteUtc
            && DateTime.UtcNow - entry.ComputedUtc < CacheLifetime)
        {
            size = entry.Size;
            return true;
        }
        size = -1;
        return false;
    }

    /// <summary>
    /// Returns the cached size or computes it in the background. Concurrent requests for the same
    /// folder share one computation. Throws OperationCanceledException after CancelPending().
    /// </summary>
    public Task<long> GetSizeAsync(string path, DateTime folderWriteUtc)
    {
        if (_disposed) return Task.FromCanceled<long>(new CancellationToken(true));
        if (TryGetCached(path, folderWriteUtc, out long cached)) return Task.FromResult(cached);

        CancellationToken token;
        lock (_ctsLock)
        {
            token = _cts.Token;
        }
        return _inflight.GetOrAdd(path, p => Task.Run(() => RunAsync(p, folderWriteUtc, token)));
    }

    /// <summary>Cancels every running or queued calculation. Finished results stay cached.</summary>
    public void CancelPending()
    {
        CancellationTokenSource old;
        lock (_ctsLock)
        {
            old = _cts;
            _cts = new CancellationTokenSource();
        }
        old.Cancel();
    }

    /// <summary>Forgets the cached size of a path and of all its parent folders.</summary>
    public void Invalidate(string path)
    {
        string? current = path;
        while (!string.IsNullOrEmpty(current))
        {
            _cache.TryRemove(current, out _);
            current = Path.GetDirectoryName(current);
        }
    }

    public void Clear() => _cache.Clear();

    public void Dispose()
    {
        if (_disposed) return;
        _disposed = true;
        CancelPending();
        _cache.Clear();
    }

    private async Task<long> RunAsync(string path, DateTime folderWriteUtc, CancellationToken token)
    {
        try
        {
            await _gate.WaitAsync(token).ConfigureAwait(false);
            try
            {
                long size = await Task.Run(() => Compute(path, token), token).ConfigureAwait(false);
                _cache[path] = new CacheEntry(size, folderWriteUtc, DateTime.UtcNow);
                return size;
            }
            finally
            {
                _gate.Release();
            }
        }
        finally
        {
            _inflight.TryRemove(path, out _);
        }
    }

    private long Compute(string root, CancellationToken token)
    {
        try
        {
            var rootInfo = new DirectoryInfo(root);
            if ((rootInfo.Attributes & FileAttributes.ReparsePoint) != 0) return 0;
        }
        catch (Exception ex) when (ex is IOException || ex is UnauthorizedAccessException)
        {
            return 0;
        }

        var options = new EnumerationOptions
        {
            IgnoreInaccessible = true,
            RecurseSubdirectories = false,
            ReturnSpecialDirectories = false,
            AttributesToSkip = 0
        };

        long total = 0;
        long scanned = 0;
        var pending = new Stack<string>();
        pending.Push(root);

        while (pending.Count > 0)
        {
            token.ThrowIfCancellationRequested();
            string dir = pending.Pop();
            try
            {
                foreach (FileSystemInfo info in new DirectoryInfo(dir).EnumerateFileSystemInfos("*", options))
                {
                    scanned++;
                    if (scanned > MaxEntriesScanned) return total;
                    if ((scanned & 1023) == 0) token.ThrowIfCancellationRequested();

                    if ((info.Attributes & FileAttributes.Directory) != 0)
                    {
                        if ((info.Attributes & FileAttributes.ReparsePoint) == 0) pending.Push(info.FullName);
                    }
                    else if (info is FileInfo file)
                    {
                        try
                        {
                            total += file.Length;
                        }
                        catch (Exception ex) when (ex is IOException || ex is UnauthorizedAccessException)
                        {
                            // File vanished or is locked: skip it.
                        }
                    }
                }
            }
            catch (Exception ex) when (ex is IOException || ex is UnauthorizedAccessException
                                       || ex is System.Security.SecurityException)
            {
                // Directory vanished or is not accessible: skip it.
            }
        }
        return total;
    }
}
