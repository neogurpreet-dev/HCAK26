using System;
using System.Collections.Generic;
using System.IO;
using System.Runtime.InteropServices;
using System.Text;

namespace Atlas;

/// <summary>Outcome of a multi-item file operation.</summary>
public sealed class OpResult
{
    public int Done;
    public int Skipped;
    public int KeptBoth;
    public readonly List<string> Errors = new();
    public bool Ok => Errors.Count == 0;

    public string Summary(string verb)
    {
        var sb = new StringBuilder();
        if (Done == 0 && Skipped > 0 && Errors.Count == 0) return "Nothing to do: the items are already in this folder.";
        sb.Append(verb).Append(' ').Append(Done).Append(Done == 1 ? " item" : " items");
        if (KeptBoth > 0) sb.Append(" (").Append(KeptBoth).Append(" renamed so nothing was overwritten)");
        if (Errors.Count > 0)
        {
            sb.Append(". ").Append(Errors.Count).Append(" failed: ").Append(Errors[0]);
            if (Errors.Count > 1) sb.Append(" (+").Append(Errors.Count - 1).Append(" more)");
        }
        return sb.ToString();
    }
}

/// <summary>
/// Safe file operations behind the context menu. Rules: never overwrite silently (name clashes get " - Copy" names),
/// never build shell command lines from file names, delete via the Windows Recycle Bin by default, and turn every
/// exception into a readable message (no method here throws to its caller except where documented).
/// </summary>
public static class FileOps
{
    static readonly HashSet<string> Reserved = BuildReserved();

    static HashSet<string> BuildReserved()
    {
        var s = new HashSet<string>(StringComparer.OrdinalIgnoreCase) { "CON", "PRN", "AUX", "NUL" };
        for (int i = 1; i <= 9; i++) { s.Add("COM" + i); s.Add("LPT" + i); }
        return s;
    }

    // ------------------------------------------------------------------ names

    /// <summary>Returns an error message, or null when the name is a legal Windows file name.</summary>
    public static string ValidateName(string name)
    {
        if (string.IsNullOrWhiteSpace(name)) return "Enter a name.";
        if (name.Length > 255) return "That name is too long.";
        if (name == "." || name == "..") return "That name is not allowed.";
        if (name.IndexOfAny(Path.GetInvalidFileNameChars()) >= 0) return "A name cannot contain any of: \\ / : * ? \" < > |";
        if (name.EndsWith(" ") || name.EndsWith(".")) return "A name cannot end with a space or a period.";
        int dot = name.IndexOf('.');
        string stem = (dot >= 0 ? name.Substring(0, dot) : name).Trim();
        if (Reserved.Contains(stem)) return "\"" + stem + "\" is a reserved Windows name.";
        return null;
    }

    static bool Exists(string p) => File.Exists(p) || Directory.Exists(p);

    public static bool PathExists(string p) => !string.IsNullOrEmpty(p) && Exists(p);

    /// <summary>A path in <paramref name="dir"/> that does not exist yet: "name", then "name (2)" (or "name - Copy", "name - Copy (2)").</summary>
    public static string UniquePath(string dir, string name, bool isDir, bool copyStyle)
    {
        string stem = name, ext = "";
        if (!isDir)
        {
            ext = Path.GetExtension(name);
            stem = Path.GetFileNameWithoutExtension(name);
            if (stem.Length == 0) { stem = name; ext = ""; }
        }
        string candidate = Path.Combine(dir, name);
        if (!Exists(candidate)) return candidate;
        for (int i = 1; i < 10000; i++)
        {
            string suffix = copyStyle ? (i == 1 ? " - Copy" : " - Copy (" + i + ")") : " (" + (i + 1) + ")";
            candidate = Path.Combine(dir, stem + suffix + ext);
            if (!Exists(candidate)) return candidate;
        }
        throw new IOException("Could not find a free name.");
    }

    public static string Describe(Exception ex)
    {
        switch (ex)
        {
            case UnauthorizedAccessException: return "Access denied. You may not have permission, or the item is read-only.";
            case PathTooLongException: return "The path is too long.";
            case FileNotFoundException:
            case DirectoryNotFoundException: return "Item no longer exists.";
            case IOException io when (io.HResult & 0xFFFF) == 32 || (io.HResult & 0xFFFF) == 33: return "The item is in use by another program.";
            default: return ex.Message;
        }
    }

    // ------------------------------------------------------------------ rename / create

    static void MoveItem(string from, string to, bool isDir)
    {
        if (isDir) Directory.Move(from, to); else File.Move(from, to);
    }

    /// <summary>Renames in place. Refuses to replace an existing item. Returns an error message or null.</summary>
    public static string Rename(string path, string newName, out string newPath)
    {
        newPath = path;
        try
        {
            newName = newName?.Trim();
            string err = ValidateName(newName);
            if (err != null) return err;
            string src = path.TrimEnd('\\', '/');
            string parent = Path.GetDirectoryName(src);
            if (parent == null) return "A drive cannot be renamed here.";
            bool isDir = Directory.Exists(src);
            if (!isDir && !File.Exists(src)) return "Item no longer exists.";

            string target = Path.Combine(parent, newName);
            if (string.Equals(target, src, StringComparison.Ordinal)) return null; // unchanged
            bool caseOnly = string.Equals(target, src, StringComparison.OrdinalIgnoreCase);
            if (!caseOnly && Exists(target)) return "An item named \"" + newName + "\" already exists here.";

            if (caseOnly)
            {
                // Windows treats "a" and "A" as the same path, so go through a temporary name.
                string tmp = Path.Combine(parent, ".atlas-rename-" + Guid.NewGuid().ToString("N"));
                MoveItem(src, tmp, isDir);
                try { MoveItem(tmp, target, isDir); }
                catch { try { MoveItem(tmp, src, isDir); } catch { } throw; }
            }
            else MoveItem(src, target, isDir);
            newPath = target;
            return null;
        }
        catch (Exception ex) { return Describe(ex); }
    }

    public static string CreateFolder(string dir, string name, out string created)
    {
        created = null;
        try
        {
            name = name?.Trim();
            string err = ValidateName(name);
            if (err != null) return err;
            if (!Directory.Exists(dir)) return "The current folder no longer exists.";
            string p = Path.Combine(dir, name);
            if (Exists(p)) return "An item named \"" + name + "\" already exists here.";
            Directory.CreateDirectory(p);
            created = p;
            return null;
        }
        catch (Exception ex) { return Describe(ex); }
    }

    /// <summary>Creates an EMPTY file. FileMode.CreateNew guarantees an existing file is never truncated.</summary>
    public static string CreateFile(string dir, string name, out string created)
    {
        created = null;
        try
        {
            name = name?.Trim();
            string err = ValidateName(name);
            if (err != null) return err;
            if (!Directory.Exists(dir)) return "The current folder no longer exists.";
            string p = Path.Combine(dir, name);
            if (Exists(p)) return "An item named \"" + name + "\" already exists here.";
            using (new FileStream(p, FileMode.CreateNew, FileAccess.Write)) { }
            created = p;
            return null;
        }
        catch (Exception ex) { return Describe(ex); }
    }

    // ------------------------------------------------------------------ copy / move (paste)

    static bool IsSameOrInside(string candidate, string parent)
    {
        string a = Path.GetFullPath(candidate).TrimEnd('\\', '/') + "\\";
        string b = Path.GetFullPath(parent).TrimEnd('\\', '/') + "\\";
        return a.StartsWith(b, StringComparison.OrdinalIgnoreCase);
    }

    /// <summary>Copies/moves the sources into destDir. Never overwrites: a clash gets a "- Copy" name. Safe to call on a worker thread.</summary>
    public static OpResult Paste(IReadOnlyList<string> sources, bool cut, string destDir)
    {
        var r = new OpResult();
        try
        {
            if (!Directory.Exists(destDir)) { r.Errors.Add("The destination folder no longer exists."); return r; }
        }
        catch (Exception ex) { r.Errors.Add(Describe(ex)); return r; }

        string destFull = Path.GetFullPath(destDir).TrimEnd('\\', '/');
        foreach (var raw in sources)
        {
            string src = raw.TrimEnd('\\', '/');
            string name = Path.GetFileName(src);
            try
            {
                bool isDir = Directory.Exists(src);
                if (!isDir && !File.Exists(src)) { r.Errors.Add("\"" + name + "\" no longer exists"); continue; }
                if (isDir && IsSameOrInside(destFull, src)) { r.Errors.Add("cannot " + (cut ? "move" : "copy") + " \"" + name + "\" into itself"); continue; }

                bool sameDir = string.Equals(Path.GetDirectoryName(src), destFull, StringComparison.OrdinalIgnoreCase);
                if (cut && sameDir) { r.Skipped++; continue; }

                string target = Path.Combine(destFull, name);
                if (Exists(target))
                {
                    target = UniquePath(destFull, name, isDir, true);
                    if (!sameDir) r.KeptBoth++;
                }

                if (cut) MoveTo(src, target, isDir);
                else
                {
                    int skippedLinks = CopyTo(src, target, isDir);
                    if (skippedLinks > 0) r.Errors.Add("\"" + name + "\": " + skippedLinks + " link(s) were not copied");
                }
                r.Done++;
            }
            catch (Exception ex) { r.Errors.Add("\"" + name + "\": " + Describe(ex)); }
        }
        return r;
    }

    static int CopyTo(string src, string target, bool isDir)
    {
        if (!isDir) { File.Copy(src, target, false); return 0; }
        return CopyDirectory(src, target);
    }

    static void MoveTo(string src, string target, bool isDir)
    {
        if (!isDir) { File.Move(src, target); return; } // File.Move also works across drives
        string a = Path.GetPathRoot(Path.GetFullPath(src)), b = Path.GetPathRoot(Path.GetFullPath(target));
        if (string.Equals(a, b, StringComparison.OrdinalIgnoreCase)) { Directory.Move(src, target); return; }
        // different drive: copy everything first, delete the original only if every item was copied
        int skipped = CopyDirectory(src, target);
        if (skipped > 0) throw new IOException(skipped + " item(s) could not be copied, so the original was left in place");
        Directory.Delete(src, true);
    }

    /// <summary>Recursive copy that never follows links (junctions / symlinks are skipped and counted). Returns the number skipped.</summary>
    static int CopyDirectory(string src, string dst)
    {
        int skipped = 0;
        Directory.CreateDirectory(dst);
        var di = new DirectoryInfo(src);
        foreach (var f in di.EnumerateFiles())
        {
            if ((f.Attributes & FileAttributes.ReparsePoint) != 0) { skipped++; continue; }
            f.CopyTo(Path.Combine(dst, f.Name), false);
        }
        foreach (var d in di.EnumerateDirectories())
        {
            if ((d.Attributes & FileAttributes.ReparsePoint) != 0) { skipped++; continue; }
            skipped += CopyDirectory(d.FullName, Path.Combine(dst, d.Name));
        }
        return skipped;
    }

    // ------------------------------------------------------------------ delete

    [StructLayout(LayoutKind.Sequential, CharSet = CharSet.Unicode)]
    struct SHFILEOPSTRUCT
    {
        public IntPtr hwnd;
        public uint wFunc;
        [MarshalAs(UnmanagedType.LPWStr)] public string pFrom;
        [MarshalAs(UnmanagedType.LPWStr)] public string pTo;
        public ushort fFlags;
        [MarshalAs(UnmanagedType.Bool)] public bool fAnyOperationsAborted;
        public IntPtr hNameMappings;
        [MarshalAs(UnmanagedType.LPWStr)] public string lpszProgressTitle;
    }

    [DllImport("shell32.dll", CharSet = CharSet.Unicode, EntryPoint = "SHFileOperationW")]
    static extern int SHFileOperation(ref SHFILEOPSTRUCT op);

    const uint FO_DELETE = 3;
    const int FOF_NOCONFIRMATION = 0x10, FOF_ALLOWUNDO = 0x40, FOF_WANTNUKEWARNING = 0x4000;

    /// <summary>
    /// Moves items to the Windows Recycle Bin. FOF_WANTNUKEWARNING makes Windows warn (instead of silently deleting for good)
    /// when an item cannot be recycled, e.g. it is too large for the bin or lives on a network drive. Returns an error or null.
    /// </summary>
    public static string MoveToRecycleBin(IReadOnlyList<string> paths)
    {
        try
        {
            if (!OperatingSystem.IsWindows()) return "The Recycle Bin is only available on Windows.";
            var sb = new StringBuilder();
            int n = 0;
            foreach (var p in paths)
            {
                string full = Path.GetFullPath(p.TrimEnd('\\', '/'));
                if (!Exists(full)) continue;
                sb.Append(full).Append('\0');
                n++;
            }
            if (n == 0) return "Item no longer exists.";
            sb.Append('\0'); // double-null terminated list

            var op = new SHFILEOPSTRUCT
            {
                hwnd = IntPtr.Zero,
                wFunc = FO_DELETE,
                pFrom = sb.ToString(),
                fFlags = (ushort)(FOF_ALLOWUNDO | FOF_NOCONFIRMATION | FOF_WANTNUKEWARNING),
            };
            int rc = SHFileOperation(ref op);
            if (op.fAnyOperationsAborted) return "Delete cancelled.";
            return rc == 0 ? null : "Windows could not move the item to the Recycle Bin (error 0x" + rc.ToString("X") + ").";
        }
        catch (Exception ex) { return Describe(ex); }
    }

    /// <summary>Permanent delete (no Recycle Bin). The caller must have asked the user to confirm. Links are removed, never followed.</summary>
    public static OpResult DeletePermanently(IReadOnlyList<string> paths)
    {
        var r = new OpResult();
        foreach (var raw in paths)
        {
            string p = raw.TrimEnd('\\', '/');
            string name = Path.GetFileName(p);
            try
            {
                if (Directory.Exists(p))
                {
                    bool link = (new DirectoryInfo(p).Attributes & FileAttributes.ReparsePoint) != 0;
                    Directory.Delete(p, !link);
                }
                else if (File.Exists(p)) File.Delete(p);
                else { r.Errors.Add("\"" + name + "\" no longer exists"); continue; }
                r.Done++;
            }
            catch (Exception ex) { r.Errors.Add("\"" + name + "\": " + Describe(ex)); }
        }
        return r;
    }
}
