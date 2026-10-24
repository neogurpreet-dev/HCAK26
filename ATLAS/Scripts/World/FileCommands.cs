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
///
/// Delete is REVERSIBLE: items are moved into the ATLAS trash (AppTrash) so "Undo delete" (empty-space menu or Ctrl+Z) can put
/// them back. They are therefore not in the Windows Recycle Bin. "Delete permanently" still bypasses everything after a
/// confirmation. The old Recycle Bin path is kept behind <see cref="UseAppTrash"/>.
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

    /// <summary>true = Delete moves items to the ATLAS trash (undoable); false = Windows Recycle Bin (not undoable from ATLAS).</summary>
    public static readonly bool UseAppTrash = true;

    readonly List<string> _clip = new();
    bool _clipCut;
    bool _busy;
    FileBuilding _ctxLifted;

    sealed class Target
    {
        public readonly List<FsEntry> Entries = new();
        public string Folder;
    }

    // ------------------------------------------------------------------ entry points

    public void ShowContextMenu(Vector2 screenPos)
    {
        if (Ui?.Menu == null) return;
        var b = Inter?.BuildingAt(screenPos);
        var items = BuildMenu(BuildTarget(b));
        Ui.Menu.Open(screenPos, items);   // closes any menu that was open before (and lowers its building)
        ClearContextLift();
        if (b != null && Ui.Menu.IsOpen && GodotObject.IsInstanceValid(b)) { _ctxLifted = b; b.SetContextLift(true); }
    }

    /// <summary>Lowers the building that was raised for the context menu (the selected building stays raised through its selection).</summary>
    public void ClearContextLift()
    {
        if (_ctxLifted != null && GodotObject.IsInstanceValid(_ctxLifted)) _ctxLifted.SetContextLift(false);
        _ctxLifted = null;
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

    // ------------------------------------------------------------------ keyboard commands (bound in ShortcutManager)

    List<FsEntry> Selection() => Inter?.SelectedEntries() ?? new List<FsEntry>();

    public void RenameSelection()
    {
        var sel = Selection();
        if (sel.Count == 1) Rename(sel[0]);
    }

    public void CopySelection(bool cut)
    {
        var sel = Selection();
        if (sel.Count > 0) SetClipboard(sel.Select(e => e.Path), cut);
    }

    public void PasteHere()
    {
        if (CanPaste()) PasteInto(CurrentPath?.Invoke());
    }

    /// <summary>Delete = reversible delete (see class comment); Shift+Delete = permanent delete after a confirmation.</summary>
    public void DeleteSelection(bool permanent)
    {
        var paths = Selection().Select(e => e.Path).ToList();
        if (paths.Count == 0) return;
        if (permanent) ConfirmDeletePermanently(paths); else DeleteReversible(paths);
    }

    /// <summary>Enter: a file opens in its default app, a folder is entered. With several files selected they all open (max 10).</summary>
    public void OpenSelection()
    {
        var sel = Selection();
        if (sel.Count == 0) return;
        if (sel.Count == 1) { OpenEntry?.Invoke(sel[0]); return; }
        if (sel.All(e => !e.IsDirectory) && sel.Count <= 10) { foreach (var e in sel) OpenEntry?.Invoke(e); return; }
        Ui.Notify("Select a single folder to open it (or up to 10 files).");
    }

    /// <summary>Alt+Enter: the native Windows Properties dialog for the selected item, or for the current folder when nothing is selected.</summary>
    public void PropertiesSelection()
    {
        var sel = Selection();
        if (sel.Count > 1) { Ui.Notify("Properties works on one item at a time."); return; }
        string path = sel.Count == 1 ? sel[0].Path : CurrentPath?.Invoke();
        if (!string.IsNullOrEmpty(path)) Report(ShellLite.ShowProperties(path), null);
    }

    /// <summary>Ctrl+Shift+N: the same "New folder" flow as the menu (name prompt, then create).</summary>
    public void NewFolderHere()
    {
        string folder = CurrentPath?.Invoke();
        if (!string.IsNullOrEmpty(folder)) NewItem(folder, true, "New folder");
    }

    public void SelectAllItems() => Inter?.SelectAll();
    public void UndoDeleteCommand() => UndoDelete();

    /// <summary>Registers every file shortcut. Modifiers must match exactly (Delete is not Shift+Delete).</summary>
    public void RegisterShortcuts(ShortcutManager sm)
    {
        var K = (Func<Key, bool, bool, bool, InputEventKey>)((key, c, sh, a) => ShortcutManager.K(key, c, sh, a));
        sm.Bind("atlas_rename", RenameSelection, K(Key.F2, false, false, false));
        sm.Bind("atlas_copy", () => CopySelection(false), K(Key.C, true, false, false));
        sm.Bind("atlas_cut", () => CopySelection(true), K(Key.X, true, false, false));
        sm.Bind("atlas_paste", PasteHere, K(Key.V, true, false, false));
        sm.Bind("atlas_delete", () => DeleteSelection(false), K(Key.Delete, false, false, false));
        sm.Bind("atlas_delete_permanent", () => DeleteSelection(true), K(Key.Delete, false, true, false));
        sm.Bind("atlas_open", OpenSelection, K(Key.Enter, false, false, false), K(Key.KpEnter, false, false, false));
        sm.Bind("atlas_properties", PropertiesSelection, K(Key.Enter, false, false, true), K(Key.KpEnter, false, false, true));
        sm.Bind("atlas_new_folder", NewFolderHere, K(Key.N, true, true, false));
        sm.Bind("atlas_select_all", SelectAllItems, K(Key.A, true, false, false));
        sm.Bind("atlas_undo_delete", UndoDeleteCommand, K(Key.Z, true, false, false));
        sm.Bind("atlas_context_menu", ShowForKeyboard, K(Key.F10, false, true, false), K(Key.Menu, false, false, false));
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
        m.Add(Item("Open with…", "app", () => OpenWith(e)));
        m.Add(Sep());
        m.Add(Item("Cut", "cut", () => SetClipboard(paths, true), "Ctrl+X"));
        m.Add(Item("Copy", "copy", () => SetClipboard(paths, false), "Ctrl+C"));
        m.Add(Sep());
        m.Add(Item("Copy name", null, () => CopyText(e.Name, "Copied the name.")));
        m.Add(Item("Copy path", null, () => CopyText(e.Path, "Copied the path.")));
        m.Add(Sep());
        m.Add(Item("Rename", "rename", () => Rename(e), "F2"));
        m.Add(Item("Delete", "trash", () => DeleteReversible(paths), "Del", danger: true));
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
        m.Add(Item("Delete", "trash", () => DeleteReversible(paths), "Del", danger: true));
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
            Item("Shortcut", "link", () => NewShortcut(folder)),
        };
        foreach (var kind in FileTemplates.Kinds)
        {
            var k = kind;
            newMenu.Children.Add(Item(k.Label, k.Icon, () => NewTemplate(folder, k)));
        }
        m.Add(newMenu);
        if (CanPaste()) m.Add(Item("Paste", "paste", () => PasteInto(folder), "Ctrl+V"));
        m.Add(Item("Undo delete", "undo", UndoDelete, "Ctrl+Z", disabled: !AppTrash.CanUndo));
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
        m.Add(Item("Delete", "trash", () => DeleteReversible(paths), "Del", danger: true));
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

    void DeleteReversible(IReadOnlyList<string> paths)
    {
        if (paths.Count == 0) return;
        if (!UseAppTrash)
        {
            string err = FileOps.MoveToRecycleBin(paths);
            if (err != null) Ui.Notify(err, true);
            else Ui.Notify("Moved " + paths.Count + (paths.Count == 1 ? " item" : " items") + " to the Recycle Bin.");
            return;
        }
        if (_busy) { Ui.Notify("Another file operation is still running."); return; }
        var copy = paths.ToList();
        _busy = true;
        Task.Run(() =>
        {
            OpResult r;
            try { r = AppTrash.Delete(copy); }
            catch (Exception ex) { r = new OpResult(); r.Errors.Add(FileOps.Describe(ex)); }
            Callable.From(() => OnTrashed(r)).CallDeferred();
        });
    }

    void OnTrashed(OpResult r)
    {
        _busy = false;
        if (r.Ok && r.Done > 0)
            Ui.Notify("Deleted " + r.Done + (r.Done == 1 ? " item" : " items") + ". Undo delete (Ctrl+Z) restores " + (r.Done == 1 ? "it." : "them."));
        else
            Ui.Notify(r.Summary("Deleted"), !r.Ok);
    }

    /// <summary>Restores the most recent delete (newest first, one batch per call).</summary>
    void UndoDelete()
    {
        if (!AppTrash.CanUndo) { Ui.Notify("Nothing to undo."); return; }
        if (_busy) { Ui.Notify("Another file operation is still running."); return; }
        _busy = true;
        Task.Run(() =>
        {
            var restored = new List<string>();
            OpResult r;
            try { r = AppTrash.Undo(restored); }
            catch (Exception ex) { r = new OpResult(); r.Errors.Add(FileOps.Describe(ex)); }
            Callable.From(() => OnUndone(r, restored)).CallDeferred();
        });
    }

    void OnUndone(OpResult r, List<string> restored)
    {
        _busy = false;
        Ui.Notify(r.Summary("Restored"), !r.Ok);
        string cur = CurrentPath?.Invoke()?.TrimEnd('\\', '/');
        if (cur != null && restored.Any(p => string.Equals(Path.GetDirectoryName(p), cur, StringComparison.OrdinalIgnoreCase)))
            Refresh?.Invoke();   // the restored items belong in the folder on screen: show them straight away
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

    // ------------------------------------------------------------------ New > file types / shortcut, Open with

    /// <summary>Asks for a name (pre-filled, stem selected) and creates a real, valid file of the chosen type.</summary>
    void NewTemplate(string folder, FileTemplates.Kind kind)
    {
        string initial;
        try { initial = Path.GetFileName(FileOps.UniquePath(folder, kind.DefaultName, false, false)); }
        catch (Exception ex) { Ui.Notify(FileOps.Describe(ex), true); return; }

        Ui.Dialog.Prompt("New " + kind.Label, "File name:", initial, "Create",
            text =>
            {
                string n = FileTemplates.WithExtension(text, kind.Ext);
                string err = FileOps.ValidateName(n);
                if (err != null) return err;
                return FileOps.PathExists(Path.Combine(folder, n)) ? "An item named \"" + n + "\" already exists here." : null;
            },
            text =>
            {
                string err = FileTemplates.Create(folder, text, kind.Ext, out string created);
                Report(err, created != null ? "Created \"" + Path.GetFileName(created) + "\"." : null);
            },
            true);
    }

    void NewShortcut(string folder)
    {
        string defaultTarget = "";
        var sel = Inter?.SelectedEntries();
        if (sel != null && sel.Count == 1) defaultTarget = sel[0].Path;

        var fields = new[]
        {
            new FormField
            {
                Label = "Target (a file or folder):", Value = defaultTarget, Placeholder = "C:\\path\\to\\item",
                Browse = setter => BrowseNative("Choose the shortcut target", DisplayServer.FileDialogMode.OpenAny, "*.* ; All items", setter),
            },
            new FormField { Label = "Shortcut name:", Value = ShortcutName(defaultTarget), Derive = v => ShortcutName(v[0]) },
        };
        Ui.Dialog.Form("Create shortcut", "Create a Windows shortcut (.lnk) in this folder.", "Create", fields,
            v => ShortcutMaker.Validate(v[0], v[1], folder),
            v =>
            {
                string err = ShortcutMaker.Create(folder, v[1], v[0], out string created);
                Report(err, created != null ? "Created shortcut \"" + Path.GetFileName(created) + "\"." : null);
            });
    }

    static string ShortcutName(string target)
    {
        string t = (target ?? "").Trim().Trim('"').TrimEnd('\\', '/');
        if (t.Length == 0) return "";
        string name = Path.GetFileName(t);
        if (name.Length == 0) name = t.Replace(":", "");
        return name + " - Shortcut";
    }

    void OpenWith(FsEntry e)
    {
        var apps = AppCatalog.For(e.Path);
        Ui.Dialog.PickApp("Open with", "Choose a program to open \"" + e.Name + "\":", apps,
            app => Report(ShellLite.OpenWithApp(app.Exe, e.Path), "Opened with " + app.Name + "."),
            () => BrowseNative("Choose a program", DisplayServer.FileDialogMode.OpenFile, "*.exe ; Programs", exe =>
            {
                if (!exe.EndsWith(".exe", StringComparison.OrdinalIgnoreCase) || !File.Exists(exe)) { Ui.Notify("Please choose a program (.exe).", true); return; }
                Ui.Dialog.AddApp(new AppChoice { Name = WinShell.DisplayName(exe), Exe = exe });
            }),
            () => Report(ShellLite.OpenWith(e.Path), null));   // native Windows "Open with" as the fallback
    }

    /// <summary>Native Windows file chooser; the chosen path is handed to <paramref name="picked"/> on the main thread.</summary>
    void BrowseNative(string title, DisplayServer.FileDialogMode mode, string filter, Action<string> picked)
    {
        var err = DisplayServer.FileDialogShow(title, "", "", false, mode, new[] { filter },
            Callable.From<bool, string[], long>((ok, paths, filterIndex) =>
            {
                if (!ok || paths == null || paths.Length == 0) return;
                string chosen = paths[0];
                Callable.From(() => picked(chosen)).CallDeferred();
            }));
        if (err != Error.Ok) Ui.Notify("The Windows file chooser could not be opened.", true);
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
