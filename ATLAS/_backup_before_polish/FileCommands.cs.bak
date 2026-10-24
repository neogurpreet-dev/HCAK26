using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Threading.Tasks;
using Godot;

namespace Atlas;

/// <summary>
/// Everything the right-click menu (and the matching keyboard shortcuts) can do. Builds the menu for what was clicked and runs
/// the commands. A menu is built from a snapshot of the targets (their paths), so an action always acts on the item that was
/// right-clicked, even if the selection changes before the user picks an entry.
///
/// Right-clicking never changes the selection: selection drives Ghost Links and the preview, and a right-click must not
/// trigger either. A right-click on a building that is part of a multi-selection targets the whole selection.
/// The file clipboard is internal to ATLAS (it does not interoperate with the Windows Explorer clipboard).
/// </summary>
public sealed class FileCommands
{
    public WorldGenerator Gen { get; set; }
    public WorldInteraction Inter { get; set; }
    public WorldCamera Cam { get; set; }
    public AtlasUI Ui { get; set; }
    public Func<string> CurrentPath { get; set; }
    public Action<FsEntry> OpenEntry { get; set; }
    public Action<bool> SetShowHidden { get; set; }
    public Action Refresh { get; set; }

    readonly List<string> _clip = new();
    bool _clipCut;
    bool _busy;

    sealed class Target
    {
        public readonly List<FsEntry> Entries = new();
        public string Folder;
    }

    // ------------------------------------------------------------------ entry points

    public void ShowContextMenu(Vector2 screenPos)
    {
        if (Ui?.Menu == null) return;
        var items = BuildMenu(BuildTarget(Inter?.BuildingAt(screenPos)));
        Ui.Menu.Open(screenPos, items);
    }

    /// <summary>Shift+F10 / Menu key: menu for the selected building (or the folder itself), anchored on screen where it is.</summary>
    public void ShowForKeyboard()
    {
        if (Ui?.Menu == null) return;
        var b = Inter?.Selected;
        if (b != null && !GodotObject.IsInstanceValid(b)) b = null;
        var vp = Cam.GetViewport().GetVisibleRect().Size;
        Vector2 pos = vp / 2f;
        if (b != null && Cam.Camera != null)
        {
            var wp = b.GlobalPosition + Vector3.Up * (b.Height * 0.5f);
            if (!Cam.Camera.IsPositionBehind(wp)) pos = Cam.Camera.UnprojectPosition(wp);
        }
        Ui.Menu.Open(pos, BuildMenu(BuildTarget(b)), true);
    }

    /// <summary>Keyboard shortcuts. Returns true when the key was used. The caller must not call this while a text field has focus.</summary>
    public bool HandleKey(InputEventKey k)
    {
        if ((k.Keycode == Key.F10 && k.ShiftPressed) || k.Keycode == Key.Menu) { ShowForKeyboard(); return true; }

        var sel = Inter?.SelectedEntries() ?? new List<FsEntry>();
        if (k.Keycode == Key.F2 && sel.Count == 1) { Rename(sel[0]); return true; }
        if (k.Keycode == Key.Delete && sel.Count > 0)
        {
            var paths = sel.Select(e => e.Path).ToList();
            if (k.ShiftPressed) ConfirmDeletePermanently(paths); else DeleteToRecycleBin(paths);
            return true;
        }
        if (k.CtrlPressed && !k.AltPressed)
        {
            switch (k.Keycode)
            {
                case Key.C: if (sel.Count > 0) { SetClipboard(sel.Select(e => e.Path), false); return true; } break;
                case Key.X: if (sel.Count > 0) { SetClipboard(sel.Select(e => e.Path), true); return true; } break;
                case Key.V: if (CanPaste()) { PasteInto(CurrentPath?.Invoke()); return true; } break;
                case Key.A: Inter?.SelectAll(); return true;
            }
        }
        return false;
    }

    // ------------------------------------------------------------------ menu construction

    Target BuildTarget(FileBuilding clicked)
    {
        var t = new Target { Folder = CurrentPath?.Invoke() };
        if (clicked != null && GodotObject.IsInstanceValid(clicked) && clicked.Entry != null)
        {
            var sel = Inter.SelectedEntries();
            if (sel.Count > 1 && Inter.IsInSelection(clicked)) t.Entries.AddRange(sel);
            else t.Entries.Add(clicked.Entry);
        }
        return t;
    }

    static GlassMenuItem Item(string text, string icon, Action act, string shortcut = null, bool danger = false, bool disabled = false, bool isChecked = false)
        => new GlassMenuItem { Text = text, Icon = icon, OnClick = act, Shortcut = shortcut, Danger = danger, Disabled = disabled, Checked = isChecked };

    static GlassMenuItem Sep() => GlassMenuItem.Separator();

    List<GlassMenuItem> BuildMenu(Target t)
    {
        var m = new List<GlassMenuItem>();
        if (t.Entries.Count == 0) EmptyMenu(m, t.Folder);
        else if (t.Entries.Count == 1) { var e = t.Entries[0]; if (e.IsDirectory) FolderMenu(m, e); else FileMenu(m, e); }
        else MultiMenu(m, t.Entries);
        return Tidy(m);
    }

    /// <summary>Removes leading, trailing and doubled separators so conditional items never leave stray lines.</summary>
    static List<GlassMenuItem> Tidy(List<GlassMenuItem> m)
    {
        var r = new List<GlassMenuItem>();
        foreach (var i in m)
        {
            if (i.IsSeparator && (r.Count == 0 || r[r.Count - 1].IsSeparator)) continue;
            r.Add(i);
        }
        while (r.Count > 0 && r[r.Count - 1].IsSeparator) r.RemoveAt(r.Count - 1);
        return r;
    }

    void FileMenu(List<GlassMenuItem> m, FsEntry e)
    {
        var paths = new List<string> { e.Path };
        m.Add(Item("Open", "external", () => OpenEntry?.Invoke(e)));
        m.Add(Item("Open with…", "app", () => Report(ShellLite.OpenWith(e.Path), null)));
        m.Add(Sep());
        m.Add(Item("Cut", "cut", () => SetClipboard(paths, true), "Ctrl+X"));
        m.Add(Item("Copy", "copy", () => SetClipboard(paths, false), "Ctrl+C"));
        m.Add(Sep());
        m.Add(Item("Copy name", null, () => CopyText(e.Name, "Copied the name.")));
        m.Add(Item("Copy path", null, () => CopyText(e.Path, "Copied the path.")));
        m.Add(Sep());
        m.Add(Item("Rename", "rename", () => Rename(e), "F2"));
        m.Add(Item("Delete", "trash", () => DeleteToRecycleBin(paths), "Del", danger: true));
        m.Add(Item("Delete permanently…", "trash", () => ConfirmDeletePermanently(paths), "Shift+Del", danger: true));
        m.Add(Sep());
        m.Add(Item("Reveal in File Explorer", "folder", () => Report(ShellLite.Reveal(e.Path), null)));
        m.Add(Item("Properties", "info", () => Report(ShellLite.ShowProperties(e.Path), null)));
    }

    void FolderMenu(List<GlassMenuItem> m, FsEntry e)
    {
        var paths = new List<string> { e.Path };
        m.Add(Item("Open", "external", () => OpenEntry?.Invoke(e)));
        m.Add(Sep());
        m.Add(Item("Cut", "cut", () => SetClipboard(paths, true), "Ctrl+X"));
        m.Add(Item("Copy", "copy", () => SetClipboard(paths, false), "Ctrl+C"));
        if (CanPaste()) m.Add(Item("Paste into folder", "paste", () => PasteInto(e.Path)));
        m.Add(Sep());
        m.Add(Item("Copy path", null, () => CopyText(e.Path, "Copied the path.")));
        m.Add(Sep());
        m.Add(Item("Rename", "rename", () => Rename(e), "F2"));
        m.Add(Item("Delete", "trash", () => DeleteToRecycleBin(paths), "Del", danger: true));
        m.Add(Item("Delete permanently…", "trash", () => ConfirmDeletePermanently(paths), "Shift+Del", danger: true));
        m.Add(Sep());
        m.Add(Item("Open terminal here", "terminal", () => Report(ShellLite.OpenTerminal(e.Path), null)));
        m.Add(Item("Reveal in File Explorer", "folder", () => Report(ShellLite.Reveal(e.Path), null)));
        m.Add(Item("Properties", "info", () => Report(ShellLite.ShowProperties(e.Path), null)));
    }

    void EmptyMenu(List<GlassMenuItem> m, string folder)
    {
        if (string.IsNullOrEmpty(folder)) return;
        var newMenu = Item("New", "plus", null);
        newMenu.Children = new List<GlassMenuItem>
        {
            Item("Folder", "newfolder", () => NewItem(folder, true, "New folder")),
            Item("Text document", "newfile", () => NewItem(folder, false, "New Text Document.txt")),
            Item("Markdown document", "newfile", () => NewItem(folder, false, "New Document.md")),
        };
        m.Add(newMenu);
        if (CanPaste()) m.Add(Item("Paste", "paste", () => PasteInto(folder), "Ctrl+V"));
        m.Add(Item("Select all", "select", () => Inter?.SelectAll(), "Ctrl+A"));
        m.Add(Sep());
        m.Add(Item("Refresh", "refresh", () => Refresh?.Invoke()));
        bool hidden = Gen != null && Gen.ShowHidden;
        m.Add(Item("Show hidden files", "eye", () => SetShowHidden?.Invoke(!hidden), isChecked: hidden));
        m.Add(Sep());
        m.Add(Item("Open terminal here", "terminal", () => Report(ShellLite.OpenTerminal(folder), null)));
        m.Add(Item("Open in File Explorer", "external", () => Report(ShellLite.OpenFolderInExplorer(folder), null)));
        m.Add(Item("Properties", "info", () => Report(ShellLite.ShowProperties(folder), null)));
    }

    void MultiMenu(List<GlassMenuItem> m, List<FsEntry> entries)
    {
        var paths = entries.Select(e => e.Path).ToList();
        var list = entries.ToList();
        m.Add(GlassMenuItem.Header(entries.Count + " items selected"));
        // opening many things at once is only offered for a handful of plain files; folders cannot all be entered at once
        if (entries.All(e => !e.IsDirectory) && entries.Count <= 10)
            m.Add(Item("Open", "external", () => { foreach (var e in list) OpenEntry?.Invoke(e); }));
        m.Add(Sep());
        m.Add(Item("Cut", "cut", () => SetClipboard(paths, true), "Ctrl+X"));
        m.Add(Item("Copy", "copy", () => SetClipboard(paths, false), "Ctrl+C"));
        m.Add(Sep());
        m.Add(Item("Copy paths", null, () => CopyText(string.Join("\n", paths), "Copied " + paths.Count + " paths.")));
        m.Add(Sep());
        m.Add(Item("Delete", "trash", () => DeleteToRecycleBin(paths), "Del", danger: true));
        m.Add(Item("Delete permanently…", "trash", () => ConfirmDeletePermanently(paths), "Shift+Del", danger: true));
    }

    // ------------------------------------------------------------------ commands

    void Report(string err, string ok)
    {
        if (err != null) Ui.Notify(err, true);
        else if (ok != null) Ui.Notify(ok);
    }

    void CopyText(string text, string message)
    {
        DisplayServer.ClipboardSet(text);
        Ui.Notify(message);
    }

    void SetClipboard(IEnumerable<string> paths, bool cut)
    {
        _clip.Clear();
        _clip.AddRange(paths);
        _clipCut = cut;
        int n = _clip.Count;
        Ui.Notify((cut ? "Cut " : "Copied ") + n + (n == 1 ? " item" : " items") + (cut ? ". Paste to move." : ". Paste to copy."));
    }

    bool CanPaste() => _clip.Count > 0 && _clip.Any(FileOps.PathExists);

    void PasteInto(string destDir)
    {
        if (string.IsNullOrEmpty(destDir) || _clip.Count == 0) return;
        if (_busy) { Ui.Notify("Another file operation is still running."); return; }
        var sources = _clip.ToList();
        bool cut = _clipCut;
        _busy = true;
        Ui.Notify((cut ? "Moving " : "Copying ") + sources.Count + (sources.Count == 1 ? " item…" : " items…"));
        Task.Run(() =>
        {
            OpResult r;
            try { r = FileOps.Paste(sources, cut, destDir); }
            catch (Exception ex) { r = new OpResult(); r.Errors.Add(FileOps.Describe(ex)); }
            Callable.From(() => OnPasted(r, cut)).CallDeferred();
        });
    }

    void OnPasted(OpResult r, bool cut)
    {
        _busy = false;
        if (cut) _clip.RemoveAll(p => !FileOps.PathExists(p)); // moved items are gone from their old place; failures stay available
        Ui.Notify(r.Summary(cut ? "Moved" : "Copied"), !r.Ok);
    }

    void DeleteToRecycleBin(IReadOnlyList<string> paths)
    {
        if (paths.Count == 0) return;
        string err = FileOps.MoveToRecycleBin(paths);
        if (err != null) Ui.Notify(err, true);
        else Ui.Notify("Moved " + paths.Count + (paths.Count == 1 ? " item" : " items") + " to the Recycle Bin.");
    }

    void ConfirmDeletePermanently(IReadOnlyList<string> paths)
    {
        if (paths.Count == 0) return;
        var copy = paths.ToList();
        string what = copy.Count == 1
            ? "\"" + Path.GetFileName(copy[0].TrimEnd('\\', '/')) + "\""
            : copy.Count + " items";
        Ui.Dialog.Confirm("Delete permanently",
            "Permanently delete " + what + "? This skips the Recycle Bin and cannot be undone.",
            "Delete permanently", true,
            () =>
            {
                var r = FileOps.DeletePermanently(copy);
                Ui.Notify(r.Summary("Permanently deleted"), !r.Ok);
            });
    }

    void Rename(FsEntry e)
    {
        string src = e.Path.TrimEnd('\\', '/');
        string parent = Path.GetDirectoryName(src);
        Ui.Dialog.Prompt("Rename", "New name for \"" + e.Name + "\":", e.Name, "Rename",
            text =>
            {
                string n = text?.Trim();
                if (n == e.Name) return null;
                string err = FileOps.ValidateName(n);
                if (err != null) return err;
                if (parent != null)
                {
                    string target = Path.Combine(parent, n);
                    bool sameItem = string.Equals(target, src, StringComparison.OrdinalIgnoreCase);
                    if (!sameItem && FileOps.PathExists(target)) return "An item named \"" + n + "\" already exists here.";
                }
                return null;
            },
            text =>
            {
                string err = FileOps.Rename(src, text, out _);
                Report(err, "Renamed to \"" + text.Trim() + "\".");
            },
            !e.IsDirectory);
    }

    void NewItem(string folder, bool isFolder, string defaultName)
    {
        string initial;
        try { initial = Path.GetFileName(FileOps.UniquePath(folder, defaultName, isFolder, false)); }
        catch (Exception ex) { Ui.Notify(FileOps.Describe(ex), true); return; }

        Ui.Dialog.Prompt(isFolder ? "New folder" : "New file", "Name:", initial, "Create",
            text =>
            {
                string n = text?.Trim();
                string err = FileOps.ValidateName(n);
                if (err != null) return err;
                return FileOps.PathExists(Path.Combine(folder, n)) ? "An item named \"" + n + "\" already exists here." : null;
            },
            text =>
            {
                string err = isFolder ? FileOps.CreateFolder(folder, text, out _) : FileOps.CreateFile(folder, text, out _);
                Report(err, "Created \"" + text.Trim() + "\".");
            },
            !isFolder);
    }
}
