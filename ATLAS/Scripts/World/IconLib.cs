using System;
using System.Collections.Generic;
using Godot;

namespace Atlas;

/// <summary>
/// Runtime-rasterised SVG icons (no asset files needed). Used for the UI and as the default
/// face decals on buildings when <see cref="BuildingAssetConfig"/> has no custom texture.
/// </summary>
public static class IconLib
{
    static readonly Dictionary<string, Texture2D> Cache = new();

    static readonly Dictionary<string, string> Paths = new()
    {
        ["home"] = "<path d='M3 11l9-8 9 8'/><path d='M5 10v10h5v-6h4v6h5V10'/>",
        ["desktop"] = "<rect x='3' y='4' width='18' height='12' rx='2'/><path d='M8 20h8M12 16v4'/>",
        ["document"] = "<path d='M6 3h8l5 5v13H6z'/><path d='M14 3v5h5M9 13h6M9 17h6'/>",
        ["download"] = "<path d='M12 4v11M7 11l5 5 5-5M5 20h14'/>",
        ["image"] = "<rect x='3' y='4' width='18' height='16' rx='2'/><circle cx='9' cy='10' r='1.6'/><path d='M4 18l5-5 4 4 3-3 4 4'/>",
        ["music"] = "<path d='M9 18V6l10-2v12'/><circle cx='7' cy='18' r='2.2'/><circle cx='17' cy='16' r='2.2'/>",
        ["video"] = "<rect x='3' y='5' width='18' height='14' rx='2'/><path d='M10 9l5 3-5 3z'/>",
        ["drive"] = "<rect x='3' y='8' width='18' height='9' rx='2'/><path d='M3 13h18M8 15.2h.01M12 15.2h.01'/><path d='M6 8l1.5-3h9L18 8'/>",
        ["folder"] = "<path d='M3 7a2 2 0 0 1 2-2h4l2 2h8a2 2 0 0 1 2 2v8a2 2 0 0 1-2 2H5a2 2 0 0 1-2-2z'/>",
        ["gear"] = "<circle cx='12' cy='12' r='3'/><path d='M12 2v3M12 19v3M2 12h3M19 12h3M4.9 4.9l2.1 2.1M17 17l2.1 2.1M19.1 4.9L17 7M7 17l-2.1 2.1'/>",
        ["search"] = "<circle cx='11' cy='11' r='6.5'/><path d='M16 16l5 5'/>",
        ["code"] = "<path d='M9 7l-5 5 5 5M15 7l5 5-5 5M13.5 5l-3 14'/>",
        ["archive"] = "<rect x='3' y='4' width='18' height='5' rx='1'/><path d='M5 9v11h14V9M10 13h4'/>",
        ["other"] = "<rect x='4' y='5' width='16' height='14' rx='3'/><circle cx='8.5' cy='12' r='.9'/><circle cx='12' cy='12' r='.9'/><circle cx='15.5' cy='12' r='.9'/>",
        ["back"] = "<path d='M15 5l-7 7 7 7'/>",
        ["forward"] = "<path d='M9 5l7 7-7 7'/>",
        ["up"] = "<path d='M12 20V6M6 12l6-6 6 6'/>",
        ["refresh"] = "<path d='M20 11a8 8 0 1 0-2.3 5.7M20 4v7h-7'/>",
        ["target"] = "<circle cx='12' cy='12' r='3'/><path d='M12 3v4M12 17v4M3 12h4M17 12h4'/>",
        ["external"] = "<path d='M14 4h6v6M20 4l-9 9M18 14v5a1 1 0 0 1-1 1H5a1 1 0 0 1-1-1V7a1 1 0 0 1 1-1h5'/>",
        ["terminal"] = "<rect x='3' y='4' width='18' height='16' rx='2'/><path d='M7 9l3 3-3 3M13 15h4'/>",
        ["copy"] = "<rect x='8' y='8' width='12' height='12' rx='2'/><path d='M16 8V6a2 2 0 0 0-2-2H6a2 2 0 0 0-2 2v8a2 2 0 0 0 2 2h2'/>",
        ["info"] = "<circle cx='12' cy='12' r='9'/><path d='M12 11v6M12 7.5v.5'/>",
        ["minimize"] = "<path d='M5 12h14'/>",
        ["maximize"] = "<rect x='5' y='5' width='14' height='14' rx='1'/>",
        ["close"] = "<path d='M6 6l12 12M18 6L6 18'/>",
        ["plus"] = "<path d='M12 5v14M5 12h14'/>",
        ["link"] = "<path d='M10 14a4 4 0 0 0 5.7 0l3-3a4 4 0 0 0-5.7-5.7l-1 1'/><path d='M14 10a4 4 0 0 0-5.7 0l-3 3a4 4 0 0 0 5.7 5.7l1-1'/>",
        ["table"] = "<rect x='3' y='4' width='18' height='16' rx='2'/><path d='M3 10h18M3 15h18M9 4v16'/>",
        ["slides"] = "<rect x='3' y='4' width='18' height='12' rx='2'/><path d='M12 16v4M8 20h8M10 8l5 2-5 2z'/>",
        ["undo"] = "<path d='M9 14L4 9l5-5'/><path d='M4 9h10a6 6 0 0 1 0 12h-3'/>",
        ["trash"] = "<path d='M4 7h16M10 11v6M14 11v6M6 7l1 13h10l1-13M9 7V4h6v3'/>",
        ["rename"] = "<path d='M4 20h4L19 9l-4-4L4 16z'/><path d='M13.5 6.5l4 4'/>",
        ["cut"] = "<circle cx='6' cy='6' r='2.5'/><circle cx='6' cy='18' r='2.5'/><path d='M8 7.5l12 9M8 16.5l12-9'/>",
        ["paste"] = "<rect x='5' y='5' width='14' height='16' rx='2'/><path d='M9 5V4h6v1M9 11h6M9 15h6'/>",
        ["newfolder"] = "<path d='M3 7a2 2 0 0 1 2-2h4l2 2h8a2 2 0 0 1 2 2v8a2 2 0 0 1-2 2H5a2 2 0 0 1-2-2z'/><path d='M12 10v6M9 13h6'/>",
        ["newfile"] = "<path d='M6 3h8l5 5v13H6z'/><path d='M14 3v5h5M12 12v6M9 15h6'/>",
        ["select"] = "<rect x='4' y='4' width='16' height='16' rx='2' stroke-dasharray='3 2'/>",
        ["check"] = "<path d='M5 12l4 4 10-10'/>",
        ["eye"] = "<path d='M2 12s4-7 10-7 10 7 10 7-4 7-10 7S2 12 2 12z'/><circle cx='12' cy='12' r='3'/>",
        ["app"] = "<rect x='4' y='4' width='7' height='7' rx='1.5'/><rect x='13' y='4' width='7' height='7' rx='1.5'/><rect x='4' y='13' width='7' height='7' rx='1.5'/><rect x='13' y='13' width='7' height='7' rx='1.5'/>",
    };

    const string LogoSvg =
        "<svg xmlns='http://www.w3.org/2000/svg' width='{S}' height='{S}' viewBox='0 0 64 64'>" +
        "<defs><linearGradient id='a' x1='0' y1='0' x2='1' y2='1'><stop offset='0' stop-color='#5EEAFF'/><stop offset='1' stop-color='#2563EB'/></linearGradient></defs>" +
        "<path d='M32 4l24 14v28L32 60 8 46V18z' fill='url(#a)'/>" +
        "<path d='M32 4l24 14-24 14L8 18z' fill='#A5F3FC'/>" +
        "<path d='M32 32v28M8 18l24 14 24-14' stroke='#0B1B4A' stroke-width='2' fill='none'/></svg>";

    public static Texture2D Get(string name, Color color, int px = 48, float stroke = 1.8f)
    {
        string key = $"{name}|{color.ToHtml(false)}|{px}|{stroke}";
        if (Cache.TryGetValue(key, out var cached)) return cached;
        if (!Paths.TryGetValue(name, out var inner)) inner = Paths["other"];
        string svg = $"<svg xmlns='http://www.w3.org/2000/svg' width='{px}' height='{px}' viewBox='0 0 24 24' fill='none' stroke='#{color.ToHtml(false)}' stroke-width='{stroke.ToString(System.Globalization.CultureInfo.InvariantCulture)}' stroke-linecap='round' stroke-linejoin='round'>{inner}</svg>";
        var tex = Rasterise(svg, px);
        Cache[key] = tex;
        return tex;
    }

    public static Texture2D Logo(int px = 64)
    {
        string key = "logo|" + px;
        if (Cache.TryGetValue(key, out var cached)) return cached;
        var tex = Rasterise(LogoSvg.Replace("{S}", px.ToString()), px);
        Cache[key] = tex;
        return tex;
    }

    /// <summary>Default decal for a building / info panel header, by category.</summary>
    public static Texture2D ForEntry(FsEntry e, Color color, int px = 96)
    {
        return Get(IconNameFor(e), color, px, 1.6f);
    }

    public static string IconNameFor(FsEntry e)
    {
        if (e.IsDirectory) return "folder";
        switch (e.Category)
        {
            case FileCategory.Code: return "code";
            case FileCategory.Documents: return "document";
            case FileCategory.Images: return "image";
            case FileCategory.Media: return "video";
            case FileCategory.Archives: return "archive";
            default: return "other";
        }
    }

    static Texture2D Rasterise(string svg, int px)
    {
        try
        {
            var img = new Image();
            var err = img.LoadSvgFromString(svg, 1.0f);
            if (err == Error.Ok) return ImageTexture.CreateFromImage(img);
            GD.PushWarning($"[ATLAS] SVG icon failed to rasterise: {err}");
        }
        catch (Exception ex) { GD.PushWarning($"[ATLAS] SVG icon error: {ex.Message}"); }
        var fallback = Image.CreateEmpty(px, px, false, Image.Format.Rgba8);
        fallback.Fill(new Color(1, 1, 1, 0.0f));
        return ImageTexture.CreateFromImage(fallback);
    }
}
