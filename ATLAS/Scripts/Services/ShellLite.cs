using System;
using System.Diagnostics;
using System.IO;
using System.Runtime.InteropServices;

namespace Atlas;

/// <summary>
/// Minimal Windows shell bridge used by Part 1 (Open / Reveal / Properties / Explorer / Terminal / VS Code).
/// Part 2 replaces this with the full WindowsShellManager (rename, copy/paste, Recycle Bin, ZIP...).
/// All methods return an error message (null on success) and never throw.
/// </summary>
public static class ShellLite
{
    public static string OpenPath(string path)
    {
        try
        {
            if (!File.Exists(path) && !Directory.Exists(path)) return "Item no longer exists.";
            Process.Start(new ProcessStartInfo(path) { UseShellExecute = true });
            return null;
        }
        catch (Exception ex) { return ex.Message; }
    }

    /// <summary>Opens a folder in the normal Windows File Explorer (used by the overflow "+" button).</summary>
    public static string OpenFolderInExplorer(string folder)
    {
        try
        {
            if (!Directory.Exists(folder)) return "Folder no longer exists.";
            Process.Start(new ProcessStartInfo("explorer.exe", "\"" + folder.TrimEnd('\\') + "\"") { UseShellExecute = true });
            return null;
        }
        catch (Exception ex) { return ex.Message; }
    }

    public static string Reveal(string path)
    {
        try
        {
            if (!File.Exists(path) && !Directory.Exists(path)) return "Item no longer exists.";
            Process.Start(new ProcessStartInfo("explorer.exe", "/select,\"" + path + "\"") { UseShellExecute = true });
            return null;
        }
        catch (Exception ex) { return ex.Message; }
    }

    /// <summary>
    /// Starts a program from its absolute executable path (used by Open All for application entities).
    /// No arguments are passed and the path is never put into a shell command string.
    /// </summary>
    public static string LaunchExecutable(string exePath)
    {
        try
        {
            if (string.IsNullOrWhiteSpace(exePath) || !Path.IsPathRooted(exePath)) return "Invalid program path.";
            if (!exePath.EndsWith(".exe", StringComparison.OrdinalIgnoreCase)) return "Not an executable program.";
            if (!File.Exists(exePath)) return "Program no longer exists.";
            Process.Start(new ProcessStartInfo(exePath) { UseShellExecute = true, WorkingDirectory = Path.GetDirectoryName(exePath) });
            return null;
        }
        catch (Exception ex) { return ex.Message; }
    }

    public static string OpenTerminal(string directory)
    {
        try
        {
            if (!Directory.Exists(directory)) return "Folder no longer exists.";
            try
            {
                var psi = new ProcessStartInfo("wt.exe") { UseShellExecute = false };
                psi.ArgumentList.Add("-d");
                psi.ArgumentList.Add(directory);
                Process.Start(psi);
            }
            catch
            {
                Process.Start(new ProcessStartInfo("cmd.exe") { WorkingDirectory = directory, UseShellExecute = true });
            }
            return null;
        }
        catch (Exception ex) { return ex.Message; }
    }

    public static string OpenVsCode(string path)
    {
        try
        {
            var psi = new ProcessStartInfo("cmd.exe") { UseShellExecute = false, CreateNoWindow = true };
            psi.ArgumentList.Add("/c");
            psi.ArgumentList.Add("code");
            psi.ArgumentList.Add(path);
            Process.Start(psi);
            return null;
        }
        catch (Exception ex) { return "VS Code could not be started: " + ex.Message; }
    }

    // ------------------------------------------------------------------ native Properties dialog

    [StructLayout(LayoutKind.Sequential, CharSet = CharSet.Auto)]
    struct SHELLEXECUTEINFO
    {
        public int cbSize;
        public uint fMask;
        public IntPtr hwnd;
        [MarshalAs(UnmanagedType.LPTStr)] public string lpVerb;
        [MarshalAs(UnmanagedType.LPTStr)] public string lpFile;
        [MarshalAs(UnmanagedType.LPTStr)] public string lpParameters;
        [MarshalAs(UnmanagedType.LPTStr)] public string lpDirectory;
        public int nShow;
        public IntPtr hInstApp;
        public IntPtr lpIDList;
        [MarshalAs(UnmanagedType.LPTStr)] public string lpClass;
        public IntPtr hkeyClass;
        public uint dwHotKey;
        public IntPtr hIcon;
        public IntPtr hProcess;
    }

    const uint SEE_MASK_INVOKEIDLIST = 0x0000000C;
    const int SW_SHOW = 5;

    [DllImport("shell32.dll", CharSet = CharSet.Auto, SetLastError = true)]
    static extern bool ShellExecuteEx(ref SHELLEXECUTEINFO lpExecInfo);

    /// <summary>Windows "Open with" chooser for a file (shell verb "openas"; the path is passed as data, never as a command line).</summary>
    public static string OpenWith(string path)
    {
        try
        {
            if (!OperatingSystem.IsWindows()) return "Open With is only available on Windows.";
            if (!File.Exists(path)) return "Item no longer exists.";
            var info = new SHELLEXECUTEINFO
            {
                cbSize = Marshal.SizeOf<SHELLEXECUTEINFO>(),
                lpVerb = "openas",
                lpFile = path,
                nShow = SW_SHOW,
                fMask = 0,
            };
            return ShellExecuteEx(ref info) ? null : "Windows could not open the Open With dialog (error " + Marshal.GetLastWin32Error() + ").";
        }
        catch (Exception ex) { return ex.Message; }
    }

    /// <summary>
    /// Opens <paramref name="filePath"/> with a chosen program. The file is passed as a single argument through ArgumentList
    /// (Windows-correct quoting, no shell involved), and the program is validated first: it must be an existing .exe, and the
    /// file is never executed itself.
    /// </summary>
    public static string OpenWithApp(string exePath, string filePath)
    {
        try
        {
            if (string.IsNullOrWhiteSpace(exePath) || !Path.IsPathRooted(exePath)) return "Choose a program (a full path to an .exe).";
            if (!exePath.EndsWith(".exe", StringComparison.OrdinalIgnoreCase)) return "That is not a program (.exe).";
            if (!File.Exists(exePath)) return "That program no longer exists.";
            if (!File.Exists(filePath)) return "The file no longer exists.";
            if (string.Equals(Path.GetFullPath(exePath), Path.GetFullPath(filePath), StringComparison.OrdinalIgnoreCase)) return "A program cannot be opened with itself.";
            var psi = new ProcessStartInfo(exePath) { UseShellExecute = false, WorkingDirectory = Path.GetDirectoryName(Path.GetFullPath(filePath)) ?? "" };
            psi.ArgumentList.Add(filePath);
            Process.Start(psi);
            return null;
        }
        catch (Exception ex) { return "Could not start the program: " + ex.Message; }
    }

    public static string ShowProperties(string path)
    {
        try
        {
            if (!OperatingSystem.IsWindows()) return "Properties dialog is only available on Windows.";
            if (!File.Exists(path) && !Directory.Exists(path)) return "Item no longer exists.";
            var info = new SHELLEXECUTEINFO
            {
                cbSize = Marshal.SizeOf<SHELLEXECUTEINFO>(),
                lpVerb = "properties",
                lpFile = path,
                nShow = SW_SHOW,
                fMask = SEE_MASK_INVOKEIDLIST,
            };
            return ShellExecuteEx(ref info) ? null : "Windows refused to open the Properties dialog (error " + Marshal.GetLastWin32Error() + ").";
        }
        catch (Exception ex) { return ex.Message; }
    }
}
