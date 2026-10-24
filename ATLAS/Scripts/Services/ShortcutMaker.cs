using System;
using System.IO;
using System.Runtime.InteropServices;

namespace Atlas;

/// <summary>Creates genuine Windows .lnk shortcuts through the Windows Script Host COM object (no text file pretending to be a shortcut).</summary>
public static class ShortcutMaker
{
    public static string Validate(string target, string name, string folder)
    {
        string t = Clean(target);
        if (string.IsNullOrEmpty(t)) return "Enter the target path.";
        if (!FileOps.PathExists(t)) return "The target was not found.";
        string n = StripLnk(name);
        string err = FileOps.ValidateName(n);
        if (err != null) return err;
        if (!string.IsNullOrEmpty(folder) && FileOps.PathExists(Path.Combine(folder, n + ".lnk"))) return "A shortcut named \"" + n + "\" already exists here.";
        return null;
    }

    static string Clean(string s) => (s ?? "").Trim().Trim('"');
    static string StripLnk(string s)
    {
        s = (s ?? "").Trim();
        return s.EndsWith(".lnk", StringComparison.OrdinalIgnoreCase) ? s.Substring(0, s.Length - 4).TrimEnd() : s;
    }

    /// <summary>Returns an error message or null. Never overwrites an existing item.</summary>
    public static string Create(string folder, string name, string target, out string created)
    {
        created = null;
        try
        {
            if (!OperatingSystem.IsWindows()) return "Shortcuts can only be created on Windows.";
            string err = Validate(target, name, folder);
            if (err != null) return err;
            if (!Directory.Exists(folder)) return "The current folder no longer exists.";

            string t = Path.GetFullPath(Clean(target));
            string lnk = Path.Combine(folder, StripLnk(name) + ".lnk");

            var type = Type.GetTypeFromProgID("WScript.Shell");
            if (type == null) return "Windows Script Host is not available on this computer, so shortcuts cannot be created.";
            object shell = Activator.CreateInstance(type);
            try
            {
                dynamic sh = shell;
                dynamic sc = sh.CreateShortcut(lnk);
                sc.TargetPath = t;
                sc.WorkingDirectory = Directory.Exists(t) ? t : (Path.GetDirectoryName(t) ?? "");
                sc.Save();
            }
            finally { try { Marshal.FinalReleaseComObject(shell); } catch { } }

            if (!File.Exists(lnk)) return "Windows did not create the shortcut.";
            created = lnk;
            return null;
        }
        catch (Exception ex) { return "Could not create the shortcut: " + FileOps.Describe(ex); }
    }
}
