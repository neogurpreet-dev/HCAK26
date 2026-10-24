using System;
using System.Collections.Generic;
using System.IO;
using System.Text.Json;

namespace Atlas;

/// <summary>
/// Reversible delete. Deleted items are MOVED (never copied-then-lost) into an ATLAS-managed trash folder under
/// %LOCALAPPDATA%\ATLAS\Trash\&lt;batch&gt;\ and recorded, with their original paths, in manifest.json. Undo restores the newest
/// batch first. Contents and folder structure are preserved because folders move as a whole. Restoring never overwrites:
/// if the original name is taken the item comes back under a "- Copy" name; if the original folder is gone the item stays in
/// the trash and the user gets a message. This is separate from the Windows Recycle Bin (items deleted this way are not in it).
/// </summary>
public static class AppTrash
{
    public sealed class Item
    {
        public string Original { get; set; } = "";
        public string Stored { get; set; } = "";
        public bool IsDir { get; set; }
    }

    public sealed class Batch
    {
        public string Id { get; set; } = "";
        public DateTime DeletedAtUtc { get; set; }
        public List<Item> Items { get; set; } = new();
    }

    static readonly object Gate = new();
    static List<Batch> _batches;

    public static string RootDir => Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "ATLAS", "Trash");
    static string ManifestPath => Path.Combine(RootDir, "manifest.json");

    public static bool CanUndo { get { lock (Gate) return Batches().Count > 0; } }

    static List<Batch> Batches()
    {
        if (_batches != null) return _batches;
        try { if (File.Exists(ManifestPath)) _batches = JsonSerializer.Deserialize<List<Batch>>(File.ReadAllText(ManifestPath)); }
        catch { _batches = null; }
        _batches ??= new List<Batch>();
        // forget entries whose stored copy has disappeared (user emptied the folder by hand)
        for (int b = _batches.Count - 1; b >= 0; b--)
        {
            var items = _batches[b].Items;
            for (int i = items.Count - 1; i >= 0; i--) if (!FileOps.PathExists(items[i].Stored)) items.RemoveAt(i);
            if (items.Count == 0) _batches.RemoveAt(b);
        }
        return _batches;
    }

    static void Save()
    {
        try
        {
            Directory.CreateDirectory(RootDir);
            string tmp = ManifestPath + ".tmp";
            File.WriteAllText(tmp, JsonSerializer.Serialize(_batches, new JsonSerializerOptions { WriteIndented = true }));
            File.Move(tmp, ManifestPath, true);
        }
        catch { /* the in-memory stack still works for this session */ }
    }

    /// <summary>Moves the items into the trash as one undoable batch. Safe to call on a worker thread.</summary>
    public static OpResult Delete(IReadOnlyList<string> paths)
    {
        var r = new OpResult();
        lock (Gate)
        {
            var list = Batches();
            string id = DateTime.UtcNow.ToString("yyyyMMddHHmmssfff") + "-" + Guid.NewGuid().ToString("N").Substring(0, 6);
            string dir = Path.Combine(RootDir, id);
            var batch = new Batch { Id = id, DeletedAtUtc = DateTime.UtcNow };
            string rootFull = Path.GetFullPath(RootDir).TrimEnd('\\', '/') + "\\";
            int n = 0;

            foreach (var raw in paths)
            {
                string src = raw.TrimEnd('\\', '/');
                string name = Path.GetFileName(src);
                string stored = null;
                string full = src;
                try
                {
                    full = Path.GetFullPath(src);
                    if (Path.GetDirectoryName(full) == null) { r.Errors.Add("a drive cannot be deleted"); continue; }
                    if ((full + "\\").StartsWith(rootFull, StringComparison.OrdinalIgnoreCase)) { r.Errors.Add("\"" + name + "\" is already in the ATLAS trash"); continue; }
                    bool isDir = Directory.Exists(full);
                    if (!isDir && !File.Exists(full)) { r.Errors.Add("\"" + name + "\" no longer exists"); continue; }

                    Directory.CreateDirectory(dir);
                    stored = Path.Combine(dir, (n++) + "_" + name);
                    FileOps.MoveTo(full, stored, isDir);
                    batch.Items.Add(new Item { Original = full, Stored = stored, IsDir = isDir });
                    r.Done++;
                }
                catch (Exception ex)
                {
                    // never remove a partial copy unless the original is still in place
                    if (stored != null && FileOps.PathExists(full) && FileOps.PathExists(stored))
                    {
                        try { if (Directory.Exists(stored)) Directory.Delete(stored, true); else File.Delete(stored); } catch { }
                    }
                    r.Errors.Add("\"" + name + "\": " + FileOps.Describe(ex));
                }
            }

            if (batch.Items.Count > 0) { list.Add(batch); Save(); }
            else { try { if (Directory.Exists(dir)) Directory.Delete(dir, false); } catch { } }
        }
        return r;
    }

    /// <summary>Restores the most recent batch. Items that cannot be restored safely stay in the trash. Safe on a worker thread.</summary>
    public static OpResult Undo(List<string> restoredPaths)
    {
        var r = new OpResult();
        lock (Gate)
        {
            var list = Batches();
            if (list.Count == 0) { r.Errors.Add("Nothing to undo."); return r; }
            var batch = list[list.Count - 1];

            for (int i = batch.Items.Count - 1; i >= 0; i--)
            {
                var it = batch.Items[i];
                string name = Path.GetFileName(it.Original.TrimEnd('\\', '/'));
                try
                {
                    if (!FileOps.PathExists(it.Stored)) { r.Errors.Add("\"" + name + "\" is no longer in the trash"); batch.Items.RemoveAt(i); continue; }
                    string parent = Path.GetDirectoryName(it.Original);
                    if (parent == null || !Directory.Exists(parent))
                    {
                        r.Errors.Add("the original folder of \"" + name + "\" no longer exists (" + parent + "), so it stays in the trash");
                        continue;
                    }
                    string target = it.Original;
                    if (FileOps.PathExists(target)) { target = FileOps.UniquePath(parent, name, it.IsDir, true); r.KeptBoth++; }
                    FileOps.MoveTo(it.Stored, target, it.IsDir);
                    restoredPaths?.Add(target);
                    r.Done++;
                    batch.Items.RemoveAt(i);
                }
                catch (Exception ex) { r.Errors.Add("\"" + name + "\": " + FileOps.Describe(ex)); }
            }

            if (batch.Items.Count == 0)
            {
                list.Remove(batch);
                try { string d = Path.Combine(RootDir, batch.Id); if (Directory.Exists(d)) Directory.Delete(d, false); } catch { }
            }
            Save();
        }
        return r;
    }
}
