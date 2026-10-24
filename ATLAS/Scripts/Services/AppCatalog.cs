using System;
using System.Collections.Generic;
using System.IO;
using Microsoft.Win32;

namespace Atlas;

/// <summary>Suggests programs for the "Open with" dialog: the file's default handler, programs Windows remembers for the extension, and a few common ones.</summary>
public static class AppCatalog
{
    const int Max = 12;

    public static List<AppChoice> For(string filePath)
    {
        var list = new List<AppChoice>();
        var seen = new HashSet<string>(StringComparer.OrdinalIgnoreCase);

        void Add(string exe)
        {
            try
            {
                if (list.Count >= Max || string.IsNullOrWhiteSpace(exe) || !File.Exists(exe)) return;
                string full = Path.GetFullPath(exe);
                if (!full.EndsWith(".exe", StringComparison.OrdinalIgnoreCase) || !seen.Add(full)) return;
                list.Add(new AppChoice { Name = WinShell.DisplayName(full), Exe = full });
            }
            catch { }
        }

        string ext = "";
        try { ext = Path.GetExtension(filePath) ?? ""; } catch { }

        Add(WinShell.GetDefaultHandler(filePath));
        if (OperatingSystem.IsWindows() && ext.Length > 0)
        {
            foreach (var name in RememberedPrograms(ext)) Add(Resolve(name));
        }

        string sys = Environment.GetFolderPath(Environment.SpecialFolder.System);
        string local = Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData);
        string pf = Environment.GetFolderPath(Environment.SpecialFolder.ProgramFiles);
        Add(Path.Combine(sys, "notepad.exe"));
        switch (ext.ToLowerInvariant())
        {
            case ".png": case ".jpg": case ".jpeg": case ".bmp": case ".gif": case ".tif": case ".tiff":
                Add(Path.Combine(sys, "mspaint.exe")); break;
        }
        Add(Path.Combine(local, "Programs", "Microsoft VS Code", "Code.exe"));
        Add(Path.Combine(pf, "Microsoft VS Code", "Code.exe"));
        return list;
    }

    static IEnumerable<string> RememberedPrograms(string ext)
    {
        var names = new List<string>();
        try
        {
            if (!OperatingSystem.IsWindows()) return names;
            using (var k = Registry.CurrentUser.OpenSubKey(@"Software\Microsoft\Windows\CurrentVersion\Explorer\FileExts\" + ext + @"\OpenWithList"))
            {
                if (k != null)
                    foreach (var v in k.GetValueNames())
                        if (v != "MRUList" && k.GetValue(v) is string s && s.EndsWith(".exe", StringComparison.OrdinalIgnoreCase)) names.Add(s);
            }
            using (var k = Registry.ClassesRoot.OpenSubKey(ext + @"\OpenWithList"))
            {
                if (k != null) foreach (var sub in k.GetSubKeyNames()) if (sub.EndsWith(".exe", StringComparison.OrdinalIgnoreCase)) names.Add(sub);
            }
        }
        catch { }
        return names;
    }

    /// <summary>Turns "wordpad.exe" into a full path using the App Paths registry keys or the Windows folders.</summary>
    static string Resolve(string exeName)
    {
        try
        {
            if (Path.IsPathRooted(exeName)) return exeName;
            if (OperatingSystem.IsWindows())
            {
                foreach (var hive in new[] { Registry.CurrentUser, Registry.LocalMachine })
                {
                    using var k = hive.OpenSubKey(@"SOFTWARE\Microsoft\Windows\CurrentVersion\App Paths\" + exeName);
                    if (k?.GetValue(null) is string p) { p = p.Trim('"'); if (File.Exists(p)) return p; }
                }
            }
            string win = Environment.GetFolderPath(Environment.SpecialFolder.Windows);
            foreach (var c in new[] { Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.System), exeName), Path.Combine(win, exeName) })
                if (File.Exists(c)) return c;
        }
        catch { }
        return null;
    }
}
