#if TOOLS
#nullable enable
using System;
using System.Collections.Generic;
using Godot;

namespace AIGuide;

/// <summary>气泡底部按钮的状态。</summary>
public sealed class BubbleControls
{
    public bool Prev;
    public bool RestartArmed;
}

/// <summary>覆盖层上要画的一项（高亮框 + 序号 + 气泡）。</summary>
public sealed class OverlayItem
{
    /// <summary>目标在本窗口内的矩形（空 = 不画框）。</summary>
    public Rect2 Rect;
    public Color Color = Colors.Orange;
    public string Number = "";
    public string Title = "";
    public string Text = "";
    public bool Dim;
    /// <summary>子项（菜单项、树项）用细框，不外扩到相邻项上。</summary>
    public bool Tight;
    /// <summary>气泡要指向的目标（本窗口坐标）；目标在弹出菜单里时由主窗口代画气泡。</summary>
    public Rect2? Anchor;
    /// <summary>气泡不能盖住的区域（弹出菜单所在的原生窗口，它总在最上层）。</summary>
    public Rect2 Avoid;
    /// <summary>有它就在气泡底部画按钮（上一步/下一步/从头开始/退出）。</summary>
    public BubbleControls? Controls;

    public OverlayItem Copy() => (OverlayItem)MemberwiseClone();
}

/// <summary>
/// 每个窗口一个的覆盖层绘制控件：高亮框、序号徽标、步骤气泡、聚光灯遮罩。
/// 不通过 GUI 拦截鼠标；在 _Input 里只吞掉落在气泡上的点击（气泡按钮），
/// 其余输入转交给 Guide 用于判定步骤完成。
/// </summary>
[Tool]
public partial class OverlayDraw : Control
{
    public Guide? Guide;
    public List<OverlayItem> Items = new();

    private Font? _font;
    private int _fontSize = 14;
    private float _scale = 1f;
    // 本帧画出的气泡与按钮，供 _Input 命中测试
    private readonly List<Rect2> _bubbles = new();
    private readonly List<(Rect2 Rect, string Action)> _buttons = new();
    private Vector2 _mouse = new(-1, -1);

    public override void _Ready()
    {
        MouseFilter = MouseFilterEnum.Ignore;
        SetAnchorsAndOffsetsPreset(LayoutPreset.FullRect);
        _scale = EditorInterface.Singleton.GetEditorScale();
        var theme = EditorInterface.Singleton.GetEditorTheme();
        if (theme != null && theme.HasFont("main", "EditorFonts"))
        {
            _font = theme.GetFont("main", "EditorFonts");
            _fontSize = theme.GetFontSize("main_size", "EditorFonts");
        }
        if (_font == null)
        {
            _font = ThemeDB.FallbackFont;
            _fontSize = (int)(14 * _scale);
        }
    }

    public override void _Process(double delta)
    {
        if (Items.Count > 0)
            QueueRedraw();
        else if (_bubbles.Count > 0 || _buttons.Count > 0)
        {
            _bubbles.Clear();
            _buttons.Clear();
        }
    }

    public override void _Input(InputEvent ev)
    {
        if (ev is InputEventMouseMotion mm)
            _mouse = mm.Position;
        // AI 合成的点击（ui_act）不受气泡影响，照常落到编辑器控件上
        if (ev is InputEventMouseButton mb && Items.Count > 0 && ev.Device != UiInput.AgentDevice)
        {
            foreach (var b in _buttons)
            {
                if (b.Rect.HasPoint(mb.Position))
                {
                    GetViewport().SetInputAsHandled();
                    if (mb.Pressed && mb.ButtonIndex == MouseButton.Left && Guide != null && IsInstanceValid(Guide))
                        Guide.OnBubbleAction(b.Action);
                    return;
                }
            }
            foreach (var r in _bubbles)
            {
                if (r.HasPoint(mb.Position))
                {
                    // 点在气泡上：不让它穿透到气泡下面的编辑器控件
                    GetViewport().SetInputAsHandled();
                    return;
                }
            }
        }
        if (Guide != null && IsInstanceValid(Guide))
            Guide.OnWindowInput(GetWindow(), ev);
    }

    public override void _Draw()
    {
        _bubbles.Clear();
        _buttons.Clear();
        var font = _font!;
        var vp = GetViewportRect().Size;
        var t = Time.GetTicksMsec() / 1000.0;
        var pulse = (float)((Math.Sin(t * 5.0) + 1.0) * 0.5);
        foreach (var it in Items)
        {
            var r = it.Rect;
            var col = it.Color;
            bool hasRect = r.Size.X > 0.5f && r.Size.Y > 0.5f;
            if (hasRect && it.Dim)
            {
                var shade = new Color(0, 0, 0, 0.55f);
                var g0 = r.Grow((it.Tight ? 0f : 4f) * _scale);
                DrawRect(new Rect2(0, 0, vp.X, g0.Position.Y), shade);
                DrawRect(new Rect2(0, g0.End.Y, vp.X, vp.Y - g0.End.Y), shade);
                DrawRect(new Rect2(0, g0.Position.Y, g0.Position.X, g0.Size.Y), shade);
                DrawRect(new Rect2(g0.End.X, g0.Position.Y, vp.X - g0.End.X, g0.Size.Y), shade);
            }
            if (hasRect)
            {
                // 子项（菜单项等）之间没有空隙，外扩会压到相邻项，看起来像“歪了”，所以只在内侧呼吸
                var g = it.Tight ? r.Grow(-pulse * 2f * _scale) : r.Grow((3f + pulse * 3f) * _scale);
                float bw = (it.Tight ? 2f : 3f) * _scale;
                DrawRect(g, new Color(col, 0.16f), true);
                DrawRect(g.Grow(-bw / 2f), col, false, bw);
                if (it.Number != "")
                {
                    float rad = 11f * _scale;
                    // 子项的徽标放在框外左侧，不挡住条目的图标和文字
                    var c = it.Tight && g.Position.X > rad * 1.6f
                        ? new Vector2(g.Position.X - rad * 0.6f, g.GetCenter().Y)
                        : g.Position + new Vector2(rad * 0.3f, rad * 0.3f);
                    DrawCircle(c, rad, col);
                    var ns = font.GetStringSize(it.Number, HorizontalAlignment.Left, -1, _fontSize);
                    DrawString(font, c + new Vector2(-ns.X / 2f, font.GetAscent(_fontSize) / 2f - 1f), it.Number, HorizontalAlignment.Left, -1, _fontSize, Colors.Black);
                }
            }
            if (it.Text != "" || it.Title != "")
            {
                var anchor = it.Anchor ?? (hasRect ? r : new Rect2());
                DrawBubble(anchor, it.Avoid, it.Title, it.Text, col, vp, it.Controls);
            }
        }
    }

    /// <summary>气泡底部的按钮（上一步 / 下一步 / 从头开始 / 退出）</summary>
    private static List<(string Action, string Label)> ControlLabels(BubbleControls controls)
    {
        var outL = new List<(string, string)>();
        if (controls.Prev)
            outL.Add(("prev", "◀ 上一步"));
        outL.Add(("next", "下一步 ▶"));
        outL.Add(("restart", controls.RestartArmed ? "确认重来？会撤销本次改动" : "↺ 从头开始"));
        outL.Add(("exit", "✕ 退出"));
        return outL;
    }

    private void DrawBubble(Rect2 r, Rect2 avoid, string title, string text, Color col, Vector2 vp, BubbleControls? controls)
    {
        var font = _font!;
        float pad = 10f * _scale;
        float maxW = Mathf.Min(400f * _scale, vp.X - 16f);
        float innerW = maxW - pad * 2f;
        var bodySize = Vector2.Zero;
        if (text != "")
            bodySize = font.GetMultilineStringSize(text, HorizontalAlignment.Left, innerW, _fontSize);
        var titleSize = Vector2.Zero;
        if (title != "")
        {
            titleSize = font.GetStringSize(title, HorizontalAlignment.Left, -1, _fontSize);
            titleSize.Y = font.GetHeight(_fontSize) + 4f * _scale;
        }
        // 按钮行
        int small = Math.Max(10, (int)(_fontSize * 0.85f));
        var btnPad = new Vector2(8f, 4f) * _scale;
        float btnGap = 6f * _scale;
        var btns = new List<(string Action, string Label, Vector2 Size)>();
        float btnH = 0, btnWTotal = 0;
        if (controls != null)
        {
            foreach (var (action, label) in ControlLabels(controls))
            {
                var sz = font.GetStringSize(label, HorizontalAlignment.Left, -1, small) + btnPad * 2f;
                btns.Add((action, label, sz));
                btnH = Mathf.Max(btnH, sz.Y);
                btnWTotal += sz.X;
            }
            btnWTotal += btnGap * (btns.Count - 1);
        }
        float btnRowH = btns.Count > 0 ? btnH + 8f * _scale : 0f;
        float bw = Mathf.Min(maxW, Mathf.Max(Mathf.Max(bodySize.X, titleSize.X), btnWTotal) + pad * 2f);
        float bh = bodySize.Y + titleSize.Y + btnRowH + pad * 2f;
        float gap = 12f * _scale;
        float x, y;
        string arrow = ""; // up / down / left / right：箭头在气泡的哪一边
        if (r.Size == Vector2.Zero)
        {
            x = (vp.X - bw) / 2f;
            y = 48f * _scale;
        }
        else if (avoid.Size != Vector2.Zero)
        {
            // 目标在弹出菜单（独立的原生窗口，总在最上层）里：气泡放在菜单旁边，绝不能和菜单重叠
            y = Mathf.Clamp(r.GetCenter().Y - bh / 2f, 8f, Mathf.Max(8f, vp.Y - bh - 8f));
            if (avoid.End.X + gap + bw <= vp.X - 8f)
            {
                x = avoid.End.X + gap;
                arrow = "left";
            }
            else if (avoid.Position.X - gap - bw >= 8f)
            {
                x = avoid.Position.X - gap - bw;
                arrow = "right";
            }
            else
            {
                x = Mathf.Clamp(avoid.Position.X, 8f, Mathf.Max(8f, vp.X - bw - 8f));
                y = avoid.End.Y + gap + bh <= vp.Y - 8f ? avoid.End.Y + gap : Mathf.Max(8f, avoid.Position.Y - gap - bh);
            }
        }
        else
        {
            x = Mathf.Clamp(r.Position.X, 8f, Mathf.Max(8f, vp.X - bw - 8f));
            y = r.End.Y + gap;
            arrow = "up";
            if (y + bh > vp.Y - 8f)
            {
                y = r.Position.Y - gap - bh;
                arrow = "down";
            }
            if (y < 8f)
            {
                // 上下都放不下：放到目标右侧或左侧；仍然放不下时宁可放在窗口顶部，也不盖住目标
                y = Mathf.Clamp(r.GetCenter().Y - bh / 2f, 8f, Mathf.Max(8f, vp.Y - bh - 8f));
                if (r.End.X + gap + bw <= vp.X - 8f)
                {
                    x = r.End.X + gap;
                    arrow = "left";
                }
                else if (r.Position.X - gap - bw >= 8f)
                {
                    x = r.Position.X - gap - bw;
                    arrow = "right";
                }
                else
                {
                    arrow = "";
                }
            }
        }
        var box = new Rect2(x, y, bw, bh);
        _bubbles.Add(box);
        var sb = new StyleBoxFlat
        {
            BgColor = new Color(0.08f, 0.09f, 0.12f, 0.96f),
            BorderColor = col,
            ShadowColor = new Color(0, 0, 0, 0.4f),
            ShadowSize = (int)(6 * _scale),
        };
        sb.SetBorderWidthAll((int)(2 * _scale));
        sb.SetCornerRadiusAll((int)(6 * _scale));
        DrawStyleBox(sb, box);
        float s = 7f * _scale;
        switch (arrow)
        {
            case "up":
            {
                float ax = Mathf.Clamp(r.GetCenter().X, box.Position.X + 14f * _scale, box.End.X - 14f * _scale);
                DrawColoredPolygon(new[] { new Vector2(ax - s, box.Position.Y), new Vector2(ax + s, box.Position.Y), new Vector2(ax, box.Position.Y - s) }, col);
                break;
            }
            case "down":
            {
                float ax = Mathf.Clamp(r.GetCenter().X, box.Position.X + 14f * _scale, box.End.X - 14f * _scale);
                DrawColoredPolygon(new[] { new Vector2(ax - s, box.End.Y), new Vector2(ax + s, box.End.Y), new Vector2(ax, box.End.Y + s) }, col);
                break;
            }
            case "left":
            {
                float ay = Mathf.Clamp(r.GetCenter().Y, box.Position.Y + 14f * _scale, box.End.Y - 14f * _scale);
                DrawColoredPolygon(new[] { new Vector2(box.Position.X, ay - s), new Vector2(box.Position.X, ay + s), new Vector2(box.Position.X - s, ay) }, col);
                break;
            }
            case "right":
            {
                float ay = Mathf.Clamp(r.GetCenter().Y, box.Position.Y + 14f * _scale, box.End.Y - 14f * _scale);
                DrawColoredPolygon(new[] { new Vector2(box.End.X, ay - s), new Vector2(box.End.X, ay + s), new Vector2(box.End.X + s, ay) }, col);
                break;
            }
        }
        float cy = y + pad;
        if (title != "")
        {
            DrawString(font, new Vector2(x + pad, cy + font.GetAscent(_fontSize)), title, HorizontalAlignment.Left, innerW, _fontSize, col);
            cy += titleSize.Y;
        }
        if (text != "")
        {
            DrawMultilineString(font, new Vector2(x + pad, cy + font.GetAscent(_fontSize)), text, HorizontalAlignment.Left, innerW, _fontSize, -1, new Color(0.95f, 0.95f, 0.97f));
            cy += bodySize.Y;
        }
        if (btns.Count > 0)
        {
            float bx = x + pad;
            float by = cy + 8f * _scale;
            foreach (var (action, label, sz) in btns)
            {
                var br = new Rect2(bx, by, sz.X, btnH);
                bool hot = br.HasPoint(_mouse);
                bool armed = action == "restart" && controls!.RestartArmed;
                var bg = new Color(col, hot ? 0.35f : 0.18f);
                if (armed)
                    bg = new Color(0.9f, 0.3f, 0.25f, hot ? 0.55f : 0.4f);
                DrawRect(br, bg, true);
                DrawRect(br, new Color(col, 0.8f), false, 1f * _scale);
                DrawString(font, new Vector2(bx + btnPad.X, by + btnPad.Y + font.GetAscent(small)), label, HorizontalAlignment.Left, -1, small, new Color(0.95f, 0.95f, 0.97f));
                _buttons.Add((br, action));
                bx += sz.X + btnGap;
            }
        }
    }
}
#endif
