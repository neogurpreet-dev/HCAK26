using System;
using System.Globalization;
using Godot;

namespace Atlas;

/// <summary>
/// Hover / selection information card (bottom-right in the reference): icon + name, Type / Size /
/// Modified / Accessed / Usage / Location rows and Open / Reveal / Properties actions.
/// Plain helper class: owns a glass PanelContainer that AtlasUI parents into the HUD.
/// </summary>
public sealed class InfoPanelView
{
    public PanelContainer Root { get; }
    public event Action<FsEntry> OpenClicked, RevealClicked, PropertiesClicked;

    readonly TextureRect _icon;
    readonly Label _name;
    readonly Label _type, _size, _modified, _accessed, _usage, _location;
    FsEntry _entry;
    Tween _fade;

    public InfoPanelView(Shader glass)
    {
        Root = AtlasTheme.GlassPanel(glass, 16);
        Root.Name = "HoverInfoPanel";
        Root.CustomMinimumSize = new Vector2(300, 0);
        Root.Visible = false;

        var margin = new MarginContainer();
        foreach (var side in new[] { "margin_left", "margin_right" }) margin.AddThemeConstantOverride(side, 18);
        margin.AddThemeConstantOverride("margin_top", 16);
        margin.AddThemeConstantOverride("margin_bottom", 14);
        Root.AddChild(margin);

        var v = new VBoxContainer();
        v.AddThemeConstantOverride("separation", 10);
        margin.AddChild(v);

        // header
        var head = new HBoxContainer();
        head.AddThemeConstantOverride("separation", 12);
        _icon = new TextureRect { CustomMinimumSize = new Vector2(34, 38), ExpandMode = TextureRect.ExpandModeEnum.IgnoreSize, StretchMode = TextureRect.StretchModeEnum.KeepAspectCentered, MouseFilter = Control.MouseFilterEnum.Ignore };
        _name = AtlasTheme.MakeLabel("", 17, Colors.White, bold: true);
        _name.SizeFlagsHorizontal = Control.SizeFlags.ExpandFill;
        _name.ClipText = true;
        _name.TextOverrunBehavior = TextServer.OverrunBehavior.TrimEllipsis;
        head.AddChild(_icon);
        head.AddChild(_name);
        v.AddChild(head);
        v.AddChild(Divider());

        // rows
        var grid = new GridContainer { Columns = 2 };
        grid.AddThemeConstantOverride("h_separation", 14);
        grid.AddThemeConstantOverride("v_separation", 8);
        _type = Row(grid, "Type");
        _size = Row(grid, "Size");
        _modified = Row(grid, "Modified");
        _accessed = Row(grid, "Accessed");
        _usage = Row(grid, "Usage");
        _location = Row(grid, "Location");
        v.AddChild(grid);
        v.AddChild(Divider());

        // actions
        var actions = new HBoxContainer { Alignment = BoxContainer.AlignmentMode.Center };
        actions.AddThemeConstantOverride("separation", 4);
        actions.AddChild(ActionButton("Open", "external", () => { if (_entry != null) OpenClicked?.Invoke(_entry); }));
        actions.AddChild(ActionButton("Reveal", "folder", () => { if (_entry != null) RevealClicked?.Invoke(_entry); }));
        actions.AddChild(ActionButton("Properties", "info", () => { if (_entry != null) PropertiesClicked?.Invoke(_entry); }));
        v.AddChild(actions);
    }

    static Control Divider() => new ColorRect { Color = new Color(1, 1, 1, 0.12f), CustomMinimumSize = new Vector2(0, 1), MouseFilter = Control.MouseFilterEnum.Ignore };

    static Label Row(GridContainer grid, string caption)
    {
        var c = AtlasTheme.MakeLabel(caption, 13, AtlasTheme.TextDim);
        c.CustomMinimumSize = new Vector2(64, 0);
        var val = AtlasTheme.MakeLabel("", 13, AtlasTheme.Text);
        val.SizeFlagsHorizontal = Control.SizeFlags.ExpandFill;
        val.ClipText = true;
        val.TextOverrunBehavior = TextServer.OverrunBehavior.TrimEllipsis;
        grid.AddChild(c);
        grid.AddChild(val);
        return val;
    }

    static Button ActionButton(string text, string icon, Action onClick)
    {
        var b = new Button { Text = text, Icon = IconLib.Get(icon, Colors.White, 20, 1.8f), ExpandIcon = false, TooltipText = text, CustomMinimumSize = new Vector2(88, 36) };
        b.AddThemeConstantOverride("h_separation", 6);
        b.AddThemeFontSizeOverride("font_size", 13);
        b.Pressed += onClick;
        return b;
    }

    public void Show(FsEntry e, string usageTier)
    {
        _entry = e;
        var cat = FileCategory.CategoryColor(e.Category);
        _icon.Texture = IconLib.ForEntry(e, cat.Lerp(Colors.White, 0.35f), 64);
        _name.Text = e.Name;
        _name.TooltipText = e.Name;
        _type.Text = FileCategory.DescribeType(e.Extension, e.IsDirectory);
        _size.Text = FormatSize(e);
        _modified.Text = FormatDate(e.HasModified, e.Modified);
        _accessed.Text = e.HasAccessed ? FormatDate(true, e.Accessed) : "Unavailable";
        _usage.Text = usageTier;
        _usage.AddThemeColorOverride("font_color", usageTier == "High" ? AtlasTheme.Gold : usageTier == "Medium" ? AtlasTheme.Cyan : AtlasTheme.TextDim);
        _location.Text = Elide(e.Path, 30);
        _location.TooltipText = e.Path;

        if (!Root.Visible)
        {
            Root.Modulate = new Color(1, 1, 1, 0);
            Root.Visible = true;
            _fade?.Kill();
            _fade = Root.CreateTween();
            _fade.TweenProperty(Root, "modulate:a", 1.0f, 0.15f);
        }
    }

    public void Hide()
    {
        _entry = null;
        _fade?.Kill();
        Root.Visible = false;
    }

    public static string FormatSize(FsEntry e)
    {
        long s = e.EffectiveSize;
        if (e.IsDirectory && s < 0) return "Calculating...";
        if (s < 0) return "Unavailable";
        return FormatBytes(s);
    }

    public static string FormatBytes(long bytes)
    {
        string[] u = { "B", "KB", "MB", "GB", "TB" };
        double v = bytes;
        int i = 0;
        while (v >= 1024 && i < u.Length - 1) { v /= 1024; i++; }
        return i == 0 ? $"{bytes} B" : string.Format(CultureInfo.InvariantCulture, "{0:0.##} {1}", v, u[i]);
    }

    static string FormatDate(bool has, DateTime t) => has ? t.ToString("dd MMM yyyy, HH:mm", CultureInfo.InvariantCulture) : "Unavailable";

    public static string Elide(string path, int max)
    {
        if (path.Length <= max) return path;
        int head = Math.Max(6, max / 2 - 2), tail = max - head - 3;
        return path.Substring(0, head) + "..." + path.Substring(path.Length - tail);
    }
}
