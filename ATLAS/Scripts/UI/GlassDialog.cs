using System;
using System.Collections.Generic;
using System.IO;
using Godot;

namespace Atlas;

/// <summary>One text field of a <see cref="GlassDialog.Form"/>.</summary>
public sealed class FormField
{
    public string Label;
    public string Value = "";
    public string Placeholder = "";
    /// <summary>When set, the field follows the other fields (e.g. a shortcut name derived from its target) until the user edits it.</summary>
    public Func<string[], string> Derive;
    /// <summary>When set, a Browse button is shown; it calls this with a setter that writes the chosen text into the field.</summary>
    public Action<Action<string>> Browse;
}

/// <summary>A program offered by the Open With dialog.</summary>
public sealed class AppChoice
{
    public string Name = "";
    public string Exe = "";
}

/// <summary>
/// Modal glass dialog for Rename / New file / New shortcut / Open With / confirmations. It always appears at the TOP CENTRE
/// of the viewport (below the 84 px top bar), is clamped inside the viewport and repositions itself when the window is resized.
/// Same glass panel look as the context menu. Enter submits, Escape cancels; the dark scrim swallows clicks so the 3D world
/// cannot be used behind it. Opening a dialog also closes any open context menu (wired in MainWorld).
/// </summary>
public partial class GlassDialog : Control
{
    const float TopInset = 100f;   // top bar is 84 px tall
    const float SideInset = 8f;

    public Shader Glass { get; set; }
    public bool IsOpen { get; private set; }
    public event Action<bool> OpenChanged;

    Control _content;
    PanelContainer _holder;
    Label _error;
    Button _ok, _cancel;

    // form state
    FormField[] _fields;
    LineEdit[] _edits;
    bool[] _touched;
    Func<string[], string> _validate;
    Action<string[]> _onForm;
    int _selectLen = -1;

    // confirm state
    Action _onConfirm;
    bool _danger;

    // program picker state
    Action<AppChoice> _onApp;
    List<AppChoice> _apps;
    List<Button> _appButtons;
    VBoxContainer _appList;
    Label _appEmpty;
    int _appIndex = -1;

    public override void _Ready()
    {
        SetAnchorsPreset(Control.LayoutPreset.FullRect);
        MouseFilter = MouseFilterEnum.Stop;
        Visible = false;
        GetViewport().SizeChanged += () => { if (IsOpen) Reposition(); };
    }

    // ------------------------------------------------------------------ public API

    /// <summary>Single text field. <paramref name="validate"/> returns an error message or null; OK stays disabled while it returns one.</summary>
    public void Prompt(string title, string message, string initial, string okText, Func<string, string> validate, Action<string> onSubmit, bool selectStemOnly)
    {
        initial ??= "";
        int stem = selectStemOnly ? Path.GetFileNameWithoutExtension(initial).Length : initial.Length;
        Form(title, message, okText, new[] { new FormField { Value = initial } },
            v => validate?.Invoke(v[0]), v => onSubmit?.Invoke(v[0]), stem > 0 ? stem : initial.Length);
    }

    /// <summary>Several labelled text fields (used by Create shortcut).</summary>
    public void Form(string title, string message, string okText, FormField[] fields, Func<string[], string> validate, Action<string[]> onSubmit, int selectLenFirstField = -1)
    {
        Reset();
        _fields = fields;
        _edits = new LineEdit[fields.Length];
        _touched = new bool[fields.Length];
        _validate = validate;
        _onForm = onSubmit;
        _selectLen = selectLenFirstField;

        var v = BeginFrame(title, message);
        for (int i = 0; i < fields.Length; i++)
        {
            int idx = i;
            var f = fields[i];
            if (!string.IsNullOrEmpty(f.Label)) v.AddChild(AtlasTheme.MakeLabel(f.Label, 13, AtlasTheme.TextDim));
            var row = new HBoxContainer();
            row.AddThemeConstantOverride("separation", 8);
            v.AddChild(row);
            var le = new LineEdit { Text = f.Value ?? "", PlaceholderText = f.Placeholder ?? "", CustomMinimumSize = new Vector2(420, 0), SizeFlagsHorizontal = Control.SizeFlags.ExpandFill };
            le.TextChanged += _ => OnFieldEdited(idx);   // raised only for user edits, not for programmatic Text changes
            le.TextSubmitted += _ => Submit();
            row.AddChild(le);
            _edits[i] = le;
            if (f.Browse != null)
            {
                var b = MakeButton("Browse…", false, false);
                b.Pressed += () => f.Browse(s => SetField(idx, s));
                row.AddChild(b);
            }
        }
        _error = AtlasTheme.MakeLabel("", 12, new Color("F87171"));
        _error.CustomMinimumSize = new Vector2(0, 16);
        v.AddChild(_error);
        AddButtons(v, okText, false, null);
        Revalidate();
        Show_();
    }

    public void Confirm(string title, string message, string okText, bool danger, Action onConfirm)
    {
        Reset();
        _onConfirm = onConfirm;
        _danger = danger;
        var v = BeginFrame(title, message);
        AddButtons(v, okText, danger, null);
        Show_();
    }

    /// <summary>
    /// Open With: a list of suggested programs plus Browse (add any .exe) and Windows… (native chooser fallback).
    /// <paramref name="onBrowse"/> should call <see cref="AddApp"/> with the program the user picked.
    /// </summary>
    public void PickApp(string title, string message, IReadOnlyList<AppChoice> apps, Action<AppChoice> onChosen, Action onBrowse, Action onNative)
    {
        Reset();
        _onApp = onChosen;
        _apps = new List<AppChoice>();
        _appButtons = new List<Button>();

        var v = BeginFrame(title, message);
        var scroll = new ScrollContainer { CustomMinimumSize = new Vector2(420, 220), HorizontalScrollMode = ScrollContainer.ScrollMode.Disabled };
        _appList = new VBoxContainer { SizeFlagsHorizontal = Control.SizeFlags.ExpandFill };
        _appList.AddThemeConstantOverride("separation", 2);
        scroll.AddChild(_appList);
        v.AddChild(scroll);
        _appEmpty = AtlasTheme.MakeLabel("No suggested programs found. Use Browse… to choose one.", 13, AtlasTheme.TextDim);
        _appList.AddChild(_appEmpty);

        AddButtons(v, "Open", false, left =>
        {
            var browse = MakeButton("Browse…", false, false);
            browse.Pressed += () => onBrowse?.Invoke();
            left.AddChild(browse);
            var native = MakeButton("Windows…", false, false);
            native.TooltipText = "Use the native Windows Open with dialog";
            native.Pressed += () => { var cb = onNative; CloseDialog(); cb?.Invoke(); };
            left.AddChild(native);
        });
        _ok.Disabled = true;
        foreach (var a in apps) AddApp(a, false);
        if (_apps.Count > 0) SelectApp(0);
        Show_();
    }

    /// <summary>Adds (or selects) a program in the open picker. Ignored when the picker is not open.</summary>
    public void AddApp(AppChoice a, bool select = true)
    {
        if (_appList == null || a == null) return; // null once the dialog has been closed
        int existing = _apps.FindIndex(x => string.Equals(x.Exe, a.Exe, StringComparison.OrdinalIgnoreCase));
        if (existing >= 0) { if (select) SelectApp(existing); return; }
        if (_appEmpty != null) { _appEmpty.Visible = false; _appEmpty.QueueFree(); _appEmpty = null; }

        int idx = _apps.Count;
        _apps.Add(a);
        var b = new Button
        {
            Text = a.Name, TooltipText = a.Exe, Icon = IconLib.Get("app", Colors.White, 20, 1.8f), ExpandIcon = false,
            Alignment = HorizontalAlignment.Left, CustomMinimumSize = new Vector2(0, 36), SizeFlagsHorizontal = Control.SizeFlags.ExpandFill,
        };
        b.AddThemeConstantOverride("h_separation", 10);
        b.Pressed += () => SelectApp(idx);
        b.GuiInput += ev =>
        {
            if (ev is InputEventMouseButton mb && mb.ButtonIndex == MouseButton.Left && mb.Pressed && mb.DoubleClick) { SelectApp(idx); Submit(); }
        };
        _appList.AddChild(b);
        _appButtons.Add(b);
        if (select) SelectApp(idx);
    }

    public void Cancel() => CloseDialog();

    // ------------------------------------------------------------------ frame

    VBoxContainer BeginFrame(string title, string message)
    {
        var dim = new ColorRect { Color = new Color(0.01f, 0.03f, 0.09f, 0.50f), MouseFilter = MouseFilterEnum.Ignore };
        dim.SetAnchorsPreset(Control.LayoutPreset.FullRect);
        AddChild(dim);
        _content = dim;

        // positioned manually by Reposition(): top centre of the viewport
        _holder = new PanelContainer { MouseFilter = MouseFilterEnum.Stop };
        var shadow = new StyleBoxFlat { BgColor = new Color(0, 0, 0, 0), ShadowColor = new Color(0, 0, 0, 0.45f), ShadowSize = 26, ShadowOffset = new Vector2(0, 8) };
        shadow.SetCornerRadiusAll(16);
        _holder.AddThemeStyleboxOverride("panel", shadow);
        dim.AddChild(_holder);

        var glass = AtlasTheme.GlassPanel(Glass, 16);
        _holder.AddChild(glass);
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
        if (!string.IsNullOrEmpty(message))
        {
            var msg = AtlasTheme.MakeLabel(message, 14, AtlasTheme.TextDim);
            msg.AutowrapMode = TextServer.AutowrapMode.WordSmart;
            msg.CustomMinimumSize = new Vector2(420, 0);
            v.AddChild(msg);
        }
        return v;
    }

    void AddButtons(VBoxContainer v, string okText, bool danger, Action<HBoxContainer> addLeft)
    {
        var row = new HBoxContainer();
        row.AddThemeConstantOverride("separation", 10);
        addLeft?.Invoke(row);
        row.AddChild(new Control { SizeFlagsHorizontal = Control.SizeFlags.ExpandFill });
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

    // ------------------------------------------------------------------ placement

    /// <summary>Top centre, below the top bar, completely inside the viewport. Called on open, after layout, and when the window resizes.</summary>
    void Reposition()
    {
        if (_holder == null || !GodotObject.IsInstanceValid(_holder)) return;
        var vp = GetViewportRect().Size;
        var size = _holder.GetCombinedMinimumSize();
        _holder.Size = size;
        float x = Mathf.Clamp((vp.X - size.X) * 0.5f, SideInset, Mathf.Max(SideInset, vp.X - size.X - SideInset));
        float maxY = Mathf.Max(SideInset, vp.Y - size.Y - SideInset);
        float y = Mathf.Min(TopInset, maxY);
        _holder.Position = new Vector2(x, y);
    }

    // ------------------------------------------------------------------ behaviour

    string[] Values()
    {
        var v = new string[_edits.Length];
        for (int i = 0; i < v.Length; i++) v[i] = _edits[i]?.Text ?? "";
        return v;
    }

    void SetField(int idx, string text)
    {
        if (!IsOpen || _edits == null || idx >= _edits.Length || _edits[idx] == null) return;
        _edits[idx].Text = text ?? "";
        OnFieldEdited(idx);
    }

    void OnFieldEdited(int idx)
    {
        if (_edits == null) return;
        _touched[idx] = true;
        for (int k = 0; k < _fields.Length; k++)
            if (k != idx && _fields[k].Derive != null && !_touched[k] && _edits[k] != null)
                _edits[k].Text = _fields[k].Derive(Values()) ?? "";
        Revalidate();
    }

    void Revalidate()
    {
        if (_edits == null) return;
        string err = _validate?.Invoke(Values());
        if (_error != null) _error.Text = err ?? "";
        if (_ok != null) _ok.Disabled = err != null;
    }

    void SelectApp(int idx)
    {
        _appIndex = idx;
        for (int j = 0; j < _appButtons.Count; j++)
        {
            if (j == idx) _appButtons[j].AddThemeStyleboxOverride("normal", AtlasTheme.Box(new Color(0.13f, 0.60f, 0.85f, 0.25f), new Color(0.13f, 0.83f, 0.93f, 0.35f), 8, 1, 10, 6));
            else _appButtons[j].RemoveThemeStyleboxOverride("normal");
        }
        if (_ok != null) _ok.Disabled = false;
    }

    void Submit()
    {
        if (!IsOpen) return;
        if (_edits != null && _onForm != null)
        {
            var vals = Values();
            string err = _validate?.Invoke(vals);
            if (err != null) { if (_error != null) _error.Text = err; return; }
            var cb = _onForm;
            CloseDialog();
            cb(vals);
        }
        else if (_onApp != null)
        {
            if (_appIndex < 0 || _appIndex >= _apps.Count) return;
            var app = _apps[_appIndex];
            var cb = _onApp;
            CloseDialog();
            cb(app);
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
        GetParent()?.MoveChild(this, -1);   // above every other HUD element
        Reposition();
        OpenChanged?.Invoke(true);
        // after the first layout pass: re-measure (wrapped text changes the height) and give keyboard focus;
        // destructive confirmations focus Cancel so a stray Enter cannot delete anything
        Callable.From(() =>
        {
            if (!IsOpen) return;
            Reposition();
            if (_edits != null && _edits.Length > 0 && _edits[0] != null)
            {
                _edits[0].GrabFocus();
                if (_selectLen >= 0) { _edits[0].Select(0, _selectLen); _edits[0].CaretColumn = _selectLen; }
            }
            else if (_danger) _cancel?.GrabFocus();
            else if (_ok != null && !_ok.Disabled) _ok.GrabFocus();
            else _cancel?.GrabFocus();
        }).CallDeferred();
    }

    void Reset()
    {
        if (_content != null) { _content.Visible = false; _content.QueueFree(); _content = null; }
        _holder = null; _error = null; _ok = null; _cancel = null;
        _fields = null; _edits = null; _touched = null; _validate = null; _onForm = null; _selectLen = -1;
        _onConfirm = null; _danger = false;
        _onApp = null; _apps = null; _appButtons = null; _appList = null; _appEmpty = null; _appIndex = -1;
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
