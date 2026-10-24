using System;
using System.IO;

namespace Atlas;

/// <summary>
/// Strongly typed snapshot of one real file or folder. Always carries the absolute path;
/// visual objects only ever reference this, they never replace it.
/// </summary>
public sealed class FsEntry
{
    static readonly DateTime ValidAfter = new DateTime(1980, 1, 1);

    public string Path = "";
    public string Name = "";
    public string Extension = "";
    public string Category = FileCategory.Other;
    public bool IsDirectory;
    public bool IsReparsePoint;

    /// <summary>File length in bytes, or -1 when unavailable.</summary>
    public long Size = -1;
    /// <summary>Recursive folder size in bytes, or -1 while not yet computed.</summary>
    public long RecursiveSize = -1;

    public DateTime Modified, Created, Accessed;
    public bool HasModified, HasCreated, HasAccessed;

    // Filled in by the usage tracker.
    public int UsageCount;
    public double RecencyDays = 9999;
    public double UsageScore;

    public bool IsArchive => Category == FileCategory.Archives;

    /// <summary>Size used for visuals: recursive size for folders (when known), length for files.</summary>
    public long EffectiveSize => IsDirectory ? RecursiveSize : Size;

    public static FsEntry FromInfo(FileSystemInfo fi)
    {
        var e = new FsEntry { Path = fi.FullName, Name = fi.Name };
        try
        {
            e.IsDirectory = (fi.Attributes & FileAttributes.Directory) != 0;
            e.IsReparsePoint = (fi.Attributes & FileAttributes.ReparsePoint) != 0;
        }
        catch { e.IsDirectory = fi is DirectoryInfo; }

        if (e.IsDirectory)
        {
            e.Category = FileCategory.Folder;
        }
        else
        {
            e.Extension = FileCategory.NormalizeExtension(fi.Extension);
            e.Category = FileCategory.Classify(e.Extension);
            try { if (fi is FileInfo f) e.Size = f.Length; } catch { e.Size = -1; }
        }

        try { var t = fi.LastWriteTime; if (t > ValidAfter) { e.Modified = t; e.HasModified = true; } } catch { }
        try { var t = fi.CreationTime; if (t > ValidAfter) { e.Created = t; e.HasCreated = true; } } catch { }
        try { var t = fi.LastAccessTime; if (t > ValidAfter) { e.Accessed = t; e.HasAccessed = true; } } catch { }
        return e;
    }

    public FsEntry CloneShallow() => (FsEntry)MemberwiseClone();
}
