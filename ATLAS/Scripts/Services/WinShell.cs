using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.Runtime.InteropServices;
using System.Text;

namespace Atlas;

/// <summary>
/// The small amount of Windows integration the activity features need. Everything is wrapped in try/catch and returns
/// null on failure; on non-Windows platforms every method simply returns null.
///
/// What is (and is not) observable:
///  * GetDefaultHandler    : which program Windows will use to open a document (shell association query, no process started).
///  * GetForegroundExe     : executable path of the program that owns the foreground window. Needs no special permission,
///                           but returns null for elevated (administrator) programs and for some packaged (Store/UWP) apps,
///                           which report a shared host process instead of the real app. Window titles are never read.
/// </summary>
public static class WinShell
{
    const uint ProcessQueryLimitedInformation = 0x1000;
    const uint AssocstrExecutable = 2;

    [DllImport("user32.dll")]
    static extern IntPtr GetForegroundWindow();

    [DllImport("user32.dll")]
    static extern uint GetWindowThreadProcessId(IntPtr hWnd, out uint processId);

    [DllImport("kernel32.dll", SetLastError = true)]
    static extern IntPtr OpenProcess(uint desiredAccess, bool inheritHandle, uint processId);

    [DllImport("kernel32.dll", SetLastError = true)]
    static extern bool CloseHandle(IntPtr handle);

    [DllImport("kernel32.dll", CharSet = CharSet.Unicode, SetLastError = true)]
    static extern bool QueryFullProcessImageName(IntPtr process, int flags, StringBuilder exeName, ref int size);

    [DllImport("shlwapi.dll", CharSet = CharSet.Unicode)]
    static extern uint AssocQueryString(uint flags, uint str, string assoc, string extra, StringBuilder output, ref uint outLength);

    static readonly Dictionary<string, string> NameCache = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);

    // Programs that are part of the Windows shell itself or generic hosts: they would only add noise as "applications".
    static readonly HashSet<string> IgnoredPrograms = new HashSet<string>(StringComparer.OrdinalIgnoreCase)
    {
        "explorer.exe", "applicationframehost.exe", "searchhost.exe", "searchapp.exe", "shellexperiencehost.exe",
        "startmenuexperiencehost.exe", "lockapp.exe", "textinputhost.exe", "dwm.exe", "sihost.exe", "openwith.exe",
        "systemsettings.exe", "taskhostw.exe", "consent.exe",
    };

    public static bool IsIgnoredProgram(string exePath)
    {
        if (string.IsNullOrEmpty(exePath)) return true;
        try { return IgnoredPrograms.Contains(Path.GetFileName(exePath)); } catch { return true; }
    }

    /// <summary>Full path of the program that owns the foreground window, or null.</summary>
    public static string GetForegroundExe()
    {
        try
        {
            if (!OperatingSystem.IsWindows()) return null;
            IntPtr hwnd = GetForegroundWindow();
            if (hwnd == IntPtr.Zero) return null;
            uint pid;
            GetWindowThreadProcessId(hwnd, out pid);
            if (pid == 0) return null;
            IntPtr h = OpenProcess(ProcessQueryLimitedInformation, false, pid);
            if (h == IntPtr.Zero) return null; // elevated or protected process
            try
            {
                var sb = new StringBuilder(1024);
                int size = sb.Capacity;
                return QueryFullProcessImageName(h, 0, sb, ref size) ? sb.ToString(0, size) : null;
            }
            finally { CloseHandle(h); }
        }
        catch (Exception) { return null; }
    }

    /// <summary>Executable Windows would use to open this document, or null (no association, packaged app, not Windows).</summary>
    public static string GetDefaultHandler(string documentPath)
    {
        try
        {
            if (!OperatingSystem.IsWindows()) return null;
            string ext = Path.GetExtension(documentPath);
            if (string.IsNullOrEmpty(ext)) return null;
            var sb = new StringBuilder(1024);
            uint len = (uint)sb.Capacity;
            uint hr = AssocQueryString(0, AssocstrExecutable, ext, "open", sb, ref len);
            if (hr != 0) return null;
            string exe = sb.ToString();
            if (string.IsNullOrWhiteSpace(exe) || !File.Exists(exe)) return null;
            return IsIgnoredProgram(exe) ? null : exe;
        }
        catch (Exception) { return null; }
    }

    /// <summary>Human-friendly program name from the file's version resource, falling back to the file name.</summary>
    public static string DisplayName(string exePath)
    {
        if (string.IsNullOrEmpty(exePath)) return "Application";
        lock (NameCache)
        {
            string cached;
            if (NameCache.TryGetValue(exePath, out cached)) return cached;
        }
        string name = null;
        try
        {
            var vi = FileVersionInfo.GetVersionInfo(exePath);
            name = !string.IsNullOrWhiteSpace(vi.FileDescription) ? vi.FileDescription : vi.ProductName;
        }
        catch (Exception) { }
        if (string.IsNullOrWhiteSpace(name))
        {
            try { name = Path.GetFileNameWithoutExtension(exePath); } catch { name = "Application"; }
        }
        name = name.Trim();
        lock (NameCache) NameCache[exePath] = name;
        return name;
    }

    public static string CurrentProcessExe()
    {
        try { return Process.GetCurrentProcess().MainModule?.FileName; } catch (Exception) { return null; }
    }
}
