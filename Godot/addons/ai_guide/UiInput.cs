#if TOOLS
#nullable enable
using System;
using System.Collections.Generic;
using System.Threading.Tasks;
using Godot;
using Dict = System.Collections.Generic.Dictionary<string, object?>;

namespace AIGuide;

/// <summary>按键组合解析结果。</summary>
public readonly record struct Combo(Key Key, bool Ctrl, bool Shift, bool Alt, bool Meta, string Error)
{
    public bool Ok => Error == "";
}

/// <summary>
/// 合成真实输入事件。事件走 Input.ParseInputEvent → DisplayServer → Window 的完整管线，
/// 和人的鼠标键盘一样会被模态窗口拦截、会让 PopupMenu 正常响应。
/// </summary>
public sealed class UiInput
{
    /// <summary>标记 Agent 合成的事件，覆盖层据此区分“人点的”和“AI 点的”。</summary>
    public const int AgentDevice = 4242;

    private readonly SceneTree _tree;

    public UiInput(SceneTree tree)
    {
        _tree = tree;
    }

    public async Task Frames(int n = 1)
    {
        for (int i = 0; i < n; i++)
            await _tree.ToSignal(_tree, SceneTree.SignalName.ProcessFrame);
    }

    public async Task Seconds(double s)
    {
        await _tree.ToSignal(_tree.CreateTimer(s), SceneTreeTimer.SignalName.Timeout);
    }

    /// <summary>画布坐标 → 窗口像素坐标（输入事件使用的坐标系）。</summary>
    public static Vector2 ToWindow(Window w, Vector2 canvasPos) => w.GetFinalTransform() * canvasPos;

    public void Send(Window w, InputEvent ev)
    {
        // 只给鼠标事件打 Agent 标记：键盘事件要匹配 ui_accept/ui_cancel 等输入动作，
        // 而动作匹配会比较设备号，自定义设备号会让所有快捷键/动作失效。
        if (ev is InputEventMouse)
            ev.Device = AgentDevice;
        var wid = w.GetWindowId();
        if (ev is InputEventFromWindow fw && wid != DisplayServer.InvalidWindowId)
        {
            fw.WindowId = wid;
            Input.ParseInputEvent(ev);
        }
        else
        {
            // 嵌入式子窗口（单窗口模式）没有独立的系统窗口，直接推给视口。
            w.PushInput(ev, false);
        }
    }

    private static void ApplyMods(InputEventWithModifiers ev, Dict? mods)
    {
        ev.CtrlPressed = J.Bool(mods, "ctrl", false);
        ev.ShiftPressed = J.Bool(mods, "shift", false);
        ev.AltPressed = J.Bool(mods, "alt", false);
        ev.MetaPressed = J.Bool(mods, "meta", false);
    }

    public void Move(Window w, Vector2 canvasPos, MouseButtonMask mask = 0)
    {
        var p = ToWindow(w, canvasPos);
        var mm = new InputEventMouseMotion { Position = p, GlobalPosition = p, ButtonMask = mask };
        Send(w, mm);
    }

    public void Button(Window w, Vector2 canvasPos, MouseButton index, bool pressed, bool dbl = false, Dict? mods = null, float factor = 1f)
    {
        var p = ToWindow(w, canvasPos);
        var mb = new InputEventMouseButton
        {
            ButtonIndex = index,
            Pressed = pressed,
            DoubleClick = dbl,
            Position = p,
            GlobalPosition = p,
            Factor = factor,
            ButtonMask = pressed && (int)index <= 5 ? (MouseButtonMask)(1 << ((int)index - 1)) : 0,
        };
        ApplyMods(mb, mods);
        Send(w, mb);
    }

    /// <summary>
    /// 原生窗口的悬停状态由“操作系统认为鼠标在哪个窗口上”决定（Viewport::_update_mouse_over 里的
    /// windowmanager_window_over）。真实光标不在 Godot 窗口上时，合成的移动事件不会更新悬停，
    /// 而按钮要求按下时处于悬停状态。所以需要时把真实光标短暂移到目标上，操作完再移回原处。
    /// </summary>
    public bool MoveOsCursor = true;

    /// <summary>本次操作是否移动过真实光标（写进 ui_act 的报告里）。</summary>
    public bool LastMovedCursor;

    /// <summary>把真实光标移到窗口内某点；返回原来的屏幕坐标（未移动返回 null）。</summary>
    public async Task<Vector2I?> OsCursorTo(Window w, Vector2 canvasPos)
    {
        if (!MoveOsCursor || w.GetWindowId() == DisplayServer.InvalidWindowId)
            return null;
        if (!DisplayServer.HasFeature(DisplayServer.Feature.MouseWarp))
            return null;
        var orig = DisplayServer.MouseGetPosition();
        if (!w.HasFocus() && w is not Popup)
        {
            w.GrabFocus();
            await Frames(2);
        }
        WarpScreen((Vector2I)(new Vector2(w.Position.X, w.Position.Y) + ToWindow(w, canvasPos)));
        await Frames(2);
        return orig;
    }

    public void OsCursorRestore(Vector2I? orig)
    {
        if (orig is { } o)
            WarpScreen(o);
    }

    /// <summary>
    /// 把真实光标移到屏幕绝对坐标。DisplayServer.WarpMouse() 的坐标相对于“最后获得焦点的窗口”，
    /// 弹窗开关之后这个窗口可能已经不是我们以为的那个（甚至已隐藏），所以先移一次、读回实际位置、再按偏差校正。
    /// </summary>
    public void WarpScreen(Vector2I target)
    {
        var fw = Focused(null);
        var guess = fw != null ? target - fw.Position : target;
        DisplayServer.WarpMouse(guess);
        var got = DisplayServer.MouseGetPosition();
        if (got != target)
            DisplayServer.WarpMouse(guess + (target - got));
    }

    private static Window? Focused(Window? fallback)
    {
        foreach (var id in DisplayServer.GetWindowList())
        {
            if (id < 0)
                continue;
            var inst = DisplayServer.WindowGetAttachedInstanceId(id);
            var obj = inst != 0 ? GodotObject.InstanceFromId(inst) : null;
            if (obj is Window w && w.HasFocus())
                return w;
        }
        return fallback;
    }

    /// <summary>
    /// 移动、按下、松开放在同一批事件里，在同一次输入刷新中连续处理（分帧发送时，
    /// 编辑器的 update_mouse_cursor_state() 会用真实光标位置插入移动事件，打断按下/松开）。
    /// 光标移入窗口后，操作系统的“鼠标进入窗口”消息需要一点时间才到；等到编辑器确实认为
    /// 鼠标悬停在该点的控件上再按下，否则按钮会偶发地不响应。
    /// </summary>
    public async Task<bool> WaitHover(Window w, Vector2 canvasPos)
    {
        if (w is PopupMenu)
        {
            await Frames(1);
            return true;
        }
        for (int i = 0; i < 12; i++)
        {
            Move(w, canvasPos);
            await Frames(1);
            if (HoveredAt(w, canvasPos))
                return true;
            await Seconds(0.03);
        }
        return false;
    }

    public static bool HoveredAt(Window w, Vector2 canvasPos)
    {
        var h = w.GuiGetHoveredControl();
        return h != null && h.GetGlobalRect().HasPoint(canvasPos);
    }

    /// <summary>
    /// 让目标进入悬停状态。先只用合成的移动事件：
    /// - 打了引擎补丁 [AI Guide patch]（scene/main/viewport.cpp，见 patches/ai_guide_synthetic_hover.patch）后，
    ///   真实光标不在任何 Godot 窗口上时，合成事件也能更新悬停；
    /// - 未打补丁但真实光标恰好在同一个窗口上时，同样可行。
    /// 都不行时才退回到短暂移动真实光标的做法。返回原光标位置（未移动返回 null）。
    /// </summary>
    public async Task<Vector2I?> EnsureHover(Window w, Vector2 canvasPos)
    {
        LastMovedCursor = false;
        if (w is PopupMenu)
            return null;
        Move(w, canvasPos);
        await Frames(1);
        if (HoveredAt(w, canvasPos) || !MoveOsCursor)
            return null;
        var orig = await OsCursorTo(w, canvasPos);
        if (orig != null)
        {
            LastMovedCursor = true;
            await WaitHover(w, canvasPos);
        }
        return orig;
    }

    public async Task Click(Window w, Vector2 canvasPos, MouseButton index = MouseButton.Left, bool dbl = false, Dict? mods = null)
    {
        var orig = await EnsureHover(w, canvasPos);
        Move(w, canvasPos);
        Button(w, canvasPos, index, true, false, mods);
        Button(w, canvasPos, index, false, false, mods);
        if (dbl)
        {
            Move(w, canvasPos);
            Button(w, canvasPos, index, true, true, mods);
            Button(w, canvasPos, index, false, false, mods);
        }
        await Frames(2);
        OsCursorRestore(orig);
    }

    public async Task Scroll(Window w, Vector2 canvasPos, int amount)
    {
        var index = amount > 0 ? MouseButton.WheelDown : MouseButton.WheelUp;
        for (int i = 0; i < Math.Abs(amount); i++)
        {
            Move(w, canvasPos);
            Button(w, canvasPos, index, true);
            Button(w, canvasPos, index, false);
            await Frames(1);
        }
    }

    public async Task Drag(Window w, Vector2 from, Vector2 to, int steps = 12, Dict? mods = null)
    {
        var orig = await EnsureHover(w, from);
        Move(w, from);
        Button(w, from, MouseButton.Left, true, false, mods);
        await Frames(1);
        for (int i = 1; i <= steps; i++)
        {
            var p = from.Lerp(to, (float)i / steps);
            if (orig != null)
                WarpScreen((Vector2I)(new Vector2(w.Position.X, w.Position.Y) + ToWindow(w, p)));
            Move(w, p, MouseButtonMask.Left);
            await Frames(1);
        }
        Button(w, to, MouseButton.Left, false, false, mods);
        await Frames(2);
        OsCursorRestore(orig);
    }

    public async Task Hover(Window w, Vector2 canvasPos, bool keep = true)
    {
        var orig = await EnsureHover(w, canvasPos);
        Move(w, canvasPos);
        await Frames(2);
        if (!keep)
            OsCursorRestore(orig);
    }

    /// <summary>解析 "ctrl+shift+s" / "Enter" / "F5" 这类组合键。</summary>
    public static Combo ParseCombo(string combo)
    {
        bool ctrl = false, shift = false, alt = false, meta = false;
        var key = Key.None;
        foreach (var raw in combo.Split('+'))
        {
            var part = raw.Trim();
            switch (part.ToLowerInvariant())
            {
                case "ctrl":
                case "control":
                    ctrl = true;
                    break;
                case "shift":
                    shift = true;
                    break;
                case "alt":
                case "option":
                    alt = true;
                    break;
                case "meta":
                case "cmd":
                case "command":
                case "win":
                case "super":
                    meta = true;
                    break;
                default:
                    var k = OS.FindKeycodeFromString(part);
                    if (k == Key.None && part.Length == 1)
                        k = OS.FindKeycodeFromString(part.ToUpperInvariant());
                    if (k == Key.None)
                        return new Combo(Key.None, false, false, false, false, $"无法识别的按键：{part}");
                    key = k;
                    break;
            }
        }
        if (key == Key.None)
            return new Combo(Key.None, false, false, false, false, $"组合键里没有主键：{combo}");
        return new Combo(key, ctrl, shift, alt, meta, "");
    }

    public async Task<string> KeyPress(Window w, string combo)
    {
        var c = ParseCombo(combo);
        if (!c.Ok)
            return c.Error;
        foreach (var pressed in new[] { true, false })
        {
            var ev = new InputEventKey
            {
                Keycode = c.Key,
                PhysicalKeycode = c.Key,
                Pressed = pressed,
                CtrlPressed = c.Ctrl,
                ShiftPressed = c.Shift,
                AltPressed = c.Alt,
                MetaPressed = c.Meta,
            };
            Send(w, ev);
        }
        await Frames(2);
        return "";
    }

    public async Task TypeText(Window w, string text)
    {
        // 按 Unicode 码点逐个输入（中文、emoji 等每个码点一个事件，与 GDScript 版逐字符一致）
        int i = 0;
        while (i < text.Length)
        {
            int code = char.IsSurrogatePair(text, i) ? char.ConvertToUtf32(text, i) : text[i];
            i += char.IsSurrogatePair(text, i) ? 2 : 1;
            if (code == '\r')
                continue;
            if (code == '\n')
            {
                foreach (var pressed in new[] { true, false })
                    Send(w, new InputEventKey { Keycode = Key.Enter, PhysicalKeycode = Key.Enter, Pressed = pressed });
                continue;
            }
            foreach (var pressed in new[] { true, false })
            {
                var ev = new InputEventKey { Unicode = code, Pressed = pressed };
                if (code < 128)
                {
                    var kc = OS.FindKeycodeFromString(((char)code).ToString().ToUpperInvariant());
                    if (kc != Key.None)
                        ev.Keycode = kc;
                }
                Send(w, ev);
            }
        }
        await Frames(2);
    }
}
#endif
