using Godot;

namespace Atlas;

/// <summary>Dark-navy / cyan Godot Theme + helpers matching the ATLAS reference UI.</summary>
public static class AtlasTheme
{
    public static readonly Color Navy = new Color("050B1E");
    public static readonly Color PanelBg = new Color(0.035f, 0.075f, 0.18f, 0.88f);
    public static readonly Color PanelBorder = new Color(0.20f, 0.40f, 0.85f, 0.55f);
    public static readonly Color Selected = new Color("1D4FD8");
    public static readonly Color SelectedBorder = new Color("4D8DFF");
    public static readonly Color Cyan = new Color("22D3EE");
    public static readonly Color Text = new Color("E8EEFF");
    public static readonly Color TextDim = new Color("8FA3C7");
    public static readonly Color Gold = new Color("FACC15");

    public static StyleBoxFlat Box(Color bg, Color border, int radius, int borderWidth = 1, int padH = 0, int padV = 0)
    {
        var sb = new StyleBoxFlat
        {
            BgColor = bg,
            BorderColor = border,
            CornerRadiusTopLeft = radius, CornerRadiusTopRight = radius,
            CornerRadiusBottomLeft = radius, CornerRadiusBottomRight = radius,
            AntiAliasing = true,
        };
        sb.SetBorderWidthAll(borderWidth);
        sb.ContentMarginLeft = sb.ContentMarginRight = padH;
        sb.ContentMarginTop = sb.ContentMarginBottom = padV;
        return sb;
    }

    public static Theme Build()
    {
        var t = new Theme();
        t.DefaultFontSize = 14;

        // Buttons
        t.SetColor("font_color", "Button", Text);
        t.SetColor("font_hover_color", "Button", Colors.White);
        t.SetColor("font_pressed_color", "Button", Colors.White);
        t.SetColor("font_focus_color", "Button", Text);
        t.SetColor("icon_normal_color", "Button", Colors.White);
        t.SetStylebox("normal", "Button", Box(new Color(1, 1, 1, 0.0f), new Color(0, 0, 0, 0), 10, 0, 10, 8));
        t.SetStylebox("hover", "Button", Box(new Color(0.25f, 0.45f, 1f, 0.16f), new Color(0, 0, 0, 0), 10, 0, 10, 8));
        t.SetStylebox("pressed", "Button", Box(new Color(0.25f, 0.45f, 1f, 0.30f), new Color(0, 0, 0, 0), 10, 0, 10, 8));
        t.SetStylebox("focus", "Button", new StyleBoxEmpty());
        t.SetConstant("h_separation", "Button", 10);

        // OptionButton (dropdown pill)
        var dd = Box(new Color(0.05f, 0.10f, 0.24f, 0.95f), new Color(0.22f, 0.40f, 0.80f, 0.6f), 8, 1, 10, 6);
        var ddHover = Box(new Color(0.08f, 0.16f, 0.34f, 0.98f), new Color(0.35f, 0.58f, 1f, 0.9f), 8, 1, 10, 6);
        t.SetStylebox("normal", "OptionButton", dd);
        t.SetStylebox("hover", "OptionButton", ddHover);
        t.SetStylebox("pressed", "OptionButton", ddHover);
        t.SetStylebox("focus", "OptionButton", new StyleBoxEmpty());
        t.SetColor("font_color", "OptionButton", Text);
        t.SetColor("font_hover_color", "OptionButton", Colors.White);

        // Popup menus (dropdown lists)
        t.SetStylebox("panel", "PopupMenu", Box(new Color(0.04f, 0.08f, 0.2f, 0.98f), new Color(0.25f, 0.45f, 0.9f, 0.7f), 8, 1, 6, 6));
        t.SetStylebox("hover", "PopupMenu", Box(new Color(0.2f, 0.4f, 0.95f, 0.35f), new Color(0, 0, 0, 0), 6, 0));
        t.SetColor("font_color", "PopupMenu", Text);
        t.SetColor("font_hover_color", "PopupMenu", Colors.White);

        // LineEdit
        t.SetStylebox("normal", "LineEdit", Box(new Color(0.03f, 0.07f, 0.17f, 0.9f), new Color(0.22f, 0.4f, 0.8f, 0.5f), 8, 1, 10, 6));
        t.SetStylebox("focus", "LineEdit", Box(new Color(0.03f, 0.08f, 0.2f, 0.95f), SelectedBorder, 8, 1, 10, 6));
        t.SetColor("font_color", "LineEdit", Text);
        t.SetColor("font_placeholder_color", "LineEdit", TextDim);
        t.SetColor("caret_color", "LineEdit", Cyan);

        // Labels / checkboxes / spinbox
        t.SetColor("font_color", "Label", Text);
        t.SetColor("font_color", "CheckBox", Text);
        t.SetColor("font_color", "CheckButton", Text);
        t.SetStylebox("normal", "PanelContainer", new StyleBoxEmpty());

        // Tooltips
        t.SetStylebox("panel", "TooltipPanel", Box(new Color(0.03f, 0.07f, 0.18f, 0.97f), PanelBorder, 6, 1, 8, 5));
        t.SetColor("font_color", "TooltipLabel", Text);
        return t;
    }

    /// <summary>Bold, letter-spaced font used for headings.</summary>
    public static FontVariation Heading(float embolden = 0.6f, int spacing = 0)
    {
        return new FontVariation { BaseFont = ThemeDB.FallbackFont, VariationEmbolden = embolden, SpacingGlyph = spacing };
    }

    public static Label MakeLabel(string text, int size, Color color, bool bold = false, int spacing = 0)
    {
        var l = new Label { Text = text, MouseFilter = Control.MouseFilterEnum.Ignore };
        l.AddThemeFontSizeOverride("font_size", size);
        l.AddThemeColorOverride("font_color", color);
        if (bold || spacing != 0) l.AddThemeFontOverride("font", Heading(bold ? 0.6f : 0f, spacing));
        return l;
    }

    /// <summary>
    /// Creates a glass panel: a PanelContainer whose first child is a ColorRect running frosted_glass.gdshader.
    /// MouseFilter is Stop (Godot's container default is Pass): mouse buttons and the wheel over a panel are consumed by the HUD
    /// and never reach WorldCamera / WorldInteraction, so scrolling a panel cannot zoom the camera or click through to the world.
    /// </summary>
    public static PanelContainer GlassPanel(Shader shader, int radius = 14)
    {
        var panel = new PanelContainer { MouseFilter = Control.MouseFilterEnum.Stop };
        panel.AddThemeStyleboxOverride("panel", new StyleBoxEmpty());
        var rect = new ColorRect { Name = "GlassBackdrop", Color = Colors.White, MouseFilter = Control.MouseFilterEnum.Ignore };
        panel.AddChild(rect);
        if (shader != null)
        {
            var mat = new ShaderMaterial { Shader = shader };
            mat.SetShaderParameter("corner_radius", (float)radius);
            rect.Material = mat;
            panel.Resized += () => mat.SetShaderParameter("rect_size", panel.Size);
        }
        else
        {
            // Shader missing: plain translucent fallback so the UI stays readable.
            rect.Color = new Color(0.035f, 0.075f, 0.18f, 0.88f);
        }
        return panel;
    }
}
