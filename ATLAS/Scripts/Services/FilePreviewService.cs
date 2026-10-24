using System;
using System.Collections.Generic;
using System.IO;
using System.Text;
using System.Text.RegularExpressions;
using Godot;

namespace Atlas;

public enum PreviewKind { Text, Image, Folder, Other }

/// <summary>Everything the hover panel needs. Built on a worker thread, so it holds no scene objects (the texture is made later).</summary>
public sealed class PreviewData
{
    public PreviewKind Kind = PreviewKind.Other;
    public string Body = "";          // BBCode, escaped
    public string Note = "";          // plain message shown when there is no content preview
    public Image Thumb;               // worker-thread result, turned into a texture on the main thread
    public Texture2D ThumbTexture;
}

/// <summary>
/// Safe, bounded previews. Nothing is ever executed, rendered by a script engine or opened through the shell:
///  * text / code : first 4 KB only, binary content is rejected, at most 12 lines of 64 characters, light syntax colouring
///  * images      : png / jpg / webp / bmp / tga / svg up to 12 MB, decoded and shrunk to a 320 px thumbnail
///  * folders     : item count and the first 10 names
///  * everything else (PDF, Office, media, executables...) : metadata only
/// PDF first-page thumbnails would need a PDF renderer that ATLAS does not include, so PDFs show metadata and a note.
/// </summary>
public static class FilePreviewService
{
    const int MaxTextBytes = 4096;
    const int MaxLines = 12;
    const int MaxLineChars = 64;
    const long MaxImageBytes = 12L * 1024 * 1024;
    const int ThumbMax = 450;
    const long MaxShellFileBytes = 200L * 1024 * 1024;

    static readonly HashSet<string> ImageExt = new HashSet<string>(StringComparer.OrdinalIgnoreCase) { "png", "jpg", "jpeg", "webp", "bmp", "tga", "svg" };

    // Plain-text documents (read directly). Source code is deliberately not previewed: code files get the small name label instead.
    static readonly HashSet<string> TextExt = new HashSet<string>(StringComparer.OrdinalIgnoreCase)
    {
        "txt", "md", "markdown", "log", "csv", "tsv", "tex",
    };

    // Formats Godot cannot decode itself but Windows can thumbnail.
    static readonly HashSet<string> ShellImageExt = new HashSet<string>(StringComparer.OrdinalIgnoreCase)
    {
        "gif", "ico", "tif", "tiff", "heic", "psd",
    };

    // PDFs, documents, presentations, spreadsheets: previewed with the thumbnail Windows itself shows (first page / slide).
    static readonly HashSet<string> ShellDocExt = new HashSet<string>(StringComparer.OrdinalIgnoreCase)
    {
        "pdf", "doc", "docx", "rtf", "odt", "ppt", "pptx", "odp", "xls", "xlsx", "ods", "epub",
    };

    static readonly HashSet<string> SlashComment = new HashSet<string>(StringComparer.OrdinalIgnoreCase)
        { "cs", "c", "cpp", "cc", "h", "hpp", "java", "kt", "go", "rs", "js", "ts", "tsx", "jsx", "php", "swift", "gdshader", "scss" };
    static readonly HashSet<string> HashComment = new HashSet<string>(StringComparer.OrdinalIgnoreCase)
        { "py", "sh", "bash", "rb", "ps1", "gd", "yml", "yaml", "toml", "ini", "cfg", "conf" };
    static readonly HashSet<string> DashComment = new HashSet<string>(StringComparer.OrdinalIgnoreCase) { "sql", "lua" };

    const string ColKeyword = "79C0FF", ColString = "7EE787", ColNumber = "FFA657", ColComment = "6B7F9E";

    static readonly Regex TokenRx = new Regex(
        @"(?<s>""(?:\\.|[^""\\])*""?|'(?:\\.|[^'\\])*'?)|(?<n>\b\d+(?:\.\d+)?\b)|(?<k>\b(?:if|else|elif|for|foreach|while|do|return|class|struct|enum|interface|public|private|protected|static|void|int|float|double|bool|string|var|let|const|new|using|namespace|import|from|def|func|function|fn|true|false|null|none|this|self|switch|case|break|continue|try|catch|except|finally|throw|in|is|as|and|or|not|extends|override|async|await|select|where|insert|update|delete|create|table)\b)",
        RegexOptions.Compiled | RegexOptions.IgnoreCase);

    /// <summary>Only text/code and image files get a hover preview. Folders, PDFs, Office files, media and executables never do.</summary>
    public static bool IsSupported(FsEntry e)
    {
        if (e == null || e.IsDirectory) return false;
        string ext = e.Extension ?? "";
        return ImageExt.Contains(ext) || ShellImageExt.Contains(ext) || TextExt.Contains(ext) || ShellDocExt.Contains(ext);
    }

    /// <summary>True only when there is real content to show (readable text or a decoded thumbnail). Never true for notes or failures.</summary>
    public static bool IsMeaningful(PreviewData d)
    {
        if (d == null) return false;
        if (d.Kind == PreviewKind.Image) return d.ThumbTexture != null;
        if (d.Kind == PreviewKind.Text) return !string.IsNullOrWhiteSpace(d.Body);
        return false;
    }

    public static string Esc(string s) => s.Replace("[", "[lb]");

    public static string Subtitle(FsEntry e)
    {
        string type = FileCategory.DescribeType(e.Extension, e.IsDirectory);
        string size = e.IsDirectory ? "" : InfoPanelView.FormatSize(e);
        string when = e.HasModified ? e.Modified.ToString("dd MMM yyyy", System.Globalization.CultureInfo.InvariantCulture) : "";
        var parts = new List<string> { type };
        if (size.Length > 0) parts.Add(size);
        if (when.Length > 0) parts.Add(when);
        return string.Join("  -  ", parts);
    }

    /// <summary>Safe to call from a worker thread.</summary>
    public static PreviewData Build(FsEntry e)
    {
        var d = new PreviewData();
        if (e.IsDirectory) return BuildFolder(e, d);

        string ext = e.Extension ?? "";
        try
        {
            if (!File.Exists(e.Path)) { d.Note = "This file is no longer available."; return d; }
            if (ImageExt.Contains(ext))
            {
                var r = BuildImage(e, d);
                if (r.Kind == PreviewKind.Image) return r;
                return BuildShell(e, new PreviewData(), ext);   // Godot could not decode it: try the Windows thumbnail
            }
            if (TextExt.Contains(ext)) return BuildText(e, d, ext);
            if (ShellImageExt.Contains(ext) || ShellDocExt.Contains(ext)) return BuildShell(e, d, ext);
        }
        catch (Exception ex) { d.Note = "Preview unavailable: " + ex.Message; return d; }

        return d; // unsupported: no content, nothing is shown
    }

    public static PreviewData Failed(FsEntry e, string message) => new PreviewData { Kind = PreviewKind.Other, Note = "Preview unavailable: " + message };

    // ------------------------------------------------------------------ folders

    static PreviewData BuildFolder(FsEntry e, PreviewData d)
    {
        d.Kind = PreviewKind.Folder;
        try
        {
            var lines = new List<string>();
            int total = 0;
            foreach (var entry in Directory.EnumerateFileSystemEntries(e.Path))
            {
                total++;
                if (lines.Count < 10)
                {
                    string name = InfoPanelView.Elide(Path.GetFileName(entry), 44);
                    lines.Add(Directory.Exists(entry) ? "- [color=#7DD3FC]" + Esc(name) + "[/color]" : "- " + Esc(name));
                }
                if (total >= 2000) break;
            }
            var sb = new StringBuilder();
            sb.Append("[color=#8FA3C7]").Append(total >= 2000 ? "2000+ items" : total + (total == 1 ? " item" : " items")).Append("[/color]\n");
            foreach (var l in lines) sb.Append(l).Append('\n');
            if (total > lines.Count) sb.Append("[color=#8FA3C7]...[/color]");
            d.Body = sb.ToString();
        }
        catch (Exception) { d.Kind = PreviewKind.Other; d.Note = "This folder cannot be listed."; }
        return d;
    }

    // ------------------------------------------------------------------ images

    static PreviewData BuildImage(FsEntry e, PreviewData d)
    {
        var fi = new FileInfo(e.Path);
        if (fi.Length > MaxImageBytes) { d.Note = "Image is too large for an instant preview (over 12 MB)."; return d; }
        var img = Image.LoadFromFile(e.Path);
        if (img == null || img.IsEmpty()) { d.Note = "This image could not be decoded."; return d; }
        int w = img.GetWidth(), h = img.GetHeight();
        if (w > ThumbMax || h > ThumbMax)
        {
            float k = ThumbMax / (float)Math.Max(w, h);
            img.Resize(Math.Max(1, (int)(w * k)), Math.Max(1, (int)(h * k)), Image.Interpolation.Bilinear);
        }
        d.Kind = PreviewKind.Image;
        d.Thumb = img;
        return d;
    }

    // ------------------------------------------------------------------ PDFs, documents, presentations (Windows thumbnails)

    static PreviewData BuildShell(FsEntry e, PreviewData d, string ext)
    {
        var fi = new FileInfo(e.Path);
        if (fi.Length <= 0 || fi.Length > MaxShellFileBytes) return d;
        Image img = null;
        if (WinShell.TryGetThumbnail(e.Path, ThumbMax, 300, out var px, out int w, out int h))
        {
            try { img = Image.CreateFromData(w, h, false, Image.Format.Rgba8, px); } catch (Exception) { img = null; }
        }
        if (img == null || img.IsEmpty()) img = ZipThumbnail(e.Path); // Office / OpenDocument files usually embed a small first-page picture
        if (img == null || img.IsEmpty()) return d;                   // no thumbnail available: nothing to show
        int iw = img.GetWidth(), ih = img.GetHeight();
        if (iw > ThumbMax || ih > ThumbMax)
        {
            float k = ThumbMax / (float)Math.Max(iw, ih);
            img.Resize(Math.Max(1, (int)(iw * k)), Math.Max(1, (int)(ih * k)), Image.Interpolation.Bilinear);
        }
        d.Kind = PreviewKind.Image;
        d.Thumb = img;
        return d;
    }

    static Image ZipThumbnail(string path)
    {
        try
        {
            using var zip = System.IO.Compression.ZipFile.OpenRead(path);
            foreach (var name in new[] { "docProps/thumbnail.jpeg", "docProps/thumbnail.jpg", "docProps/thumbnail.png", "Thumbnails/thumbnail.png" })
            {
                var entry = zip.GetEntry(name);
                if (entry == null || entry.Length <= 0 || entry.Length > 4L * 1024 * 1024) continue;
                byte[] bytes;
                using (var s = entry.Open())
                using (var ms = new MemoryStream())
                {
                    s.CopyTo(ms);
                    bytes = ms.ToArray();
                }
                var img = new Image();
                var err = name.EndsWith(".png", StringComparison.OrdinalIgnoreCase) ? img.LoadPngFromBuffer(bytes) : img.LoadJpgFromBuffer(bytes);
                if (err == Error.Ok && !img.IsEmpty()) return img;
            }
        }
        catch (Exception) { }
        return null;
    }

    // ------------------------------------------------------------------ text / code

    static PreviewData BuildText(FsEntry e, PreviewData d, string ext)
    {
        byte[] buf = new byte[MaxTextBytes];
        int n;
        using (var fs = new FileStream(e.Path, FileMode.Open, System.IO.FileAccess.Read, FileShare.ReadWrite | FileShare.Delete))
            n = fs.Read(buf, 0, buf.Length);

        if (n == 0) return d; // empty file: nothing to preview
        if (Array.IndexOf(buf, (byte)0, 0, n) >= 0) { d.Note = "Binary content: no text preview."; return d; }

        string text = Encoding.UTF8.GetString(buf, 0, n).TrimStart('\uFEFF').Replace("\r\n", "\n").Replace('\r', '\n');
        if (text.Trim().Length == 0) return d; // whitespace only: nothing to preview
        var all = text.Split('\n');
        bool cut = n == buf.Length || all.Length > MaxLines;
        var sb = new StringBuilder();
        int shown = 0;
        foreach (var raw in all)
        {
            if (shown >= MaxLines) break;
            string line = raw.Replace("\t", "    ");
            if (line.Length > MaxLineChars) line = line.Substring(0, MaxLineChars - 3) + "...";
            sb.Append(Highlight(line, ext)).Append('\n');
            shown++;
        }
        if (cut) sb.Append("[color=#8FA3C7]...[/color]");
        d.Kind = PreviewKind.Text;
        d.Body = sb.ToString();
        return d;
    }

    static string CommentToken(string ext)
    {
        if (SlashComment.Contains(ext)) return "//";
        if (HashComment.Contains(ext)) return "#";
        if (DashComment.Contains(ext)) return "--";
        return null;
    }

    static int CommentIndex(string line, string tok)
    {
        bool inSingle = false, inDouble = false;
        for (int i = 0; i < line.Length; i++)
        {
            char c = line[i];
            if (c == '\\') { i++; continue; }
            if (!inSingle && c == '"') inDouble = !inDouble;
            else if (!inDouble && c == '\'') inSingle = !inSingle;
            else if (!inSingle && !inDouble && string.CompareOrdinal(line, i, tok, 0, tok.Length) == 0) return i;
        }
        return -1;
    }

    static string Highlight(string line, string ext)
    {
        string tok = CommentToken(ext);
        if (tok == null) return Esc(line);

        int ci = CommentIndex(line, tok);
        string code = ci >= 0 ? line.Substring(0, ci) : line;
        string comment = ci >= 0 ? line.Substring(ci) : "";

        var sb = new StringBuilder();
        int pos = 0;
        foreach (Match m in TokenRx.Matches(code))
        {
            if (m.Index > pos) sb.Append(Esc(code.Substring(pos, m.Index - pos)));
            string color = m.Groups["s"].Success ? ColString : (m.Groups["n"].Success ? ColNumber : ColKeyword);
            sb.Append("[color=#").Append(color).Append(']').Append(Esc(m.Value)).Append("[/color]");
            pos = m.Index + m.Length;
        }
        if (pos < code.Length) sb.Append(Esc(code.Substring(pos)));
        if (comment.Length > 0) sb.Append("[color=#").Append(ColComment).Append(']').Append(Esc(comment)).Append("[/color]");
        return sb.ToString();
    }
}
