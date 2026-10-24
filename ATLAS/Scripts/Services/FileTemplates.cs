using System;
using System.IO;
using System.IO.Compression;
using System.Text;
using System.Xml.Linq;

namespace Atlas;

/// <summary>
/// Creates genuine, openable files for the "New" menu. Every file is built in memory, self-checked (OOXML parts must be
/// well-formed XML, BMP/ZIP headers must be right) and only then written with FileMode.CreateNew, so an existing file can
/// never be overwritten and a broken file is never left on disk.
/// </summary>
public static class FileTemplates
{
    public sealed class Kind
    {
        public readonly string Label, Icon, Ext, DefaultName;
        public Kind(string label, string icon, string ext, string defaultName) { Label = label; Icon = icon; Ext = ext; DefaultName = defaultName; }
    }

    /// <summary>Menu order (Folder and Shortcut are handled separately and come first).</summary>
    public static readonly Kind[] Kinds =
    {
        new("Bitmap image (.bmp)", "image", ".bmp", "New Bitmap Image.bmp"),
        new("Microsoft Word document (.docx)", "document", ".docx", "New Microsoft Word Document.docx"),
        new("Microsoft PowerPoint presentation (.pptx)", "slides", ".pptx", "New Microsoft PowerPoint Presentation.pptx"),
        new("Text document (.txt)", "newfile", ".txt", "New Text Document.txt"),
        new("Microsoft Excel worksheet (.xlsx)", "table", ".xlsx", "New Microsoft Excel Worksheet.xlsx"),
        new("Compressed (zipped) folder (.zip)", "archive", ".zip", "New Compressed (zipped) Folder.zip"),
        new("Markdown file (.md)", "newfile", ".md", "New Markdown File.md"),
    };

    /// <summary>Appends the required extension when the user removed it.</summary>
    public static string WithExtension(string name, string ext)
    {
        name = name?.Trim() ?? "";
        return name.EndsWith(ext, StringComparison.OrdinalIgnoreCase) ? name : name + ext;
    }

    /// <summary>Creates the file. Returns an error message or null. Never overwrites; removes a file it started but could not finish.</summary>
    public static string Create(string dir, string name, string ext, out string created)
    {
        created = null;
        string path = null;
        bool started = false;
        try
        {
            name = WithExtension(name, ext);
            string err = FileOps.ValidateName(name);
            if (err != null) return err;
            if (!Directory.Exists(dir)) return "The current folder no longer exists.";
            path = Path.Combine(dir, name);
            if (FileOps.PathExists(path)) return "An item named \"" + name + "\" already exists here.";

            byte[] bytes = Build(ext, Path.GetFileNameWithoutExtension(name));
            string verr = Verify(ext, bytes);
            if (verr != null) return "Could not build a valid " + ext + " file (" + verr + "). Nothing was created.";

            using (var fs = new FileStream(path, FileMode.CreateNew, FileAccess.Write))
            {
                started = true;
                fs.Write(bytes, 0, bytes.Length);
            }
            created = path;
            return null;
        }
        catch (Exception ex)
        {
            if (started && path != null) { try { File.Delete(path); } catch { } }
            return FileOps.Describe(ex);
        }
    }

    // ------------------------------------------------------------------ builders

    static byte[] Build(string ext, string stem)
    {
        switch (ext.ToLowerInvariant())
        {
            case ".txt": return Array.Empty<byte>();
            case ".md": return new UTF8Encoding(false).GetBytes("# " + stem + "\r\n\r\n");
            case ".bmp": return Bmp(32, 32);
            case ".zip": return EmptyZip();
            case ".docx": return Docx();
            case ".xlsx": return Xlsx();
            case ".pptx": return Pptx();
            default: throw new NotSupportedException("Unsupported file type " + ext);
        }
    }

    /// <summary>A white 24-bit uncompressed bitmap.</summary>
    static byte[] Bmp(int w, int h)
    {
        int row = w * 3, pad = (4 - row % 4) % 4, data = (row + pad) * h, size = 54 + data;
        using var ms = new MemoryStream(size);
        using var bw = new BinaryWriter(ms);
        bw.Write((byte)'B'); bw.Write((byte)'M');
        bw.Write(size); bw.Write(0); bw.Write(54);                       // file header: size, reserved, pixel data offset
        bw.Write(40); bw.Write(w); bw.Write(h);                           // BITMAPINFOHEADER: size, width, height
        bw.Write((short)1); bw.Write((short)24); bw.Write(0); bw.Write(data);
        bw.Write(2835); bw.Write(2835); bw.Write(0); bw.Write(0);
        for (int y = 0; y < h; y++)
        {
            for (int x = 0; x < w; x++) { bw.Write((byte)255); bw.Write((byte)255); bw.Write((byte)255); }
            for (int p = 0; p < pad; p++) bw.Write((byte)0);
        }
        bw.Flush();
        return ms.ToArray();
    }

    /// <summary>The 22-byte end-of-central-directory record: a valid ZIP with no entries (Windows shows an empty zipped folder).</summary>
    static byte[] EmptyZip()
    {
        var b = new byte[22];
        b[0] = 0x50; b[1] = 0x4B; b[2] = 0x05; b[3] = 0x06;
        return b;
    }

    const string Hdr = "<?xml version=\"1.0\" encoding=\"UTF-8\" standalone=\"yes\"?>\r\n";
    const string RelNs = "http://schemas.openxmlformats.org/package/2006/relationships";
    const string OdRel = "http://schemas.openxmlformats.org/officeDocument/2006/relationships";

    static string Rels(params (string id, string type, string target)[] rels)
    {
        var sb = new StringBuilder(Hdr);
        sb.Append("<Relationships xmlns=\"" + RelNs + "\">");
        foreach (var r in rels) sb.Append("<Relationship Id=\"" + r.id + "\" Type=\"" + r.type + "\" Target=\"" + r.target + "\"/>");
        sb.Append("</Relationships>");
        return sb.ToString();
    }

    static string Types(params (string part, string type)[] overrides)
    {
        var sb = new StringBuilder(Hdr);
        sb.Append("<Types xmlns=\"http://schemas.openxmlformats.org/package/2006/content-types\">");
        sb.Append("<Default Extension=\"rels\" ContentType=\"application/vnd.openxmlformats-package.relationships+xml\"/>");
        sb.Append("<Default Extension=\"xml\" ContentType=\"application/xml\"/>");
        foreach (var o in overrides) sb.Append("<Override PartName=\"" + o.part + "\" ContentType=\"" + o.type + "\"/>");
        sb.Append("</Types>");
        return sb.ToString();
    }

    static byte[] Package(params (string name, string xml)[] parts)
    {
        using var ms = new MemoryStream();
        using (var zip = new ZipArchive(ms, ZipArchiveMode.Create, leaveOpen: true))
        {
            foreach (var part in parts)
            {
                var entry = zip.CreateEntry(part.name, CompressionLevel.Optimal);
                using var s = entry.Open();
                var data = new UTF8Encoding(false).GetBytes(part.xml);
                s.Write(data, 0, data.Length);
            }
        }
        return ms.ToArray();
    }

    static byte[] Docx() => Package(
        ("[Content_Types].xml", Types(("/word/document.xml", "application/vnd.openxmlformats-officedocument.wordprocessingml.document.main+xml"))),
        ("_rels/.rels", Rels(("rId1", OdRel + "/officeDocument", "word/document.xml"))),
        ("word/document.xml", Hdr +
            "<w:document xmlns:w=\"http://schemas.openxmlformats.org/wordprocessingml/2006/main\"><w:body><w:p/>" +
            "<w:sectPr><w:pgSz w:w=\"12240\" w:h=\"15840\"/><w:pgMar w:top=\"1440\" w:right=\"1440\" w:bottom=\"1440\" w:left=\"1440\" w:header=\"720\" w:footer=\"720\" w:gutter=\"0\"/></w:sectPr>" +
            "</w:body></w:document>"));

    static byte[] Xlsx() => Package(
        ("[Content_Types].xml", Types(
            ("/xl/workbook.xml", "application/vnd.openxmlformats-officedocument.spreadsheetml.sheet.main+xml"),
            ("/xl/worksheets/sheet1.xml", "application/vnd.openxmlformats-officedocument.spreadsheetml.worksheet+xml"))),
        ("_rels/.rels", Rels(("rId1", OdRel + "/officeDocument", "xl/workbook.xml"))),
        ("xl/workbook.xml", Hdr +
            "<workbook xmlns=\"http://schemas.openxmlformats.org/spreadsheetml/2006/main\" xmlns:r=\"" + OdRel + "\">" +
            "<sheets><sheet name=\"Sheet1\" sheetId=\"1\" r:id=\"rId1\"/></sheets></workbook>"),
        ("xl/_rels/workbook.xml.rels", Rels(("rId1", OdRel + "/worksheet", "worksheets/sheet1.xml"))),
        ("xl/worksheets/sheet1.xml", Hdr +
            "<worksheet xmlns=\"http://schemas.openxmlformats.org/spreadsheetml/2006/main\"><sheetData/></worksheet>"));

    const string PNs = "xmlns:a=\"http://schemas.openxmlformats.org/drawingml/2006/main\" xmlns:r=\"" + OdRel + "\" xmlns:p=\"http://schemas.openxmlformats.org/presentationml/2006/main\"";
    const string EmptyTree =
        "<p:spTree><p:nvGrpSpPr><p:cNvPr id=\"1\" name=\"\"/><p:cNvGrpSpPr/><p:nvPr/></p:nvGrpSpPr>" +
        "<p:grpSpPr><a:xfrm><a:off x=\"0\" y=\"0\"/><a:ext cx=\"0\" cy=\"0\"/><a:chOff x=\"0\" y=\"0\"/><a:chExt cx=\"0\" cy=\"0\"/></a:xfrm></p:grpSpPr></p:spTree>";

    /// <summary>A presentation with a master, one blank layout and a standard Office theme, and no slides (PowerPoint offers to add the first one).</summary>
    static byte[] Pptx()
    {
        string fill = "<a:solidFill><a:schemeClr val=\"phClr\"/></a:solidFill>";
        string theme = Hdr +
            "<a:theme xmlns:a=\"http://schemas.openxmlformats.org/drawingml/2006/main\" name=\"Office Theme\"><a:themeElements>" +
            "<a:clrScheme name=\"Office\">" +
            "<a:dk1><a:sysClr val=\"windowText\" lastClr=\"000000\"/></a:dk1><a:lt1><a:sysClr val=\"window\" lastClr=\"FFFFFF\"/></a:lt1>" +
            "<a:dk2><a:srgbClr val=\"44546A\"/></a:dk2><a:lt2><a:srgbClr val=\"E7E6E6\"/></a:lt2>" +
            "<a:accent1><a:srgbClr val=\"4472C4\"/></a:accent1><a:accent2><a:srgbClr val=\"ED7D31\"/></a:accent2><a:accent3><a:srgbClr val=\"A5A5A5\"/></a:accent3>" +
            "<a:accent4><a:srgbClr val=\"FFC000\"/></a:accent4><a:accent5><a:srgbClr val=\"5B9BD5\"/></a:accent5><a:accent6><a:srgbClr val=\"70AD47\"/></a:accent6>" +
            "<a:hlink><a:srgbClr val=\"0563C1\"/></a:hlink><a:folHlink><a:srgbClr val=\"954F72\"/></a:folHlink></a:clrScheme>" +
            "<a:fontScheme name=\"Office\"><a:majorFont><a:latin typeface=\"Calibri Light\"/><a:ea typeface=\"\"/><a:cs typeface=\"\"/></a:majorFont>" +
            "<a:minorFont><a:latin typeface=\"Calibri\"/><a:ea typeface=\"\"/><a:cs typeface=\"\"/></a:minorFont></a:fontScheme>" +
            "<a:fmtScheme name=\"Office\"><a:fillStyleLst>" + fill + fill + fill + "</a:fillStyleLst>" +
            "<a:lnStyleLst><a:ln w=\"6350\">" + fill + "</a:ln><a:ln w=\"12700\">" + fill + "</a:ln><a:ln w=\"19050\">" + fill + "</a:ln></a:lnStyleLst>" +
            "<a:effectStyleLst><a:effectStyle><a:effectLst/></a:effectStyle><a:effectStyle><a:effectLst/></a:effectStyle><a:effectStyle><a:effectLst/></a:effectStyle></a:effectStyleLst>" +
            "<a:bgFillStyleLst>" + fill + fill + fill + "</a:bgFillStyleLst></a:fmtScheme>" +
            "</a:themeElements></a:theme>";

        string master = Hdr +
            "<p:sldMaster " + PNs + "><p:cSld><p:bg><p:bgRef idx=\"1001\"><a:schemeClr val=\"bg1\"/></p:bgRef></p:bg>" + EmptyTree + "</p:cSld>" +
            "<p:clrMap bg1=\"lt1\" tx1=\"dk1\" bg2=\"lt2\" tx2=\"dk2\" accent1=\"accent1\" accent2=\"accent2\" accent3=\"accent3\" accent4=\"accent4\" accent5=\"accent5\" accent6=\"accent6\" hlink=\"hlink\" folHlink=\"folHlink\"/>" +
            "<p:sldLayoutIdLst><p:sldLayoutId id=\"2147483649\" r:id=\"rId1\"/></p:sldLayoutIdLst></p:sldMaster>";

        string layout = Hdr +
            "<p:sldLayout " + PNs + " type=\"blank\" preserve=\"1\"><p:cSld name=\"Blank\">" + EmptyTree + "</p:cSld>" +
            "<p:clrMapOvr><a:masterClrMapping/></p:clrMapOvr></p:sldLayout>";

        string pres = Hdr +
            "<p:presentation " + PNs + " saveSubsetFonts=\"1\"><p:sldMasterIdLst><p:sldMasterId id=\"2147483648\" r:id=\"rId1\"/></p:sldMasterIdLst>" +
            "<p:sldSz cx=\"12192000\" cy=\"6858000\"/><p:notesSz cx=\"6858000\" cy=\"9144000\"/></p:presentation>";

        return Package(
            ("[Content_Types].xml", Types(
                ("/ppt/presentation.xml", "application/vnd.openxmlformats-officedocument.presentationml.presentation.main+xml"),
                ("/ppt/slideMasters/slideMaster1.xml", "application/vnd.openxmlformats-officedocument.presentationml.slideMaster+xml"),
                ("/ppt/slideLayouts/slideLayout1.xml", "application/vnd.openxmlformats-officedocument.presentationml.slideLayout+xml"),
                ("/ppt/theme/theme1.xml", "application/vnd.openxmlformats-officedocument.theme+xml"))),
            ("_rels/.rels", Rels(("rId1", OdRel + "/officeDocument", "ppt/presentation.xml"))),
            ("ppt/presentation.xml", pres),
            ("ppt/_rels/presentation.xml.rels", Rels(("rId1", OdRel + "/slideMaster", "slideMasters/slideMaster1.xml"), ("rId2", OdRel + "/theme", "theme/theme1.xml"))),
            ("ppt/slideMasters/slideMaster1.xml", master),
            ("ppt/slideMasters/_rels/slideMaster1.xml.rels", Rels(("rId1", OdRel + "/slideLayout", "../slideLayouts/slideLayout1.xml"), ("rId2", OdRel + "/theme", "../theme/theme1.xml"))),
            ("ppt/slideLayouts/slideLayout1.xml", layout),
            ("ppt/slideLayouts/_rels/slideLayout1.xml.rels", Rels(("rId1", OdRel + "/slideMaster", "../slideMasters/slideMaster1.xml"))),
            ("ppt/theme/theme1.xml", theme));
    }

    // ------------------------------------------------------------------ self-check

    /// <summary>Checks what can be checked without Office: structure, headers, and that every XML part is well-formed.</summary>
    static string Verify(string ext, byte[] bytes)
    {
        try
        {
            switch (ext.ToLowerInvariant())
            {
                case ".txt":
                case ".md": return null;
                case ".bmp":
                    return bytes.Length > 54 && bytes[0] == (byte)'B' && bytes[1] == (byte)'M' && BitConverter.ToInt32(bytes, 2) == bytes.Length ? null : "bad bitmap header";
                case ".zip":
                    return bytes.Length == 22 && bytes[0] == 0x50 && bytes[1] == 0x4B && bytes[2] == 0x05 && bytes[3] == 0x06 ? null : "bad zip record";
                default:
                    using (var zip = new ZipArchive(new MemoryStream(bytes), ZipArchiveMode.Read))
                    {
                        if (zip.GetEntry("[Content_Types].xml") == null) return "missing [Content_Types].xml";
                        if (zip.GetEntry("_rels/.rels") == null) return "missing _rels/.rels";
                        foreach (var e in zip.Entries)
                        {
                            if (!e.FullName.EndsWith(".xml", StringComparison.Ordinal) && !e.FullName.EndsWith(".rels", StringComparison.Ordinal)) continue;
                            using var s = e.Open();
                            XDocument.Load(s);
                        }
                    }
                    return null;
            }
        }
        catch (Exception ex) { return ex.Message; }
    }
}
