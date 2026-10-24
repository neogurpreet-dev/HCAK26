using System;
using System.Collections.Generic;
using Godot;

namespace Atlas;

/// <summary>Extension -> category classification, display names and the ATLAS category palette.</summary>
public static class FileCategory
{
    public const string Code = "Code";
    public const string Documents = "Documents";
    public const string Images = "Images";
    public const string Media = "Media";
    public const string Archives = "Archives";
    public const string Other = "Other";
    public const string Folder = "Folder";

    /// <summary>Order in which satellite islands are placed, clockwise starting upper-left (matches the reference).</summary>
    public static readonly string[] Ordered = { Code, Documents, Media, Other, Images, Archives };

    static readonly Dictionary<string, string> ExtMap = BuildExtMap();
    static readonly Dictionary<string, string> TypeNames = BuildTypeNames();

    static Dictionary<string, string> BuildExtMap()
    {
        var d = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
        void Add(string cat, string list)
        {
            foreach (var e in list.Split(' ', StringSplitOptions.RemoveEmptyEntries)) d[e] = cat;
        }
        Add(Code, "py cs cpp c h hpp cc js jsx ts tsx html htm css scss json xml yml yaml toml java kt go rs rb php sh bat cmd ps1 sql swift lua gd gdshader tscn tres ipynb ini cfg csproj sln vue");
        Add(Documents, "pdf doc docx txt md rtf odt xls xlsx csv ppt pptx epub tex log");
        Add(Images, "png jpg jpeg gif bmp svg webp ico tif tiff psd heic raw");
        Add(Media, "mp4 mkv avi mov wmv webm flv mp3 wav flac ogg m4a aac wma");
        Add(Archives, "zip rar 7z tar gz bz2 xz iso cab");
        return d;
    }

    static Dictionary<string, string> BuildTypeNames()
    {
        return new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase)
        {
            ["py"] = "Python File", ["cs"] = "C# Source File", ["cpp"] = "C++ Source File", ["c"] = "C Source File",
            ["h"] = "C/C++ Header", ["js"] = "JavaScript File", ["ts"] = "TypeScript File", ["html"] = "HTML Document",
            ["css"] = "Stylesheet", ["json"] = "JSON File", ["xml"] = "XML Document", ["yml"] = "YAML File",
            ["yaml"] = "YAML File", ["java"] = "Java Source File", ["gd"] = "GDScript File", ["ipynb"] = "Jupyter Notebook",
            ["pdf"] = "PDF Document", ["doc"] = "Word Document", ["docx"] = "Word Document", ["txt"] = "Text Document",
            ["md"] = "Markdown Document", ["xls"] = "Excel Workbook", ["xlsx"] = "Excel Workbook", ["csv"] = "CSV File",
            ["ppt"] = "PowerPoint Deck", ["pptx"] = "PowerPoint Deck", ["png"] = "PNG Image", ["jpg"] = "JPEG Image",
            ["jpeg"] = "JPEG Image", ["gif"] = "GIF Image", ["svg"] = "SVG Image", ["webp"] = "WebP Image",
            ["mp4"] = "MP4 Video", ["mkv"] = "MKV Video", ["mov"] = "QuickTime Video", ["mp3"] = "MP3 Audio",
            ["wav"] = "WAV Audio", ["flac"] = "FLAC Audio", ["zip"] = "ZIP Archive", ["rar"] = "RAR Archive",
            ["7z"] = "7-Zip Archive", ["tar"] = "TAR Archive", ["gz"] = "GZip Archive", ["exe"] = "Application",
            ["dll"] = "Application Extension",
        };
    }

    public static string NormalizeExtension(string ext)
    {
        if (string.IsNullOrWhiteSpace(ext)) return string.Empty;
        return ext.Trim().TrimStart('.').ToLowerInvariant();
    }

    public static string Classify(string extension)
    {
        var e = NormalizeExtension(extension);
        return e.Length > 0 && ExtMap.TryGetValue(e, out var c) ? c : Other;
    }

    public static string DescribeType(string extension, bool isDirectory)
    {
        if (isDirectory) return "File folder";
        var e = NormalizeExtension(extension);
        if (e.Length == 0) return "File";
        return TypeNames.TryGetValue(e, out var n) ? n : e.ToUpperInvariant() + " File";
    }

    public static int IndexOf(string category)
    {
        for (int i = 0; i < Ordered.Length; i++) if (Ordered[i] == category) return i;
        return Ordered.Length - 1;
    }

    /// <summary>Saturated category colour (used for accents and island rims).</summary>
    public static Color CategoryColor(string category)
    {
        switch (category)
        {
            case Code: return new Color("3B82F6");
            case Documents: return new Color("A855F7");
            case Images: return new Color("FACC15");
            case Media: return new Color("22C55E");
            case Archives: return new Color("FF6A3D");
            case Folder: return new Color("22D3EE");
            default: return new Color("A7B1C2");
        }
    }
}
