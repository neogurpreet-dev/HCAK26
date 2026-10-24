using System;
using System.Collections.Generic;
using System.IO;

namespace Atlas;

/// <summary>
/// Keeps ATLAS on local and physically connected storage. Two rules:
///  * Drives: only <see cref="DriveType.Fixed"/> and <see cref="DriveType.Removable"/> are allowed. Network, optical, RAM and unknown
///    drives, and UNC (\\server\share) paths, are refused.
///  * Entries: anything with a reparse point (junctions, symlinks and the cloud-files placeholders OneDrive / Dropbox / Google Drive
///    use) or an Offline / Recall-on-access attribute (file content not stored on this disk) is skipped, so ATLAS never lists it and
///    never triggers a cloud download by reading it.
/// Pure System.IO, no Godot objects: safe on worker threads.
/// </summary>
public static class DriveGuard
{
    // FileAttributes has no named members for these two in .NET: they mark cloud placeholders whose data is fetched on access.
    const FileAttributes RecallOnOpen = (FileAttributes)0x00040000;
    const FileAttributes RecallOnDataAccess = (FileAttributes)0x00400000;

    /// <summary>Attributes that make an entry invisible to ATLAS. Used as EnumerationOptions.AttributesToSkip, so the filtering happens inside the OS enumeration (no per-entry cost, no exceptions).</summary>
    public const FileAttributes SkipAttributes = FileAttributes.ReparsePoint | FileAttributes.Offline | RecallOnOpen | RecallOnDataAccess;

    /// <summary>Local and removable drives that are ready (the only places ATLAS may scan).</summary>
    public static List<DriveInfo> LocalDrives()
    {
        var list = new List<DriveInfo>();
        try
        {
            foreach (var d in DriveInfo.GetDrives())
            {
                try { if ((d.DriveType == DriveType.Fixed || d.DriveType == DriveType.Removable) && d.IsReady) list.Add(d); }
                catch { /* drive vanished while enumerating */ }
            }
        }
        catch { }
        return list;
    }

    /// <summary>Returns null when ATLAS may open this folder, otherwise a message for the user. Never throws.</summary>
    public static string Check(string fullPath)
    {
        try
        {
            string root = Path.GetPathRoot(fullPath);
            if (string.IsNullOrEmpty(root)) return "That path has no drive.";
            if (root.StartsWith("\\\\", StringComparison.Ordinal))
                return "Network locations are not supported: ATLAS only shows local and removable drives.";

            if (OperatingSystem.IsWindows())
            {
                DriveType type;
                try { type = new DriveInfo(root).DriveType; }
                catch (ArgumentException) { return "Unknown drive: ATLAS only shows local and removable drives."; }
                if (type != DriveType.Fixed && type != DriveType.Removable)
                    return root + " is a " + Describe(type) + " drive: ATLAS only shows local and removable drives.";
            }

            // the folder itself must not be a link / cloud placeholder (a drive root never is)
            bool isRoot = string.Equals(fullPath.TrimEnd('\\', '/'), root.TrimEnd('\\', '/'), StringComparison.OrdinalIgnoreCase);
            if (!isRoot)
            {
                FileAttributes attrs;
                try { attrs = File.GetAttributes(fullPath); }
                catch { return null; } // unreadable here: the normal "access denied / not found" path reports it
                if ((attrs & SkipAttributes) != 0)
                    return "This folder is a link or a cloud-synced folder that is not stored on a local disk, so ATLAS skips it.";
            }
            return null;
        }
        catch (Exception) { return null; }
    }

    static string Describe(DriveType t)
    {
        switch (t)
        {
            case DriveType.Network: return "network";
            case DriveType.CDRom: return "optical";
            case DriveType.Ram: return "RAM";
            case DriveType.NoRootDirectory: return "missing";
            default: return "unknown";
        }
    }
}
