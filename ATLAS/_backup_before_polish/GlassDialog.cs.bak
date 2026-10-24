using System;
using System.IO;
using Godot;

namespace Atlas;

/// <summary>
/// Modal glass dialog used for Rename / New folder / New file (text prompt with live validation) and for confirmations
/// (permanent delete). Same panel look as the menus. Enter submits, Escape cancels; the dark scrim swallows all clicks
/// so the 3D world cannot be clicked behind it.
/// </summary>
public partial class GlassDialog : Control
{
    public Shader Glass { get; set; }
    public bool IsOpen { get; private set; }
    public event Action<bool> OpenChanged;

    Control _content;
    LineEdit _edit;
    Label _error;
    Button _ok, _cancel;
    Func<string, string> _validate;
    Action<string> _onText;
    Action _onConfirm;
    int _selectLen = -1;
    bool _danger;

    public override void _Ready()
    {
        SetAnchorsPreset(Control.LayoutPreset.FullRect);
        MouseFilter = MouseFilterEnum.Stop;
        Visible = false;
    }

    // ------------------------------------------------------------------ public API

    /// <summary>Text prompt. <paramref name="validate"/> returns an error message or null; OK stays disabled while it returns one.</summary>
    public void Prompt(string title, string message, string initial, string okText, Func<string, string> validate, Action<string> onSubmit, bool selectStemOnly)
    {
        Reset();
        _validate = validate;
        _onText = onSubmit;
        initial ??= "";
        int stem = selectStemOnly ? Path.GetFileNameWithoutExtension(initial).Length : initial.Length;
        _selectLen = stem > 0 ? stem : initial.Length;
        Build(title, message, true, initial, okText, false);
        OnTextChanged(initial);
        Show_();
    }

    public void Confirm(string title, string message, string okText, bool danger, Action onConfirm)
    {
        Reset();
        _onConfirm = onConfirm;
        _danger = danger;
        Build(title, message, false, null, okText, danger);
        Show_();
    }

    public void Cancel() => CloseDialog();

    // ------------------------------------------------------------------ building

    void Build(string title, string message, bool withEdit, string initial, string okText, bool danger)
    {
        var dim = new ColorRect { Color = new Color(0.01f, 0.03f, 0.09f, 0.50f), MouseFilter = MouseFilterEnum.Ignore };
        dim.SetAnchorsPreset(Control.LayoutPreset.FullRect);
        AddChild(dim);
        _content = dim;

        var center = new CenterContainer { MouseFilter = MouseFilterEnum.Ignore };
        center.SetAnchorsPreset(Control.LayoutPreset.FullRect);
        dim.AddChild(center);

        var holder = new PanelContainer { MouseFilter = MouseFilterEnum.Stop };
        var shadow = new StyleBoxFlat { BgColor = new Color(0, 0, 0, 0), ShadowColor = new Color(0, 0, 0, 0.45f), ShadowSize = 26, ShadowOffset = new Vector2(0, 8) };
        shadow.SetCornerRadiusAll(16);
        holder.AddThemeStyleboxOverride("panel", shadow);
        center.AddChild(holder);

        var glass = AtlasTheme.GlassPanel(Glass, 16);
        holder.AddChild(glass);
        var margin = new MarginContainer();
        margin.AddThemeConstantOverride("margin_left", 24);
        margin.AddThemeConstantOverride("margin_right", 24);
        margin.AddThemeConstantOverride("margin_top", 20);
        margin.AddThemeConstantOverride("margin_bottom", 18);
        glass.AddChild(margin);

        var v = new VBoxContainer();
        v.AddThemeConstantOverride("separation", 12);
        margin.AddChild(v);

        v.AddChild(AtlasTheme.MakeLabel(title, 18, Colors.White, bold: true));
        var msg = AtlasTheme.MakeLabel(message, 14, AtlasTheme.TextDim);
        msg.AutowrapMode = TextServer.AutowrapMode.WordSmart;
        msg.CustomMinimumSize = new Vector2(420, 0);
        v.AddChild(msg);

        if (withEdit)
        {
            _edit = new LineEdit { Text = initial, CustomMinimumSize = new Vector2(420, 0) };
            _edit.TextChanged += OnTextChanged;
            _edit.TextSubmitted += _ => Submit();
            v.AddChild(_edit);
            _error = AtlasTheme.MakeLabel("", 12, new Color("F87171"));
            _error.CustomMinimumSize = new Vector2(0, 16);
            v.AddChild(_error);
        }

        var row = new HBoxContainer { Alignment = BoxContainer.AlignmentMode.End };
        row.AddThemeConstantOverride("separation", 10);
        _cancel = MakeButton("Cancel", false, false);
        _cancel.Pressed += Cancel;
        _ok = MakeButton(okText, true, danger);
        _ok.Pressed += Submit;
        row.AddChild(_cancel);
        row.AddChild(_ok);
        v.AddChild(row);
    }

    static Button MakeButton(string text, bool primary, bool danger)
    {
        var b = new Button { Text = text, CustomMinimumSize = new Vector2(96, 36), FocusMode = Control.FocusModeEnum.All };
        Color bg = primary ? (danger ? new Color(0.55f, 0.12f, 0.14f, 0.92f) : new Color(0.07f, 0.42f, 0.56f, 0.92f)) : new Color(1, 1, 1, 0.06f);
        Color bd = primary ? (danger ? new Color("F87171") : AtlasTheme.Cyan) : new Color(1, 1, 1, 0.18f);
        b.AddThemeStyleboxOverride("normal", AtlasTheme.Box(bg, bd, 10, 1, 16, 8));
        b.AddThemeStyleboxOverride("hover", AtlasTheme.Box(bg.Lightened(0.14f), bd, 10, 1, 16, 8));
        b.AddThemeStyleboxOverride("pressed", AtlasTheme.Box(bg.Lightened(0.22f), bd, 10, 1, 16, 8));
        b.AddThemeStyleboxOverride("disabled", AtlasTheme.Box(new Color(1, 1, 1, 0.03f), new Color(1, 1, 1, 0.08f), 10, 1, 16, 8));
        b.AddThemeStyleboxOverride("focus", AtlasTheme.Box(new Color(0, 0, 0, 0), AtlasTheme.Cyan, 10, 2, 16, 8));
        b.AddThemeColorOverride("font_disabled_color", AtlasTheme.TextDim);
        return b;
    }

    // ------------------------------------------------------------------ behaviour

    void OnTextChanged(string text)
    {
        string err = _validate?.Invoke(text);
        if (_error != null) _error.Text = err ?? "";
        if (_ok != null) _ok.Disabled = err != null;
    }

    void Submit()
    {
        if (!IsOpen) return;
        if (_edit != null && _onText != null)
        {
            string text = _edit.Text;
            string err = _validate?.Invoke(text);
            if (err != null) { if (_error != null) _error.Text = err; return; }
            var cb = _onText;
            CloseDialog();
            cb(text);
        }
        else
        {
            var cb = _onConfirm;
            CloseDialog();
            cb?.Invoke();
        }
    }

    void Show_()
    {
        Visible = true;
        IsOpen = true;
        OpenChanged?.Invoke(true);
        // focus after the first layout pass; destructive confirmations focus Cancel so a stray Enter cannot delete
        Callable.From(() =>
        {
            if (!IsOpen) return;
            if (_edit != null)
            {
                _edit.GrabFocus();
                if (_selectLen >= 0) { _edit.Select(0, _selectLen); _edit.CaretColumn = _selectLen; }
            }
            else if (_danger) _cancel?.GrabFocus();
            else _ok?.GrabFocus();
        }).CallDeferred();
    }

    void Reset()
    {
        if (_content != null) { _content.QueueFree(); _content = null; }
        _edit = null; _error = null; _ok = null; _cancel = null;
        _validate = null; _onText = null; _onConfirm = null;
        _selectLen = -1; _danger = false;
    }

    void CloseDialog()
    {
        bool was = IsOpen;
        IsOpen = false;
        Visible = false;
        Reset();
        if (was) OpenChanged?.Invoke(false);
    }

    public override void _Input(InputEvent @event)
    {
        if (!IsOpen) return;
        if (@event is InputEventKey k && k.Pressed && !k.Echo && k.Keycode == Key.Escape)
        {
            CloseDialog();
            GetViewport().SetInputAsHandled();
        }
    }
}
