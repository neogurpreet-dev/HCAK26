using System;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.IO;
using System.Threading;
using System.Threading.Tasks;

namespace Atlas;

/// <summary>
/// Asynchronous recursive folder-size calculator with a cache, bounded concurrency and cancellation.
/// Reparse points (junctions / symlinks) are skipped entirely, which also rules out directory cycles.
/// Never touches Godot objects, so it is safe to run on worker threads.
/// </summary>
public sealed class FolderSizeService
{
    const int MaxFilesPerFolder = 750_000;
    static readonly TimeSpan Ttl = TimeSpan.FromMinutes(5);

    readonly ConcurrentDictionary<string, (long size, DateTime stamp)> _cache = new(StringComparer.OrdinalIgnoreCase);
    readonly SemaphoreSlim _gate = new(3);

    public bool TryGet(string path, out long size)
    {
        if (_cache.TryGetValue(path, out var v) && DateTime.UtcNow - v.stamp < Ttl) { size = v.size; return true; }
        size = -1;
        return false;
    }

    /// <summary>Drops cached values for a path and every ancestor (their totals changed too).</summary>
    public void Invalidate(string path)
    {
        try
        {
            var dir = path;
            while (!string.IsNullOrEmpty(dir))
            {
                _cache.TryRemove(dir, out _);
                dir = Path.GetDirectoryName(dir);
            }
        }
        catch { }
    }

    public void Clear() => _cache.Clear();

    public async Task<long> ComputeAsync(string path, CancellationToken ct)
    {
        if (TryGet(path, out var cached)) return cached;
        await _gate.WaitAsync(ct).ConfigureAwait(false);
        try
        {
            long total = await Task.Run(() => Walk(path, ct), ct).ConfigureAwait(false);
            _cache[path] = (total, DateTime.UtcNow);
            return total;
        }
        finally { _gate.Release(); }
    }

    static long Walk(string root, CancellationToken ct)
    {
        long total = 0;
        long files = 0;
        var opts = new EnumerationOptions
        {
            IgnoreInaccessible = true,
            RecurseSubdirectories = false,
            AttributesToSkip = FileAttributes.ReparsePoint, // include hidden/system, skip links
            ReturnSpecialDirectories = false,
        };
        var stack = new Stack<string>();
        stack.Push(root);
        while (stack.Count > 0)
        {
            ct.ThrowIfCancellationRequested();
            var dir = stack.Pop();
            try
            {
                var di = new DirectoryInfo(dir);
                foreach (var f in di.EnumerateFiles("*", opts))
                {
                    try { total += f.Length; } catch { }
                    if (++files > MaxFilesPerFolder) return total;
                }
                foreach (var d in di.EnumerateDirectories("*", opts)) stack.Push(d.FullName);
            }
            catch (OperationCanceledException) { throw; }
            catch { /* inaccessible or vanished: skip */ }
        }
        return total;
    }
}
