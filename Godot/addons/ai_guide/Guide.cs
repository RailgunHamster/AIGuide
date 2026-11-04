#if TOOLS
#nullable enable
using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading.Tasks;
using Godot;
using Dict = System.Collections.Generic.Dictionary<string, object?>;
using List = System.Collections.Generic.List<object?>;

namespace AIGuide;

/// <summary>带目标、需要每帧重新定位的对象（高亮 / 引导步骤）。</summary>
public class Tracked
{
    public object? Target;
    /// <summary>录制时留下的退路查询：locator 失效（界面结构变了）时用它再找一次。</summary>
    public object? Fallback;
    public BaseRef? Base;
    public ulong ResolvedMs;
    public Geo? Last;
}

public sealed class Mark : Tracked
{
    public string Text = "";
    public Color Color;
    public string Number = "";
    public ulong ExpireMs;
}

public sealed class GuideStep : Tracked
{
    public string Text = "";
    /// <summary>"click" | "manual" | 条件对象（见 ui_guide 的说明）。</summary>
    public object? Until = "click";
    public string? Clipboard;
    public bool Done;
    public string How = "";
    public ulong DoneMs;
    // 运行时状态
    public int ScrollTries;
    public ulong ScrollMs;
    /// <summary>必须点一下目标（或“下一步”）才算完成，条件成立也不自动跳。</summary>
    public bool Manual;
    public long EnterUnix;
    public bool EntryChecked = true;
    public bool PreSatisfied;
    public ulong FsCheckMs;
}

internal sealed class GuideRun
{
    public string Id = "";
    public string Title = "";
    public List<GuideStep> Steps = new();
    public int Index;
    public string State = "running";
    public bool Dim;
    public bool AutoScroll;
    public List<Dict> Log = new();
    public ulong StartedMs;
    public ulong PendingMs;
    public ulong FinishedMs;
}

/// <summary>
/// 覆盖层与引导状态机。
/// - 高亮(marks)：一次性指示若干目标，可设定时长。
/// - 引导(guide)：一串步骤，逐步高亮并等待完成条件（默认：用户点中目标）。
/// </summary>
[Tool]
public partial class Guide : Node
{
    public static readonly Color ColorStep = new(1.0f, 0.62f, 0.12f);
    public static readonly Color ColorMark = new(0.25f, 0.78f, 1.0f);
    public static readonly Color ColorAgent = new(0.68f, 0.45f, 1.0f);
    public static readonly Color ColorDone = new(0.35f, 0.9f, 0.45f);
    private const int LogLimit = 50;

    public UiTree Ui = null!;
    /// <summary>气泡上“从头开始”按钮的处理函数（Tools 设置：回滚到引导开始时的状态再重来）。</summary>
    public Func<Task>? RestartHandler;
    /// <summary>当前主屏幕（2D/3D/Script…），由 Tools 提供。</summary>
    public Func<string>? MainScreen;

    private readonly Dictionary<ulong, (CanvasLayer Layer, OverlayDraw Draw)> _layers = new();
    private List<Mark> _marks = new();
    private GuideRun? _guide;
    private int _seq;
    private ulong _restartArmedMs;

    private static ulong Now => Time.GetTicksMsec();

    // ============================================================ 目标缓存

    /// <summary>每个 mark / step 都带 target 与缓存的 base；节点失效或到期时重新解析。</summary>
    private Geo GeomOf(Tracked entry, ulong now)
    {
        var b = entry.Base;
        bool stale = entry.ResolvedMs + (ulong)(entry.Target is Dict ? 1000 : 400) < now;
        if (b is { } bb && (!IsInstanceValid(bb.Node) || !bb.Node.IsInsideTree()))
            b = null;
        if (b == null || stale)
        {
            var r = Ui.Resolve(entry.Target);
            // 录制的 locator 失效时（界面结构变了），用录制时留下的退路查询再找一次
            if (!r.Ok && entry.Fallback != null)
            {
                var r2 = Ui.Resolve(entry.Fallback);
                if (r2.Ok)
                    r = r2;
            }
            entry.ResolvedMs = now;
            entry.Base = r.Ok ? r.Base : null;
            entry.Last = r;
            return r;
        }
        var g = Ui.Geometry(b.Value);
        entry.Last = g;
        return g;
    }

    // ============================================================ 每帧

    public override void _Process(double delta)
    {
        var now = Now;
        var perWindow = new Dictionary<Window, List<OverlayItem>>();
        var keep = new List<Mark>();
        foreach (var m in _marks)
        {
            if (m.ExpireMs > 0 && now >= m.ExpireMs)
                continue;
            keep.Add(m);
            var g = GeomOf(m, now);
            if (g.Ok && g.Visible)
                Push(perWindow, g.Window, new OverlayItem { Rect = g.Vis, Color = m.Color, Number = m.Number, Text = m.Text });
        }
        _marks = keep;
        if (_guide != null)
            GuideTick(now, perWindow);
        Flush(perWindow);
    }

    private void Push(Dictionary<Window, List<OverlayItem>> perWindow, Window? w, OverlayItem item)
    {
        w ??= Ui.TreeRoot();
        if (!perWindow.TryGetValue(w, out var l))
            perWindow[w] = l = new List<OverlayItem>();
        l.Add(item);
    }

    private void Flush(Dictionary<Window, List<OverlayItem>> perWindow)
    {
        var used = new HashSet<ulong>();
        foreach (var (w, items) in perWindow)
        {
            var d = DrawFor(w);
            if (d != null)
            {
                d.Items = items;
                d.QueueRedraw();
                used.Add(w.GetInstanceId());
            }
        }
        // 引导进行中时保证主窗口有覆盖层，用于记录用户点击
        if (GuideRunning())
        {
            DrawFor(Ui.TreeRoot());
            used.Add(Ui.TreeRoot().GetInstanceId());
        }
        var dead = new List<ulong>();
        foreach (var (id, e) in _layers)
        {
            if (!IsInstanceValid(e.Layer) || !IsInstanceValid(e.Draw))
            {
                dead.Add(id);
                continue;
            }
            if (!used.Contains(id) && e.Draw.Items.Count > 0)
            {
                e.Draw.Items = new List<OverlayItem>();
                e.Draw.QueueRedraw();
            }
        }
        foreach (var id in dead)
            _layers.Remove(id);
    }

    private OverlayDraw? DrawFor(Window? w)
    {
        if (w == null || !IsInstanceValid(w))
            return null;
        var id = w.GetInstanceId();
        if (_layers.TryGetValue(id, out var e) && IsInstanceValid(e.Layer))
            return e.Draw;
        var layer = new CanvasLayer { Name = "AIGuideOverlay", Layer = 128 };
        layer.SetMeta(UiTree.OwnMeta, true);
        var d = new OverlayDraw { Name = "Draw", Guide = this };
        d.SetMeta(UiTree.OwnMeta, true);
        layer.AddChild(d);
        w.AddChild(layer, false, InternalMode.Back);
        _layers[id] = (layer, d);
        return d;
    }

    public void ClearAll()
    {
        _marks.Clear();
        _guide = null;
        foreach (var (_, e) in _layers)
            if (IsInstanceValid(e.Layer))
                e.Layer.QueueFree();
        _layers.Clear();
    }

    // ============================================================ 高亮

    public List<Geo> AddMarks(List<object?> targets, string text, long durationMs, Color color, bool numbered)
    {
        var now = Now;
        var outL = new List<Geo>();
        for (int i = 0; i < targets.Count; i++)
        {
            var m = new Mark
            {
                Target = targets[i],
                Text = i == 0 ? text : "",
                Color = color,
                Number = numbered && targets.Count > 1 ? (i + 1).ToString() : "",
                ExpireMs = durationMs > 0 ? now + (ulong)durationMs : 0,
            };
            var g = GeomOf(m, now);
            _marks.Add(m);
            outL.Add(g);
        }
        return outL;
    }

    public void ClearMarks() => _marks.Clear();

    // ============================================================ 引导

    public bool GuideRunning() => _guide != null && _guide.State == "running";

    public Dict StartGuide(string title, List<object?> steps, bool dim, bool autoScroll)
    {
        _seq++;
        var list = new List<GuideStep>();
        foreach (var s in steps)
        {
            if (s is Dict d)
            {
                var e = new GuideStep
                {
                    Target = d.GetValueOrDefault("target"),
                    Text = J.Str(d, "text"),
                    Until = d.TryGetValue("until", out var u) && u != null ? u : "click",
                };
                if (d.TryGetValue("fallback", out var fb))
                    e.Fallback = fb;
                if (d.TryGetValue("clipboard", out var cb) && cb != null)
                    e.Clipboard = J.Str(cb);
                list.Add(e);
            }
            else
            {
                list.Add(new GuideStep { Target = s });
            }
        }
        _guide = new GuideRun
        {
            Id = $"g{_seq}",
            Title = title,
            Steps = list,
            Dim = dim,
            AutoScroll = autoScroll,
            StartedMs = Now,
        };
        OnStepEnter();
        _Process(0);
        return Status();
    }

    /// <summary>进入某一步时的准备：需要粘贴的内容提前放进剪贴板。</summary>
    private void OnStepEnter()
    {
        var step = CurrentStep();
        if (step == null)
            return;
        // 文件修改时间只有秒级精度，往前留 1 秒余量
        step.EnterUnix = (long)Time.GetUnixTimeFromSystem() - 1;
        step.EntryChecked = false;
        step.PreSatisfied = false;
        if (step.Clipboard != null)
            DisplayServer.ClipboardSet(step.Clipboard);
    }

    public void StopGuide()
    {
        if (_guide != null && GuideRunning())
        {
            _guide.State = "stopped";
            _guide.FinishedMs = Now;
        }
    }

    public string ControlGuide(string action)
    {
        if (_guide == null)
            return "当前没有引导";
        var steps = _guide.Steps;
        switch (action)
        {
            case "next":
                if (GuideRunning())
                    Advance("agent_next");
                break;
            case "prev":
            {
                int i = Math.Max(0, _guide.Index - 1);
                _guide.Index = i;
                steps[i].Done = false;
                steps[i].ScrollTries = 0;
                // 退回的这一步通常已经满足完成条件（比如节点已经选中了），不能让它立刻又自动跳过去：
                // 改成必须再点一下目标才算完成。
                steps[i].Manual = true;
                _guide.State = "running";
                _guide.PendingMs = 0;
                OnStepEnter();
                break;
            }
            case "stop":
                StopGuide();
                break;
            case "restart":
            case "restart_clean":
                // restart：现场没回滚，已满足的条件不能自动跳步，全部改为点击确认；
                // restart_clean：现场已回滚到引导开始时（Tools 的“从头开始”），按原条件判定。
                foreach (var st in steps)
                {
                    st.Done = false;
                    st.ScrollTries = 0;
                    st.Manual = action == "restart";
                }
                _guide.Index = 0;
                _guide.State = "running";
                _guide.PendingMs = 0;
                OnStepEnter();
                break;
            default:
                return $"未知动作：{action}";
        }
        return "";
    }

    public GuideStep? CurrentStep() => GuideRunning() ? _guide!.Steps[_guide.Index] : null;

    private void Advance(string how)
    {
        var g = _guide!;
        int i = g.Index;
        var step = g.Steps[i];
        step.Done = true;
        step.How = how;
        step.DoneMs = Now;
        g.PendingMs = 0;
        Log(new Dict { ["event"] = "step_done", ["step"] = i + 1, ["how"] = how });
        if (i + 1 >= g.Steps.Count)
        {
            g.State = "done";
            g.FinishedMs = Now;
        }
        else
        {
            g.Index = i + 1;
            g.Steps[i + 1].ScrollTries = 0;
            OnStepEnter();
        }
    }

    private bool CheckUntil(GuideStep step, Geo g, object? cond = null)
    {
        var until = cond ?? step.Until ?? "click";
        if (until is string)
            return false; // "click" 由输入事件判定；"manual" 只能由 guide_control 推进
        if (until is not Dict u)
            return false;
        if (u.TryGetValue("any", out var anyV))
        {
            // 任一条件满足即可（例：按了保存快捷键，或者文件确实已经保存）
            foreach (var c in J.AsList(anyV))
                if (CheckUntil(step, g, c))
                    return true;
            return false;
        }
        if (u.TryGetValue("file_saved", out var fsV))
            return FileSaved(step, fsV as Dict ?? new Dict());
        if (u.TryGetValue("text_contains", out var tcV))
        {
            var spec = tcV as Dict ?? new Dict();
            var rt = Ui.Resolve(spec.GetValueOrDefault("target"));
            if (!rt.Ok)
                return false;
            string have = rt.Node switch
            {
                TextEdit te => te.Text,
                LineEdit le => le.Text,
                _ => Ui.TextOf(rt.Node!),
            };
            return Norm(have).Contains(Norm(J.Str(spec, "text")));
        }
        if (u.ContainsKey("key"))
            return false; // 由输入事件判定，见 OnWindowInput
        if (u.TryGetValue("exists", out var exV))
            return Ui.Resolve(exV).Ok;
        if (u.TryGetValue("visible", out var visV))
        {
            // “已显露”：可见，或者只是被滚出视野（滚一下就能看到）；折叠/未选中的标签页/隐藏窗口都不算。
            var r = Ui.Resolve(visV);
            if (!r.Ok)
                return false;
            if (r.Visible)
                return true;
            var reasons = Ui.HiddenReasons(r);
            return reasons.Count == 1 && J.Str(reasons[0], "type") == "scrolled_out_or_clipped";
        }
        if (u.TryGetValue("selected", out var selV))
        {
            var want = J.Str(selV);
            var root = EditorInterface.Singleton.GetEditedSceneRoot();
            foreach (var n in EditorInterface.Singleton.GetSelection().GetSelectedNodes())
            {
                var rel = root != null ? root.GetPathTo(n).ToString() : n.Name.ToString();
                if (rel == want || n.Name.ToString() == want || n.GetPath().ToString() == want)
                    return true;
            }
            return false;
        }
        if (u.TryGetValue("inspecting", out var inspV))
        {
            var obj = EditorInterface.Singleton.GetInspector().GetEditedObject();
            if (obj == null)
                return false;
            var want = J.Str(inspV);
            if (obj.IsClass(want))
                return true;
            if (obj is Resource res && res.ResourcePath == want)
                return true;
            if (obj is Node node)
            {
                var root2 = EditorInterface.Singleton.GetEditedSceneRoot();
                return root2 != null && root2.GetPathTo(node).ToString() == want;
            }
            return false;
        }
        if (u.TryGetValue("main_screen", out var msV))
            return (MainScreen?.Invoke() ?? "") == J.Str(msV);
        return false;
    }

    /// <summary>
    /// {"file_saved": {"ext": "gdshader", "contains": "..."}}：进入这一步之后，有这种扩展名的文件被写入磁盘，
    /// 且（如果给了 contains）内容里有这段文字。不关心是用哪个快捷键、哪个菜单保存的。
    /// 每 500ms 查一次，只看本步开始后修改过的文件。
    /// </summary>
    private bool FileSaved(GuideStep step, Dict spec)
    {
        var now = Now;
        if (now - step.FsCheckMs < 500)
            return false;
        step.FsCheckMs = now;
        var ext = J.Str(spec, "ext").TrimStart('.');
        var want = Norm(J.Str(spec, "contains"));
        var found = new List<string>();
        CollectFiles("res://", ext, found, 0);
        foreach (var p in found)
        {
            if ((long)FileAccess.GetModifiedTime(p) < step.EnterUnix)
                continue;
            if (want == "" || Norm(FileAccess.GetFileAsString(p)).Contains(want))
                return true;
        }
        return false;
    }

    private static void CollectFiles(string dir, string ext, List<string> outL, int depth)
    {
        if (depth > 12 || outL.Count > 2000)
            return;
        foreach (var f in DirAccess.GetFilesAt(dir))
            if (ext == "" || f.GetExtension() == ext)
                outL.Add(dir.PathJoin(f));
        foreach (var d in DirAccess.GetDirectoriesAt(dir))
        {
            if (d.StartsWith(".") || d == "addons")
                continue;
            CollectFiles(dir.PathJoin(d), ext, outL, depth + 1);
        }
    }

    /// <summary>比较文本时忽略空白差异（缩进、换行风格不同不算不一致）。</summary>
    public static string Norm(string t) => t.Replace(" ", "").Replace("\t", "").Replace("\r", "").Replace("\n", "");

    private void GuideTick(ulong now, Dictionary<Window, List<OverlayItem>> perWindow)
    {
        var gr = _guide!;
        var steps = gr.Steps;
        int total = steps.Count;
        var title = gr.Title;
        if (gr.State == "done")
        {
            if (now - gr.FinishedMs < 2500)
                Push(perWindow, Ui.TreeRoot(), new OverlayItem { Color = ColorDone, Title = "✓ 完成" + (title != "" ? "：" + title : "") });
            return;
        }
        if (!GuideRunning())
            return;
        int i = gr.Index;
        var step = steps[i];
        var g = GeomOf(step, now);
        if (gr.PendingMs > 0 && now - gr.PendingMs > 120)
        {
            Advance("click");
            return;
        }
        // 进入这一步时条件就已经成立（例如 Body 本来就是选中的）：不自动跳过，否则用户会觉得“怎么直接到第 2 步了”。
        // 改为点一下高亮处或“下一步”确认。条件只认“进入这一步之后才变成立”。
        if (!step.EntryChecked)
        {
            step.EntryChecked = true;
            if (step.Until is Dict && CheckUntil(step, g))
            {
                step.Manual = true;
                step.PreSatisfied = true;
            }
        }
        if (!step.Manual && CheckUntil(step, g))
        {
            Advance("condition");
            return;
        }
        var head = $"第 {i + 1}/{total} 步";
        if (title != "")
            head = title + " · " + head;
        if (g.Ok && g.Visible)
        {
            var shownText = step.Text;
            if (step.Clipboard != null && !shownText.Contains("剪贴板"))
                shownText += "\n（内容已放进剪贴板）";
            if (step.PreSatisfied)
                shownText += "\n（这一步现在已经是完成状态：点一下高亮处或「下一步 ▶」继续）";
            var w = g.Window;
            bool tight = g.Kind != "" && g.Kind != "control";
            var item = new OverlayItem { Rect = g.Vis, Color = ColorStep, Number = (i + 1).ToString(), Title = head, Text = shownText, Dim = gr.Dim, Tight = tight, Controls = Controls(i) };
            var root = Ui.TreeRoot();
            if (w != null && w != root && w is Popup)
            {
                // 弹出菜单窗口很小，气泡放在里面必然盖住菜单项：菜单里只画框，气泡由主窗口画在菜单旁边
                var wr = WindowRectIn(w, root);
                if (wr.Size != Vector2.Zero)
                {
                    var bubble = item.Copy();
                    bubble.Rect = new Rect2();
                    bubble.Dim = false;
                    bubble.Anchor = new Rect2(wr.Position + g.Vis.Position, g.Vis.Size);
                    bubble.Avoid = wr;
                    item.Title = "";
                    item.Text = "";
                    item.Controls = null;
                    Push(perWindow, w, item);
                    Push(perWindow, root, bubble);
                    return;
                }
            }
            Push(perWindow, w, item);
            return;
        }
        // 刚出现的目标可能还没完成布局，间隔重试几次；次数有限，避免和用户自己的滚动抢。
        if (g.Ok && gr.AutoScroll && step.ScrollTries < 3 && now - step.ScrollMs > 300)
        {
            step.ScrollTries++;
            step.ScrollMs = now;
            TryScrollIntoView(g);
        }
        var hint = step.Text;
        var reasons = g.Ok ? Ui.HiddenReasons(g) : new List<Dict>();
        hint += reasons.Count > 0 ? $"\n（目标被隐藏：{ReasonText(reasons[0])}）" : "\n（等待目标出现…）";
        Push(perWindow, Ui.TreeRoot(), new OverlayItem { Color = ColorStep, Title = head, Text = hint, Controls = Controls(i) });
    }

    private BubbleControls Controls(int i) => new() { Prev = i > 0, RestartArmed = RestartArmed() };

    private bool RestartArmed() => _restartArmedMs > 0 && Now - _restartArmedMs < 4000;

    /// <summary>
    /// 窗口 w 在窗口 root 坐标系里的矩形（w 为原生窗口时用屏幕坐标换算；
    /// 嵌入式子窗口的 position 本来就相对于嵌入它的视口）。
    /// </summary>
    private static Rect2 WindowRectIn(Window w, Window root)
    {
        if (!w.Visible)
            return new Rect2();
        var pos = new Vector2(w.Position.X, w.Position.Y);
        if (!w.IsEmbedded())
            pos -= new Vector2(root.Position.X, root.Position.Y);
        return new Rect2(pos, new Vector2(w.Size.X, w.Size.Y));
    }

    /// <summary>气泡按钮（OverlayDraw 调用）。</summary>
    public void OnBubbleAction(string action)
    {
        if (!GuideRunning())
            return;
        switch (action)
        {
            case "prev":
                _restartArmedMs = 0;
                Log(new Dict { ["event"] = "user_prev" });
                ControlGuide("prev");
                break;
            case "next":
                _restartArmedMs = 0;
                Log(new Dict { ["event"] = "user_next" });
                Advance("user_next");
                break;
            case "exit":
                _restartArmedMs = 0;
                Log(new Dict { ["event"] = "user_exit" });
                StopGuide();
                break;
            case "restart":
                if (!RestartArmed())
                {
                    // 从头开始会撤销本次引导中的改动，第一次点只是“上膛”，4 秒内再点一次才执行
                    _restartArmedMs = Now;
                    return;
                }
                _restartArmedMs = 0;
                Log(new Dict { ["event"] = "user_restart" });
                if (RestartHandler != null)
                    _ = RestartHandler();
                else
                    ControlGuide("restart");
                break;
        }
    }

    public void NoteRestart(Dict rep)
    {
        if (_guide == null)
            return;
        Log(new Dict { ["event"] = "restarted", ["rolled_back"] = rep.GetValueOrDefault("rolled_back") ?? new List(), ["warnings"] = rep.GetValueOrDefault("warnings") ?? new List() });
    }

    private static string ReasonText(Dict r) => J.Str(r, "type") switch
    {
        "inactive_tab" => $"在未选中的标签页「{J.Str(r, "text")}」里",
        "folded_section" => "在折叠的分组里",
        "collapsed_tree_item" => $"在未展开的「{J.Str(r, "text")}」下面",
        "window_hidden" => "所在窗口未打开",
        "hidden_ancestor" => "所在面板未显示",
        _ => "不在可视区域内",
    };

    /// <summary>只做“滚动到可见”这类不改变数据的显露；展开/切换标签等由步骤本身引导用户完成。</summary>
    public void TryScrollIntoView(Geo g)
    {
        var node = g.Node;
        if (node == null)
            return;
        if (g.Item != null && node is Tree t)
        {
            if (UiTree.TreeItemShown(t, g.Item))
                t.ScrollToItem(g.Item);
            return;
        }
        if (node is ItemList il && g.Kind == "list")
        {
            if (Ui.SubitemIndex(il, "list", g.Key) >= 0)
                il.EnsureCurrentIsVisible();
            return;
        }
        if (node is Control c)
        {
            // 嵌套的滚动容器（如检查器里内联展开的子检查器）要从内到外逐层滚动。
            var p = c.GetParent();
            while (p != null && p is not Window)
            {
                if (p is ScrollContainer sc)
                    sc.EnsureControlVisible(c);
                p = p.GetParent();
            }
        }
    }

    // ============================================================ 输入

    public void OnWindowInput(Window w, InputEvent ev)
    {
        if (!GuideRunning())
            return;
        var step = CurrentStep()!;
        if (ev is InputEventKey k && k.Pressed && !k.Echo)
        {
            var untilK = step.Until;
            if (untilK is Dict ud && ud.TryGetValue("any", out var anyV))
            {
                foreach (var c in J.AsList(anyV))
                {
                    if (c is Dict cd && cd.ContainsKey("key"))
                    {
                        untilK = c;
                        break;
                    }
                }
            }
            if (untilK is Dict kd && kd.TryGetValue("key", out var keyV))
            {
                var want = UiInput.ParseCombo(J.Str(keyV));
                if (want.Ok && (k.Keycode == want.Key || k.PhysicalKeycode == want.Key)
                    && k.CtrlPressed == want.Ctrl && k.ShiftPressed == want.Shift && k.AltPressed == want.Alt)
                {
                    Log(new Dict { ["event"] = "key", ["keys"] = J.Str(keyV) });
                    _guide!.PendingMs = Now;
                }
            }
            return;
        }
        if (ev is not InputEventMouseButton mb)
            return;
        if (!mb.Pressed || !(mb.ButtonIndex == MouseButton.Left || mb.ButtonIndex == MouseButton.Right))
            return;
        var who = ev.Device == UiInput.AgentDevice ? "agent" : "user";
        var g = step.Last;
        bool inside = false;
        if (g != null && g.Ok && g.Visible && g.Window == w)
            inside = g.Vis.Grow(2f).HasPoint(mb.Position);
        var hit = Ui.ControlAt(w, mb.Position);
        var entry = new Dict
        {
            ["event"] = "click",
            ["who"] = who,
            ["button"] = mb.ButtonIndex == MouseButton.Left ? "left" : "right",
            ["on_target"] = inside,
            ["pos"] = new List { Mathf.RoundToInt(mb.Position.X), Mathf.RoundToInt(mb.Position.Y) },
            ["window"] = w != Ui.TreeRoot() ? w.Title : "main",
        };
        if (hit != null)
            entry["hit"] = $"{Ui.RefFor(hit)} {hit.GetClass()} {J.Q(UiTree.ClipText(Ui.TextOf(hit), 40))}";
        Log(entry);
        if (inside && ((step.Until is string us && us == "click") || step.Manual))
            _guide!.PendingMs = Now;
    }

    private void Log(Dict entry)
    {
        if (_guide == null)
            return;
        entry["t_ms"] = (long)(Now - _guide.StartedMs);
        _guide.Log.Add(entry);
        while (_guide.Log.Count > LogLimit)
            _guide.Log.RemoveAt(0);
    }

    // ============================================================ 状态

    public Dict Status()
    {
        if (_guide == null)
            return new Dict { ["state"] = "none" };
        var gr = _guide;
        var outSteps = new List();
        for (int i = 0; i < gr.Steps.Count; i++)
        {
            var s = gr.Steps[i];
            var e = new Dict { ["n"] = i + 1, ["text"] = s.Text, ["done"] = s.Done };
            if (s.Done)
                e["how"] = s.How;
            outSteps.Add(e);
        }
        var outD = new Dict
        {
            ["id"] = gr.Id,
            ["title"] = gr.Title,
            ["state"] = gr.State,
            ["step"] = gr.Index + 1,
            ["total"] = gr.Steps.Count,
            ["steps"] = outSteps,
            ["log"] = gr.Log.Skip(Math.Max(0, gr.Log.Count - 15)).Cast<object?>().ToList(),
        };
        if (GuideRunning())
        {
            var cur = CurrentStep()!;
            var g = cur.Last;
            var c = new Dict
            {
                ["text"] = cur.Text,
                ["target_found"] = g?.Ok ?? false,
                ["target_visible"] = g?.Visible ?? false,
            };
            if (g == null || !g.Ok)
                c["error"] = g?.Error ?? "";
            else if (!g.Visible)
                c["hidden_because"] = Ui.HiddenReasons(g);
            outD["current"] = c;
        }
        return outD;
    }
}
#endif
