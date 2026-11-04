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

/// <summary>MCP 方法分发与全部工具实现。</summary>
[Tool]
public partial class Tools : Node
{
    public const string Version = "0.2.0";
    private static readonly string[] Protocols = { "2025-11-25", "2025-06-18", "2025-03-26", "2024-11-05" };
    private const int ToolTimeoutMs = 15000;

    private const string Instructions = """
Godot editor UI bridge. You can SEE, OPERATE and VISUALLY GUIDE every part of the live Godot editor UI.
Workflow:
1. editor_state for context; scene_tree / editor_exec for semantic facts (what node holds the material, etc.).
2. ui_snapshot (whole editor or a subtree) or ui_find (by text/class/property) to get element refs like e12 (controls) or e12.40 (tree items, tabs, list items, menu items, menu bar entries). Refs stay valid while the element exists. ui_describe gives a persistent locator and why an element is hidden (inactive tab, folded inspector section, collapsed tree item, closed window).
3a. To DO it: ui_act (real mouse/keyboard events, same path as a human). Pass show=true to visibly demonstrate before clicking.
3b. To TEACH the user: ui_guide with ordered steps (each step = target + short instruction). The overlay highlights the current step and advances when the user clicks it (or when an `until` condition holds). Poll guide_status to follow progress; if the user goes off-path, re-plan and call ui_guide again. Steps whose targets do not exist yet (popup menu items, inspector properties of a node not selected yet) are fine: they resolve when they appear, so prefer locators or text queries over refs for those.
4. editor_screenshot to verify visually.
Teaching a multi-step procedure (recommended, fast for the user): REHEARSE first.
  rehearsal_start → perform the whole procedure yourself with ui_act (use action="paste" for code/long text) →
  rehearsal_stop(save_as=..., texts=[...]) which rolls the editor back automatically and turns the recording into guide steps
  with stable locators and completion conditions → the user follows them (start_guide=true, or recipe_play later).
  Saved recipes are reusable: check recipe_list before rehearsing again. The user can also start saved recipes from the editor's top menu "AI > 引导教程".
Write guide text in the user's language and keep each step short.
""";

    public UiTree Ui = null!;
    public UiInput Input = null!;
    public Guide Guide = null!;
    public Rehearsal Rehearsal = null!;
    /// <summary>
    /// 引导开始时的现场快照（复用演练的快照/回滚）。用户点气泡上的“从头开始”时回滚到这里。
    /// 与 Rehearsal 是两个独立实例：它不录制 ui_act。
    /// </summary>
    public Rehearsal Checkpoint = null!;
    public string MainScreenName = "";
    private bool _restarting;

    public override void _Ready()
    {
        Ui = new UiTree();
        Input = new UiInput(GetTree());
        Guide = new Guide { Name = "AIGuideOverlay", Ui = Ui };
        Guide.MainScreen = () => MainScreenName;
        Guide.RestartHandler = async () => await GuideRestart();
        AddChild(Guide);
        Rehearsal = new Rehearsal(Ui, Input, this);
        Checkpoint = new Rehearsal(Ui, Input, this);
    }

    /// <summary>所有开始引导的入口都走这里：先给现场拍快照，再开始。</summary>
    public Dict StartGuide(string title, List<object?> steps, bool dim, bool autoScroll)
    {
        Guide.ClearMarks();
        if (Checkpoint.Active)
            Checkpoint.Cancel();
        Checkpoint.Start(title);
        return Guide.StartGuide(title, steps, dim, autoScroll);
    }

    /// <summary>从头开始：回滚到引导开始时的现场，再从第 1 步开始。</summary>
    public async Task<Dict> GuideRestart()
    {
        if (_restarting)
            return new Dict { ["error"] = "正在回滚" };
        _restarting = true;
        try
        {
            var rep = new Dict();
            if (Checkpoint.Active)
            {
                rep = await Checkpoint.Stop(true);
                Checkpoint.Start(J.Str(Guide.Status(), "title"));
                Guide.ControlGuide("restart_clean");
            }
            else
            {
                Guide.ControlGuide("restart");
            }
            Guide.NoteRestart(rep);
            return rep;
        }
        finally
        {
            _restarting = false;
        }
    }

    public void Shutdown()
    {
        Guide?.ClearAll();
        if (Rehearsal?.Active == true)
            Rehearsal.Cancel();
        if (Checkpoint?.Active == true)
            Checkpoint.Cancel();
    }

    public void OnMainScreenChanged(string screenName) => MainScreenName = screenName;

    // ============================================================ JSON-RPC

    public async Task<object?> HandleRpc(Dict msg)
    {
        var id = msg.GetValueOrDefault("id");
        var method = J.Str(msg, "method");
        var prms = msg.GetValueOrDefault("params") as Dict ?? new Dict();
        switch (method)
        {
            case "initialize":
            {
                var want = J.Str(prms, "protocolVersion", Protocols[1]);
                return Ok(id, new Dict
                {
                    ["protocolVersion"] = Protocols.Contains(want) ? want : Protocols[1],
                    ["capabilities"] = new Dict { ["tools"] = new Dict { ["listChanged"] = false } },
                    ["serverInfo"] = new Dict { ["name"] = "godot-ai-guide", ["title"] = "Godot AI Guide", ["version"] = Version },
                    ["instructions"] = Instructions,
                });
            }
            case "ping":
                return Ok(id, new Dict());
            case "tools/list":
                return Ok(id, new Dict { ["tools"] = ToolDefsList() });
            case "tools/call":
            {
                var name = J.Str(prms, "name");
                var args = prms.GetValueOrDefault("arguments") as Dict ?? new Dict();
                var result = await CallTool(name, args);
                return Ok(id, result);
            }
            case "resources/list":
                return Ok(id, new Dict { ["resources"] = new List() });
            case "resources/templates/list":
                return Ok(id, new Dict { ["resourceTemplates"] = new List() });
            case "prompts/list":
                return Ok(id, new Dict { ["prompts"] = new List() });
        }
        if (method.StartsWith("notifications/"))
            return null;
        return new Dict { ["jsonrpc"] = "2.0", ["id"] = id, ["error"] = new Dict { ["code"] = -32601, ["message"] = $"Method not found: {method}" } };
    }

    private static Dict Ok(object? id, Dict result) => new() { ["jsonrpc"] = "2.0", ["id"] = id, ["result"] = result };

    public static Dict Text(string s) => new() { ["content"] = new List { new Dict { ["type"] = "text", ["text"] = s } }, ["isError"] = false };

    public static Dict Json(object? d) => Text(J.Stringify(d, true));

    public static Dict Fail(string s) => new() { ["content"] = new List { new Dict { ["type"] = "text", ["text"] = s } }, ["isError"] = true };

    private static List? _defsCache;

    private static List ToolDefsList()
    {
        _defsCache ??= J.AsList(J.Parse(ToolDefs.Json.Replace("$TARGET", ToolDefs.TargetSchema.Trim())));
        return (List)J.Clone(_defsCache)!;
    }

    /// <summary>带超时的工具调用：工具迟迟不返回（弹窗挡住、焦点切换等）时也给 Agent 一个明确的错误。</summary>
    public async Task<Dict> CallTool(string toolName, Dict a)
    {
        var task = CallToolSafe(toolName, a);
        var t0 = Time.GetTicksMsec();
        while (!task.IsCompleted && Time.GetTicksMsec() - t0 < ToolTimeoutMs)
            await Input.Frames(1);
        if (task.IsCompleted)
            return task.Result;
        return Fail($"{toolName} 执行超时（{ToolTimeoutMs / 1000} 秒）。可能有弹窗或模态对话框挡住了操作：用 editor_state 查看打开的窗口，必要时先关闭它们。");
    }

    private async Task<Dict> CallToolSafe(string toolName, Dict a)
    {
        try
        {
            return await CallToolInner(toolName, a);
        }
        catch (Exception e)
        {
            GD.PushWarning($"[AI Guide] 工具 {toolName} 出错：{e}");
            return Fail($"{toolName} 出错：{e.GetType().Name}: {e.Message}");
        }
    }

    private async Task<Dict> CallToolInner(string toolName, Dict a)
    {
        switch (toolName)
        {
            case "editor_state": return EditorState();
            case "scene_tree": return SceneTreeTool(a);
            case "ui_snapshot": return UiSnapshot(a);
            case "ui_find": return UiFind(a);
            case "ui_describe": return UiDescribe(a);
            case "ui_act": return await UiAct(a);
            case "ui_highlight": return UiHighlight(a);
            case "ui_guide": return UiGuide(a);
            case "guide_status": return Json(Guide.Status());
            case "guide_control": return await GuideControl(a);
            case "editor_screenshot": return await Screenshot(a);
            case "editor_exec": return await Exec(a);
            case "ai_guide_reload": return ReloadPlugin();
            case "rehearsal_start": return Json(Rehearsal.Start(J.Str(a, "title")));
            case "rehearsal_stop": return await RehearsalStop(a);
            case "recipe_list": return Json(Rehearsal.ListRecipes());
            case "recipe_play": return RecipePlay(a);
        }
        return Fail($"未知工具：{toolName}");
    }

    // ============================================================ 热重载（开发用）

    /// <summary>
    /// C# 版的热重载：后台执行 dotnet build；编辑器的 HotReloadAssemblyWatcher 每 0.5 秒检查一次程序集，
    /// 发现新的构建就重新加载程序集，插件通过 ISerializationListener 拆掉并重建自己（见 AIGuidePlugin）。
    /// 正在执行的请求会被丢弃，所以先回复，再开始构建。
    /// </summary>
    private Dict ReloadPlugin()
    {
        var csproj = FindCsproj();
        if (csproj == "")
            return Fail("找不到工程的 .csproj，无法重新构建。");
        _ = Task.Run(() =>
        {
            var output = new Godot.Collections.Array();
            var code = OS.Execute("dotnet", new[] { "build", csproj, "-nologo", "-v:q" }, output, true);
            var text = string.Join("", output.Select(v => v.AsString()));
            Callable.From(() =>
            {
                if (code == 0)
                    GD.Print("[AI Guide] 构建完成，编辑器将自动重新加载程序集");
                else
                    GD.PushError($"[AI Guide] 构建失败（{code}）：\n{UiTree.Left(text, 4000)}");
            }).CallDeferred();
        });
        return Text($"已开始重新构建 {csproj.GetFile()}；构建成功后编辑器会自动重新加载程序集并重建插件，约 5～15 秒。之后再调用。");
    }

    private static string FindCsproj()
    {
        var dir = ProjectSettings.GlobalizePath("res://");
        foreach (var f in DirAccess.GetFilesAt("res://"))
            if (f.EndsWith(".csproj"))
                return dir.PathJoin(f);
        return "";
    }

    // ============================================================ 实现

    public List<Window> VisibleWindows()
    {
        var outL = new List<Window>();
        var stack = new Stack<Node>();
        stack.Push(Ui.TreeRoot());
        while (stack.Count > 0)
        {
            var n = stack.Pop();
            if (Ui.IsOwn(n) || n is SubViewport)
                continue;
            if (n is Window w)
            {
                if (!w.Visible)
                    continue;
                outL.Add(w);
            }
            foreach (var ch in n.GetChildren(true))
                stack.Push(ch);
        }
        return outL;
    }

    private Dict WindowInfo(Window w) => new()
    {
        ["ref"] = Ui.RefFor(w),
        ["class"] = w.GetClass(),
        ["title"] = w.Title,
        ["main"] = w == Ui.TreeRoot(),
        ["modal"] = w.Exclusive,
        ["focused"] = w.HasFocus(),
    };

    private static List SelectedNodes() => Rehearsal.SelectionPaths().Cast<object?>().ToList();

    private string FocusDesc(Control? focus) =>
        focus == null ? "" : $"{Ui.RefFor(focus)} {focus.GetClass()} {J.Q(UiTree.ClipText(Ui.TextOf(focus), 40))}";

    private Dict EditorState()
    {
        var ei = EditorInterface.Singleton;
        var root = ei.GetEditedSceneRoot();
        var insp = ei.GetInspector().GetEditedObject();
        Dict? inspInfo = null;
        if (insp != null)
        {
            inspInfo = new Dict { ["class"] = insp.GetClass() };
            if (insp is Node inode && root != null)
                inspInfo["path"] = root.GetPathTo(inode).ToString();
            else if (insp is Resource res)
                inspInfo["path"] = res.ResourcePath;
        }
        var focus = Ui.TreeRoot().GuiGetFocusOwner();
        var gs = Guide.Status();
        return Json(new Dict
        {
            ["godot"] = Engine.GetVersionInfo()["string"].AsString(),
            ["project"] = ProjectSettings.GetSetting("application/config/name", "").AsString(),
            ["project_path"] = ProjectSettings.GlobalizePath("res://"),
            ["edited_scene"] = root?.SceneFilePath,
            ["scene_root"] = root != null ? new Dict { ["name"] = root.Name.ToString(), ["class"] = root.GetClass() } : null,
            ["open_scenes"] = ei.GetOpenScenes().Cast<object?>().ToList(),
            ["selected_nodes"] = SelectedNodes(),
            ["inspecting"] = inspInfo,
            ["main_screen"] = MainScreenName,
            ["windows"] = VisibleWindows().Select(w => (object?)WindowInfo(w)).ToList(),
            ["focused_control"] = focus != null ? FocusDesc(focus) : null,
            ["editor_language"] = ei.GetEditorSettings().GetSetting("interface/editor/editor_language").AsString(),
            ["editor_scale"] = ei.GetEditorScale(),
            ["guide"] = new Dict { ["state"] = gs.GetValueOrDefault("state") ?? "none", ["step"] = gs.GetValueOrDefault("step") ?? 0, ["total"] = gs.GetValueOrDefault("total") ?? 0 },
        });
    }

    private Dict SceneTreeTool(Dict a)
    {
        var root = EditorInterface.Singleton.GetEditedSceneRoot();
        if (root == null)
            return Fail("当前没有打开的场景");
        Node start = root;
        if (J.Str(a, "root") != "")
        {
            var s = root.GetNodeOrNull(new NodePath(J.Str(a, "root")));
            if (s == null)
                return Fail($"找不到节点：{J.Str(a, "root")}");
            start = s;
        }
        var depth = (int)J.Int(a, "depth", 6);
        var props = J.AsList(a.GetValueOrDefault("properties")).Select(J.Str).ToList();
        var lines = new List<string>();
        SceneWalk(root, start, 0, depth, props, lines);
        return Text(string.Join("\n", lines));
    }

    private static void SceneWalk(Node root, Node n, int d, int maxD, List<string> props, List<string> lines)
    {
        if (lines.Count > 2000)
            return;
        var path = n == root ? "." : root.GetPathTo(n).ToString();
        var line = $"{new string(' ', d * 2)}{path} ({n.GetClass()})";
        if (n.GetScript().AsGodotObject() is Script scr && scr.ResourcePath != "")
            line += " script=" + scr.ResourcePath;
        if (n.SceneFilePath != "" && n != root)
            line += " instance=" + n.SceneFilePath;
        foreach (var pn in props)
        {
            if (!HasProperty(n, pn))
                continue;
            var v = n.Get(pn);
            if (v.VariantType != Variant.Type.Nil)
                line += $" {pn}={Short(v)}";
        }
        lines.Add(line);
        if (d >= maxD)
        {
            if (n.GetChildCount() > 0)
                lines.Add($"{new string(' ', d * 2)}  …（{n.GetChildCount()} 个子节点未展开）");
            return;
        }
        foreach (var ch in n.GetChildren())
            if (ch.Owner == root)
                SceneWalk(root, ch, d + 1, maxD, props, lines);
    }

    private static bool HasProperty(GodotObject o, string name)
    {
        foreach (var p in o.GetPropertyList())
            if (p["name"].AsString() == name)
                return true;
        return false;
    }

    private static string Short(Variant v)
    {
        if (v.VariantType == Variant.Type.Object)
        {
            var o = v.AsGodotObject();
            if (o is Resource r)
                return $"{r.GetClass()}({(r.ResourcePath != "" ? r.ResourcePath : "内嵌")})";
            return o?.GetClass() ?? "null";
        }
        return UiTree.ClipText(v.ToString(), 60);
    }

    private Dict UiSnapshot(Dict a)
    {
        Node? rootNode = null;
        if (a.GetValueOrDefault("root") is { } rootArg && J.Str(rootArg) != "")
        {
            var r = Ui.Resolve(rootArg);
            if (!r.Ok)
                return Fail(r.Error);
            rootNode = r.Node;
        }
        return Text(Ui.Snapshot(a, rootNode));
    }

    private Dict UiFind(Dict a)
    {
        var hits = Ui.Find(a);
        if (hits.Count == 0)
            return Text("没有找到匹配项。可尝试 visible_only=false，或先用 ui_snapshot 浏览。");
        var lines = new List<string>();
        foreach (var h in hits)
        {
            var node = h.Node!;
            var flags = h.Visible ? "" : " {hidden}";
            lines.Add($"[{h.Ref}] {(h.Kind != "" ? h.Kind : node.GetClass())} {J.Q(UiTree.ClipText(h.Text, 60))} {UiTree.RectStr(h.Rect)}{flags}  locator={Ui.LocatorOf(node, h.Kind, h.Key)}");
        }
        return Text(string.Join("\n", lines));
    }

    private Dict UiDescribe(Dict a)
    {
        var r = Ui.Resolve(a.GetValueOrDefault("target"));
        return r.Ok ? Json(Ui.Describe(r)) : Fail(r.Error);
    }

    private (bool Ok, Window? Window, Vector2 Pos, string Error) PointOf(object? spec, Window defaultWindow)
    {
        if (spec is Dict d && d.ContainsKey("x") && d.ContainsKey("y") && !d.ContainsKey("text"))
            return (true, defaultWindow, new Vector2((float)J.Num(d["x"]), (float)J.Num(d["y"])), "");
        var r = Ui.Resolve(spec);
        if (!r.Ok)
            return (false, null, Vector2.Zero, r.Error);
        return (true, r.Window, r.Visible ? r.Vis.GetCenter() : r.Rect.GetCenter(), "");
    }

    private static bool HasArg(Dict a, string k) => a.TryGetValue(k, out var v) && v != null && J.Str(v) != "";

    private async Task<Dict> UiAct(Dict a)
    {
        var action = J.Str(a, "action");
        Window w = Ui.TreeRoot();
        if (a.GetValueOrDefault("window") is { } winArg)
        {
            var wr = Ui.Resolve(winArg);
            if (!wr.Ok)
                return Fail("window: " + wr.Error);
            w = wr.Window!;
        }
        Geo? r = null;
        var pos = Vector2.Zero;
        bool hasTarget = HasArg(a, "target");
        if (hasTarget)
        {
            r = Ui.Resolve(a["target"]);
            if (!r.Ok)
                return Fail(r.Error);
            w = r.Window!;
            if (!r.Visible)
            {
                // 只做滚动显露，然后重新取几何
                Guide.TryScrollIntoView(r);
                await Input.Frames(2);
                r = Ui.Geometry(r.Base);
                if (!r.Visible)
                    return Fail($"目标当前不可见，不能直接操作。hidden_because={J.Stringify(Ui.HiddenReasons(r))}。请先引导/执行显露它的步骤。");
            }
            pos = r.Hot?.GetCenter() ?? r.Vis.GetCenter();
        }
        else if (a.ContainsKey("x") && a.ContainsKey("y"))
        {
            pos = new Vector2((float)J.Num(a["x"]), (float)J.Num(a["y"]));
        }
        else if (action != "key" && action != "type")
        {
            return Fail("需要 target 或 x,y");
        }

        // 像人一样：要操作弹窗以外的东西，先按 Escape 关掉打开着的弹出菜单/弹出面板。
        var closedPopups = await CloseOtherPopups(w);
        var before = VisibleWindows();
        var selBefore = Rehearsal.SelectionPaths();
        // 演练中的按键（保存类快捷键）：记下前后哪些文件被写了，引导时按“文件已保存”判定，而不是死认这个快捷键
        var filesBefore = Rehearsal.Active && action == "key" ? Rehearsal.ScanFiles() : null;
        if (J.Bool(a, "focus_window", true) && action != "hover")
        {
            if (!w.HasFocus() && w == Ui.TreeRoot())
            {
                w.GrabFocus();
                await Input.Frames(1);
            }
        }
        if (J.Bool(a, "show", false) && hasTarget)
        {
            Guide.AddMarks(new List { a["target"] }, $"AI 演示：{ActionCn(action)}", 900, Guide.ColorAgent, false);
            await Input.Seconds(0.7);
        }

        Input.MoveOsCursor = J.Bool(a, "move_cursor", true);
        Input.LastMovedCursor = false;
        var mods = a.GetValueOrDefault("modifiers") as Dict;
        var note = "";
        switch (action)
        {
            case "click":
                if (r != null && r.Kind == "menu" && r.Node is PopupMenu pm)
                {
                    // 菜单项矩形是推算的，用键盘确认更可靠：聚焦该项后按 Enter。
                    var idx = Ui.SubitemIndex(pm, "menu", r.Key);
                    Input.Move(w, pos);
                    await Input.Frames(1);
                    pm.SetFocusedItem(idx);
                    await Input.Frames(1);
                    await Input.KeyPress(w, "Enter");
                    note = "菜单项通过 聚焦+Enter 触发";
                }
                else
                {
                    await Input.Click(w, pos, MouseButton.Left, false, mods);
                }
                break;
            case "double_click":
                await Input.Click(w, pos, MouseButton.Left, true, mods);
                break;
            case "right_click":
                await Input.Click(w, pos, MouseButton.Right, false, mods);
                break;
            case "middle_click":
                await Input.Click(w, pos, MouseButton.Middle, false, mods);
                break;
            case "hover":
                await Input.Hover(w, pos, true);
                break;
            case "scroll":
                await Input.Scroll(w, pos, (int)J.Int(a, "amount", 3));
                break;
            case "drag":
            {
                var to = PointOf(a.GetValueOrDefault("to"), w);
                if (!to.Ok)
                    return Fail("to: " + to.Error);
                if (to.Window != w)
                    return Fail("暂不支持跨窗口拖拽");
                await Input.Drag(w, pos, to.Pos, 12, mods);
                break;
            }
            case "focus":
                if (r?.Node is Control fc)
                {
                    fc.GrabFocus();
                    await Input.Frames(1);
                }
                break;
            case "type":
                if (hasTarget)
                    await Input.Click(w, pos);
                await Input.TypeText(FocusedWindow(w), J.Str(a, "text"));
                break;
            case "paste":
            {
                if (hasTarget)
                    await Input.Click(w, pos);
                var oldClip = DisplayServer.ClipboardGet();
                DisplayServer.ClipboardSet(J.Str(a, "text"));
                var fwp = FocusedWindow(w);
                await Input.KeyPress(fwp, "ctrl+a");
                await Input.KeyPress(fwp, "ctrl+v");
                await Input.Frames(2);
                DisplayServer.ClipboardSet(oldClip);
                break;
            }
            case "key":
            {
                var fw2 = hasTarget ? w : FocusedWindow(w);
                var err = await Input.KeyPress(fw2, J.Str(a, "keys"));
                if (err != "")
                    return Fail(err);
                break;
            }
            default:
                return Fail($"未知 action：{action}");
        }
        await Input.Frames(3);
        if (closedPopups.Count > 0)
            note = $"先关闭了弹窗：{string.Join(", ", closedPopups)}。" + note;
        var rep = ActReport(action, r, before, note);
        if (Input.LastMovedCursor)
            rep["cursor_moved"] = true;
        if (Rehearsal.Active)
        {
            if (action == "key")
            {
                await Input.Frames(5);
                rep["files_written"] = Rehearsal.FilesWrittenSince(filesBefore).Cast<object?>().ToList();
            }
            RecordAct(action, a, r, rep, selBefore);
            rep["recorded_step"] = Rehearsal.Records.Count;
        }
        return Json(rep);
    }

    /// <summary>演练中：把这次操作录成可回放的一步（稳定 locator + 退路查询 + 效果）。</summary>
    private void RecordAct(string action, Dict a, Geo? r, Dict rep, List<string> selBefore)
    {
        var entry = new Dict
        {
            ["action"] = action,
            ["selection_before"] = selBefore.Cast<object?>().ToList(),
            ["selection_after"] = SelectedNodes(),
        };
        // 没指定目标的按键（如保存快捷键）：以当时拿着焦点的控件作为这一步的高亮目标
        if (r == null && action == "key")
        {
            var fo = FocusedWindow(Ui.TreeRoot()).GuiGetFocusOwner();
            if (fo != null)
                r = Ui.Geometry(new BaseRef(fo, "", null));
        }
        if (r != null && r.Node != null)
        {
            var node = r.Node;
            var kind = r.Kind;
            entry["locator"] = Ui.LocatorOf(node, kind, r.Key);
            entry["kind"] = kind;
            entry["text"] = r.Text;
            if (kind == "" && node is Control c)
                entry["tooltip"] = UiTree.Shown(node, c.TooltipText);
            if (node is EditorProperty ep && kind == "")
            {
                entry["property"] = ep.GetEditedProperty().ToString();
                entry["property_label"] = ep.Label;
            }
            var fb = new Dict();
            if (node is TextEdit || node is LineEdit)
            {
                // 文本框的内容会变，不能当身份；用类名找
                entry["text"] = "";
            }
            else if (J.Str(entry, "text") == "" && J.Str(entry, "tooltip") == "")
            {
                // 没有文字的小按钮（如下拉箭头）：借用所在属性行的名字描述位置
                var p = node.GetParent();
                while (p != null && p is not Window)
                {
                    if (p is EditorProperty pep)
                    {
                        entry["context"] = node is BaseButton ? $"「{pep.Label}」右侧的下拉按钮" : $"「{pep.Label}」";
                        break;
                    }
                    p = p.GetParent();
                }
            }
            if (J.Str(entry, "text") != "")
            {
                fb["text"] = entry["text"];
                fb["exact"] = true;
            }
            if (kind != "")
                fb["kind"] = kind;
            else if (entry.ContainsKey("property"))
                fb = new Dict { ["property"] = entry["property"] };
            else
                fb["class"] = node.GetClass();
            if (fb.Count > 1 || fb.ContainsKey("property") || node is TextEdit || node is LineEdit)
                entry["fallback"] = fb;
        }
        else
        {
            entry["locator"] = "";
        }
        if (action is "type" or "paste")
            entry["text_input"] = J.Str(a, "text");
        if (action == "key")
        {
            entry["keys"] = J.Str(a, "keys");
            entry["files_written"] = rep.GetValueOrDefault("files_written") ?? new List();
        }
        if (rep.TryGetValue("opened_windows", out var ow))
            entry["opened"] = ow;
        Rehearsal.Record(entry);
    }

    private async Task<Dict> RehearsalStop(Dict a)
    {
        if (!Rehearsal.Active)
            return Fail("当前没有演练");
        var steps = Rehearsal.ToSteps();
        ApplyTexts(steps, a);
        var report = await Rehearsal.Stop(J.Bool(a, "rollback", true));
        var t = J.Str(a, "title", J.Str(report, "title"));
        report["steps"] = steps;
        if (J.Str(a, "save_as") != "")
        {
            var path = Rehearsal.SaveRecipe(J.Str(a, "save_as"), t, steps);
            report["saved"] = path != "" ? path : "保存失败";
        }
        if (J.Bool(a, "start_guide", false) && steps.Count > 0)
            report["guide"] = StartGuide(t, steps, J.Bool(a, "dim", true), true);
        return Json(report);
    }

    private static void ApplyTexts(List<object?> steps, Dict a)
    {
        var texts = J.AsList(a.GetValueOrDefault("texts"));
        for (int i = 0; i < Math.Min(texts.Count, steps.Count); i++)
            if (J.Str(texts[i]).Trim() != "" && steps[i] is Dict sd)
                sd["text"] = J.Str(texts[i]);
    }

    public Dict RecipePlay(Dict a)
    {
        var d = Rehearsal.LoadRecipe(J.Str(a, "name"));
        if (d.Count == 0)
            return Fail($"找不到配方：{J.Str(a, "name")}。先用 recipe_list 查看。");
        var steps = (List)J.Clone(J.AsList(d.GetValueOrDefault("steps")))!;
        ApplyTexts(steps, a);
        return Json(StartGuide(J.Str(a, "title", J.Str(d, "title")), steps, J.Bool(a, "dim", true), true));
    }

    private async Task<List<string>> CloseOtherPopups(Window targetWindow)
    {
        var closed = new List<string>();
        foreach (var p in VisibleWindows())
        {
            if (p is not Popup || p == targetWindow)
                continue;
            // 目标在这个弹窗里（或其子窗口里）时保留它
            bool inside = false;
            Node? cur = targetWindow;
            while (cur != null)
            {
                if (cur == p)
                {
                    inside = true;
                    break;
                }
                cur = cur.GetParent();
            }
            if (inside)
                continue;
            await Input.KeyPress(p, "Escape");
            await Input.Frames(2);
            if (p.Visible)
            {
                p.Hide();
                await Input.Frames(1);
            }
            closed.Add(p.GetClass());
        }
        return closed;
    }

    private Window FocusedWindow(Window fallback)
    {
        foreach (var w in VisibleWindows())
            if (w.HasFocus())
                return w;
        return fallback;
    }

    private static string ActionCn(string action) => action switch
    {
        "click" => "点击",
        "double_click" => "双击",
        "right_click" => "右键",
        "hover" => "悬停",
        "type" => "输入",
        "drag" => "拖拽",
        "scroll" => "滚动",
        "key" => "按键",
        _ => action,
    };

    private Dict ActReport(string action, Geo? r, List<Window> before, string note)
    {
        var after = VisibleWindows();
        var opened = after.Where(w => !before.Contains(w)).Select(w => (object?)WindowInfo(w)).ToList();
        var closed = before.Where(w => IsInstanceValid(w) && !after.Contains(w))
            .Select(w => (object?)new Dict { ["ref"] = Ui.RefFor(w), ["class"] = w.GetClass(), ["title"] = w.Title }).ToList();
        var rep = new Dict { ["ok"] = true, ["action"] = action };
        if (r != null)
            rep["target"] = $"{r.Ref} {J.Q(UiTree.ClipText(r.Text, 50))}";
        if (opened.Count > 0)
            rep["opened_windows"] = opened;
        if (closed.Count > 0)
            rep["closed_windows"] = closed;
        foreach (var w in after)
            if (w.Exclusive && w != Ui.TreeRoot())
                rep["modal_open"] = WindowInfo(w);
        var focus = FocusedWindow(Ui.TreeRoot()).GuiGetFocusOwner();
        if (focus != null)
            rep["focused_control"] = FocusDesc(focus);
        rep["selected_nodes"] = SelectedNodes();
        if (note != "")
            rep["note"] = note;
        return rep;
    }

    private Dict UiHighlight(Dict a)
    {
        bool clear = J.Bool(a, "clear", false);
        if (clear)
            Guide.ClearMarks();
        var targets = J.AsList(a.GetValueOrDefault("targets"));
        if (targets.Count == 0)
            return Text(clear ? "已清除高亮" : "没有 targets");
        var res = Guide.AddMarks(targets, J.Str(a, "text"), J.Int(a, "duration_ms", 6000), Guide.ColorMark, true);
        var outL = new List();
        for (int i = 0; i < res.Count; i++)
        {
            var g = res[i];
            outL.Add(g.Ok
                ? new Dict { ["n"] = i + 1, ["ref"] = g.Ref, ["text"] = g.Text, ["visible"] = g.Visible }
                : new Dict { ["n"] = i + 1, ["error"] = g.Error });
        }
        return Json(outL);
    }

    private Dict UiGuide(Dict a)
    {
        var steps = J.AsList(a.GetValueOrDefault("steps"));
        if (steps.Count == 0)
            return Fail("steps 不能为空");
        return Json(StartGuide(J.Str(a, "title"), steps, J.Bool(a, "dim", true), J.Bool(a, "auto_scroll", true)));
    }

    private async Task<Dict> GuideControl(Dict a)
    {
        var action = J.Str(a, "action");
        if (action == "do_step")
        {
            var step = Guide.CurrentStep();
            if (step == null)
                return Fail("没有进行中的步骤");
            var before = Guide.Status();
            var res = await UiAct(new Dict { ["action"] = "click", ["target"] = step.Target, ["show"] = true });
            await Input.Frames(3);
            // 菜单项是用“聚焦 + 回车”激活的，不会产生落在目标上的鼠标点击；
            // 操作成功但引导没前进时，手动推进到下一步。
            // 点击判定有 120ms 的确认延迟，条件判定也要等界面刷新：最多等 600ms 看引导是否自己前进
            bool Same(Dict after) => J.Str(after, "id") == J.Str(before, "id") && J.Int(after, "step", 0) == J.Int(before, "step", 0) && J.Str(after, "state") == "running";
            var after = Guide.Status();
            var t0 = Time.GetTicksMsec();
            while (Time.GetTicksMsec() - t0 < 600 && Same(after))
            {
                await Input.Frames(1);
                after = Guide.Status();
            }
            if (!J.Bool(res, "isError", false) && Same(after))
                Guide.ControlGuide("next");
            ((List)res["content"]!).Add(new Dict { ["type"] = "text", ["text"] = J.Stringify(Guide.Status()) });
            return res;
        }
        if (action == "restart" && J.Bool(a, "rollback", true))
        {
            if (J.Str(Guide.Status(), "state") == "none")
                return Fail("当前没有引导");
            var rep = await GuideRestart();
            var st = Guide.Status();
            st["rollback"] = rep;
            return Json(st);
        }
        var err = Guide.ControlGuide(action);
        return err != "" ? Fail(err) : Json(Guide.Status());
    }

    private async Task<Dict> Screenshot(Dict a)
    {
        Window w = Ui.TreeRoot();
        var crop = new Rect2();
        if (a.GetValueOrDefault("window") is { } winArg)
        {
            var wr = Ui.Resolve(winArg);
            if (!wr.Ok)
                return Fail(wr.Error);
            w = wr.Window!;
        }
        if (a.GetValueOrDefault("target") is { } tArg)
        {
            var tr = Ui.Resolve(tArg);
            if (!tr.Ok)
                return Fail(tr.Error);
            w = tr.Window!;
            crop = tr.Rect.Grow((float)J.Num(a.GetValueOrDefault("margin"), 80));
        }
        await ToSignal(RenderingServer.Singleton, RenderingServer.SignalName.FramePostDraw);
        var img = w.GetTexture().GetImage();
        if (img == null)
            return Fail("无法读取窗口图像");
        if (crop.Size != Vector2.Zero)
        {
            var px = w.GetFinalTransform() * crop;
            var bounds = new Rect2I(Vector2I.Zero, img.GetSize());
            var ri = new Rect2I((Vector2I)px.Position, (Vector2I)px.Size).Intersection(bounds);
            if (ri.Size.X > 0 && ri.Size.Y > 0)
                img = img.GetRegion(ri);
        }
        var maxW = (int)J.Int(a, "max_width", 1600);
        if (img.GetWidth() > maxW)
            img.Resize(maxW, (int)(img.GetHeight() * (float)maxW / img.GetWidth()), Image.Interpolation.Bilinear);
        var png = img.SavePngToBuffer();
        var info = $"window={Ui.RefFor(w)} size={img.GetWidth()}x{img.GetHeight()}" + (crop.Size != Vector2.Zero ? " crop=" + UiTree.RectStr(crop) : "");
        return new Dict
        {
            ["content"] = new List
            {
                new Dict { ["type"] = "image", ["data"] = Convert.ToBase64String(png), ["mimeType"] = "image/png" },
                new Dict { ["type"] = "text", ["text"] = info },
            },
            ["isError"] = false,
        };
    }

    // ============================================================ editor_exec（运行 GDScript 片段）

    /// <summary>收集 editor_exec 执行期间的输出与错误。</summary>
    private partial class Capture : Logger
    {
        public readonly List<string> Lines = new();

        public override void _LogMessage(string message, bool error)
        {
            lock (Lines)
                Lines.Add((error ? "[stderr] " : "") + message.Trim());
        }

        public override void _LogError(string function, string file, int line, string code, string rationale, bool editorNotify, int errorType, Godot.Collections.Array<ScriptBacktrace> scriptBacktraces)
        {
            lock (Lines)
                Lines.Add($"[error] {file}:{line} {rationale} {code}");
        }
    }

    private async Task<Dict> Exec(Dict a)
    {
        var code = J.Str(a, "code");
        if (code.Trim() == "")
            return Fail("code 为空");
        var lines = code.Split('\n');
        var indent = "\t";
        foreach (var l in lines)
        {
            if (l.StartsWith(" "))
            {
                indent = "    ";
                break;
            }
            if (l.StartsWith("\t"))
                break;
        }
        var src = "@tool\nextends RefCounted\n\nfunc run():\n" + string.Concat(lines.Select(l => indent + l + "\n"));
        var cap = new Capture();
        OS.AddLogger(cap);
        try
        {
            var script = new GDScript { SourceCode = src };
            var err = script.Reload();
            if (err != Error.Ok)
            {
                await Input.Frames(1);
                return Fail($"编译失败（{err}）：\n{string.Join("\n", cap.Lines)}");
            }
            var inst = script.New().AsGodotObject();
            var ret = inst.Call("run");
            // 片段里用了 await 时，run() 返回的是 GDScriptFunctionState，等它的 completed 信号拿真正的返回值
            if (ret.VariantType == Variant.Type.Object && ret.AsGodotObject() is { } st && st.GetClass() == "GDScriptFunctionState")
            {
                var args = await ToSignal(st, "completed");
                ret = args.Length > 0 ? args[0] : default;
            }
            await Input.Frames(1);
            var text = "result: ";
            var obj = J.FromVariant(ret);
            text += obj switch { null => "<null>", Dict or List => J.Stringify(obj, true), _ => J.Str(obj) };
            if (cap.Lines.Count > 0)
                text += "\n--- log ---\n" + string.Join("\n", cap.Lines);
            GC.KeepAlive(inst);
            return Text(text);
        }
        finally
        {
            OS.RemoveLogger(cap);
        }
    }
}
#endif
