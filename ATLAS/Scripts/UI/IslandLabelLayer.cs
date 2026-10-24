using System.Collections.Generic;
using Godot;

namespace Atlas;

/// <summary>
/// Floating "Code / 18 items" cards above each island. Pooled PanelContainers repositioned every frame
/// from the island's 3D anchor via Camera3D.UnprojectPosition. Mouse-transparent, so they never block picking.
/// </summary>
public sealed class IslandLabelLayer
{
    public Control Root { get; }

    sealed class Card
    {
        public PanelContainer Panel;
        public Label Title, Subtitle;
        public Vector3 Anchor;
        public bool Active;
    }

    readonly List<Card> _pool = new();
    readonly List<IslandInfo> _islands = new();

    public IslandLabelLayer()
    {
        Root = new Control { Name = "IslandLabels", MouseFilter = Control.MouseFilterEnum.Ignore };
        Root.SetAnchorsPreset(Control.LayoutPreset.FullRect);
    }

    public void SetIslands(IReadOnlyList<IslandInfo> islands)
    {
        _islands.Clear();
        _islands.AddRange(islands);
        while (_pool.Count < _islands.Count) _pool.Add(MakeCard());

        for (int i = 0; i < _pool.Count; i++)
        {
            var c = _pool[i];
            if (i < _islands.Count)
            {
                var isl = _islands[i];
                c.Title.Text = isl.Name;
                c.Subtitle.Text = isl.Count == 1 ? "1 item" : $"{isl.Count} items";
                c.Anchor = isl.Anchor;
                c.Active = true;
                c.Panel.Size = Vector2.Zero;
            }
            else { c.Active = false; c.Panel.Visible = false; }
        }
    }

    public void Update(Camera3D cam)
    {
        if (cam == null) return;
        foreach (var c in _pool)
        {
            if (!c.Active) continue;
            if (cam.IsPositionBehind(c.Anchor)) { c.Panel.Visible = false; continue; }
            var p = cam.UnprojectPosition(c.Anchor);
            var size = c.Panel.GetCombinedMinimumSize();
            c.Panel.Size = size;
            c.Panel.Position = new Vector2(p.X - size.X * 0.5f, p.Y - size.Y);
            c.Panel.Visible = true;
        }
    }

    Card MakeCard()
    {
        var panel = new PanelContainer { MouseFilter = Control.MouseFilterEnum.Ignore, Visible = false };
        panel.AddThemeStyleboxOverride("panel", AtlasTheme.Box(new Color(0.03f, 0.07f, 0.17f, 0.88f), new Color(0.25f, 0.45f, 0.9f, 0.5f), 8, 1, 13, 8));
        var v = new VBoxContainer { MouseFilter = Control.MouseFilterEnum.Ignore };
        v.AddThemeConstantOverride("separation", 0);
        var title = AtlasTheme.MakeLabel("", 15, Colors.White, bold: true);
        var sub = AtlasTheme.MakeLabel("", 12, AtlasTheme.TextDim);
        v.AddChild(title);
        v.AddChild(sub);
        panel.AddChild(v);
        Root.AddChild(panel);
        return new Card { Panel = panel, Title = title, Subtitle = sub };
    }
}
