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

/// <summary>
/// 演练：AI 先用 ui_act 把操作完整走一遍，插件录下每一步的稳定定位与效果；
/// 结束时自动回滚现场，并把录制转换成引导步骤，可存成“配方”反复使用。
///
/// 回滚的范围（尽力而为，做不到的会在报告里列出）：
/// - 编辑器撤销历史：把演练开始时已有的每段历史退回到当时的版本号（只撤演练期间的操作，不碰之前的修改）
/// - 演练中新建的文件：删除，以及新建后变空的目录
/// - 演练中打开的场景：关闭；当前场景、选中的节点、主屏幕：恢复
/// - 演练中打开的窗口/弹窗：关闭；着色器编辑器里新开的标签：关闭
/// 做不到的：修改了已有文件的磁盘内容、项目设置、编辑器设置 —— 只报告，不回滚。
///
/// 同一个类也用作“引导开始时的现场快照”（Tools.Checkpoint），供“从头开始”回滚。
/// </summary>
public sealed class Rehearsal
{
    public const string RecipeDir = "res://.ai_guide/recipes";

    private readonly UiTree _ui;
    private readonly UiInput _input;
    private readonly Tools _tools;

    public bool Active;
    public string Title = "";
    public List<Dict> Records = new();

    private sealed class StartState
    {
        public Dictionary<string, long> Files = new();
        public Dictionary<long, ulong> Histories = new();
        public List<string> OpenScenes = new();
        public string CurrentScene = "";
        public List<string> Selection = new();
        public string MainScreen = "";
        public HashSet<ulong> Windows = new();
        public int ShaderTabs;
        public int OpenScripts;
    }

    private StartState _start = new();
    private bool _projectSettingsChanged;
    private bool _editorSettingsChanged;

    public Rehearsal(UiTree ui, UiInput input, Tools tools)
    {
        _ui = ui;
        _input = input;
        _tools = tools;
    }

    // ============================================================ 开始

    public Dict Start(string title)
    {
        if (Active)
            return new Dict { ["error"] = "已经在演练中，先调用 rehearsal_stop" };
        Title = title;
        Records = new List<Dict>();
        _projectSettingsChanged = false;
        _editorSettingsChanged = false;
        var ei = EditorInterface.Singleton;
        var root = ei.GetEditedSceneRoot();
        _start = new StartState
        {
            Files = ScanFiles(),
            Histories = HistoryVersions(),
            OpenScenes = ei.GetOpenScenes().ToList(),
            CurrentScene = root != null ? root.SceneFilePath : "",
            Selection = SelectionPaths(),
            MainScreen = _tools.MainScreenName,
            Windows = _tools.VisibleWindows().Select(w => w.GetInstanceId()).ToHashSet(),
            ShaderTabs = ShaderTabCount(),
            OpenScripts = ei.GetScriptEditor().GetOpenScripts().Count,
        };
        Subscribe(true);
        Active = true;
        return new Dict
        {
            ["ok"] = true,
            ["title"] = Title,
            ["files_tracked"] = _start.Files.Count,
            ["histories"] = _start.Histories.Count,
            ["note"] = "现在用 ui_act 把操作完整走一遍（代码类文本用 action=paste）。只有 ui_act 会被录制；editor_exec 的改动不会变成引导步骤，也不保证能回滚。",
        };
    }

    // Godot 信号对应的 C# 事件：断开一个没有连接的回调会报错，所以自己记住是否已连接
    private bool _subscribed;

    private void Subscribe(bool on)
    {
        if (on == _subscribed)
            return;
        var es = EditorInterface.Singleton.GetEditorSettings();
        if (on)
        {
            ProjectSettings.SettingsChanged += OnProjectSettingsChanged;
            es.SettingsChanged += OnEditorSettingsChanged;
        }
        else
        {
            ProjectSettings.SettingsChanged -= OnProjectSettingsChanged;
            es.SettingsChanged -= OnEditorSettingsChanged;
        }
        _subscribed = on;
    }

    private void OnProjectSettingsChanged() => _projectSettingsChanged = true;

    private void OnEditorSettingsChanged() => _editorSettingsChanged = true;

    public static List<string> SelectionPaths()
    {
        var root = EditorInterface.Singleton.GetEditedSceneRoot();
        var outL = new List<string>();
        foreach (var n in EditorInterface.Singleton.GetSelection().GetSelectedNodes())
            outL.Add(root != null ? root.GetPathTo(n).ToString() : n.Name.ToString());
        return outL;
    }

    private static Dictionary<long, ulong> HistoryVersions()
    {
        var ur = EditorInterface.Singleton.GetEditorUndoRedo();
        var ids = new HashSet<long> { 0 }; // 0 = EditorUndoRedoManager.GLOBAL_HISTORY
        foreach (var r in EditorInterface.Singleton.GetOpenSceneRoots())
            ids.Add(ur.GetObjectHistoryId(r));
        var outD = new Dictionary<long, ulong>();
        foreach (var id in ids)
        {
            var h = ur.GetHistoryUndoRedo((int)id);
            if (h != null)
                outD[id] = h.GetVersion();
        }
        return outD;
    }

    public static Dictionary<string, long> ScanFiles()
    {
        var outD = new Dictionary<string, long>();
        ScanDir("res://", outD);
        return outD;
    }

    private static void ScanDir(string dir, Dictionary<string, long> outD)
    {
        foreach (var d in DirAccess.GetDirectoriesAt(dir))
        {
            // 以 . 开头的目录（.godot 缓存、版本管理工具的数据目录、本插件的 .ai_guide 等）Godot 本身也不导入，不追踪
            if (d.StartsWith("."))
                continue;
            var sub = dir.PathJoin(d);
            outD[sub + "/"] = 0;
            ScanDir(sub, outD);
        }
        foreach (var f in DirAccess.GetFilesAt(dir))
        {
            var p = dir.PathJoin(f);
            outD[p] = (long)FileAccess.GetModifiedTime(p);
        }
    }

    // ============================================================ 录制

    /// <summary>由 ui_act 在每次操作后调用。</summary>
    public void Record(Dict entry)
    {
        if (!Active)
            return;
        entry["n"] = Records.Count + 1;
        Records.Add(entry);
    }

    private static string LabelOf(Dict rec)
    {
        foreach (var k in new[] { "text", "tooltip", "property_label" })
        {
            var v = J.Str(rec, k).Trim();
            if (v != "")
                return UiTree.ClipText(v.Split('\n')[0], 40);
        }
        return "";
    }

    public static string DefaultText(Dict rec)
    {
        var label = LabelOf(rec);
        var action = J.Str(rec, "action", "click");
        var kind = J.Str(rec, "kind");
        switch (action)
        {
            case "paste":
                return "全选（Ctrl+A）后粘贴（Ctrl+V）。要粘贴的内容已经放进剪贴板：\n" + UiTree.ClipText(J.Str(rec, "text_input"), 400).Replace("⏎", "\n");
            case "type":
                return "输入：" + J.Str(rec, "text_input");
            case "key":
                return "按 " + J.Str(rec, "keys");
            case "double_click":
                return label != "" ? $"双击「{label}」" : "双击高亮处";
            case "right_click":
                return label != "" ? $"右键点击「{label}」" : "右键点击高亮处";
        }
        switch (kind)
        {
            case "tree": return $"选中「{label}」";
            case "menu": return $"在菜单里选择「{label}」";
            case "bar": return $"打开顶部菜单「{label}」";
            case "tab": return $"切换到「{label}」";
            case "list": return $"选择「{label}」";
        }
        if (rec.ContainsKey("property"))
            return $"点击检查器里的「{(label != "" ? label : J.Str(rec, "property"))}」";
        if (label == "" && rec.ContainsKey("context"))
            return "点击" + J.Str(rec, "context");
        return label != "" ? $"点击「{label}」" : "点击高亮的位置";
    }

    /// <summary>把录制转换成 ui_guide 的步骤。悬停、滚动、聚焦、拖拽不算步骤（引导会自动滚动）。</summary>
    public List<object?> ToSteps()
    {
        var steps = new List<object?>();
        var lastText = ""; // 最近一次粘贴/输入里最有辨识度的一行，用来确认保存下来的确实是这次的内容
        foreach (var rec in Records)
        {
            var action = J.Str(rec, "action");
            if (action is "paste" or "type")
                lastText = Snippet(J.Str(rec, "text_input"));
            if (action is "hover" or "scroll" or "focus" or "drag" || J.Str(rec, "locator") == "")
                continue;
            var step = new Dict { ["target"] = rec["locator"], ["text"] = DefaultText(rec), ["until"] = "click" };
            if (rec.TryGetValue("fallback", out var fb))
                step["fallback"] = fb;
            switch (action)
            {
                case "paste":
                case "type":
                    step["until"] = new Dict { ["text_contains"] = new Dict { ["target"] = rec["locator"], ["text"] = Snippet(J.Str(rec, "text_input")) } };
                    if (action == "paste")
                        step["clipboard"] = J.Str(rec, "text_input");
                    break;
                case "key":
                {
                    // 快捷键可能和用户的全局热键冲突，也可能有等价的操作（Ctrl+S 保存全部、菜单里点“保存”）。
                    // 这一按如果写了文件，就以“文件已保存”为准，按键只作为其中一种完成方式。
                    var conds = new List { new Dict { ["key"] = J.Str(rec, "keys") } };
                    foreach (var f in J.AsList(rec.GetValueOrDefault("files_written")))
                    {
                        var fs = new Dict { ["ext"] = J.Str(f).GetExtension() };
                        if (lastText != "")
                            fs["contains"] = lastText;
                        conds.Add(new Dict { ["file_saved"] = fs });
                        break;
                    }
                    step["until"] = conds.Count == 1 ? conds[0] : new Dict { ["any"] = conds };
                    break;
                }
                default:
                {
                    var selBefore = J.AsList(rec.GetValueOrDefault("selection_before"));
                    var selAfter = J.AsList(rec.GetValueOrDefault("selection_after"));
                    if (J.Str(rec, "kind") == "tree" && !J.DeepEquals(selAfter, selBefore) && selAfter.Count > 0)
                        step["until"] = new Dict { ["selected"] = selAfter[0] };
                    break;
                }
            }
            steps.Add(step);
        }
        return steps;
    }

    /// <summary>与快照相比新建或修改过的文件（不含目录）。</summary>
    public static List<string> FilesWrittenSince(Dictionary<string, long>? before)
    {
        var outL = new List<string>();
        if (before == null || before.Count == 0)
            return outL;
        foreach (var (path, mtime) in ScanFiles())
        {
            if (path.EndsWith("/"))
                continue;
            if (!before.TryGetValue(path, out var old) || old != mtime)
                outL.Add(path);
        }
        return outL;
    }

    /// <summary>文本步骤的完成判定用最长的一行（足够独特，又不要求逐字一致）。</summary>
    public static string Snippet(string text)
    {
        var best = "";
        foreach (var line in text.Split('\n'))
        {
            var l = line.Trim();
            if (l.Length > best.Length)
                best = l;
        }
        return UiTree.Left(best, 60);
    }

    // ============================================================ 结束与回滚

    /// <summary>放弃快照、不回滚。</summary>
    public void Cancel()
    {
        Active = false;
        Subscribe(false);
    }

    public async Task<Dict> Stop(bool rollback)
    {
        if (!Active)
            return new Dict { ["error"] = "当前没有演练" };
        Cancel();
        var report = new Dict { ["title"] = Title, ["recorded"] = Records.Count, ["rolled_back"] = new List(), ["warnings"] = new List() };
        if (rollback)
            await Rollback(report);
        return report;
    }

    private async Task Rollback(Dict report)
    {
        var done = (List)report["rolled_back"]!;
        var warn = (List)report["warnings"]!;
        var ei = EditorInterface.Singleton;

        // 1. 关掉演练中打开的窗口/弹窗
        foreach (var win in _tools.VisibleWindows())
        {
            if (!_start.Windows.Contains(win.GetInstanceId()))
            {
                win.Hide();
                done.Add($"关闭窗口 {win.GetClass()}");
            }
        }
        await _input.Frames(2);

        // 2. 撤销历史退回到开始时的版本
        var ur = ei.GetEditorUndoRedo();
        foreach (var (id, v0) in _start.Histories)
        {
            var h = ur.GetHistoryUndoRedo((int)id);
            if (h == null)
                continue;
            int n = 0;
            while (h.HasUndo() && h.GetVersion() > v0 && n < 500)
            {
                h.Undo();
                n++;
            }
            if (n > 0)
                done.Add($"撤销 {n} 步（历史 {id}）");
            if (h.GetVersion() < v0)
                warn.Add($"历史 {id} 在演练中被撤销到了开始之前，无法自动重做回去");
        }
        await _input.Frames(2);

        // 3. 关闭演练中新打开的场景，恢复当前场景
        foreach (var p in ei.GetOpenScenes())
        {
            if (!_start.OpenScenes.Contains(p) && p != "")
            {
                ei.OpenSceneFromPath(p);
                await _input.Frames(2);
                ei.CloseScene();
                await _input.Frames(2);
                done.Add("关闭场景 " + p);
            }
        }
        var cur = _start.CurrentScene;
        var root = ei.GetEditedSceneRoot();
        if (cur != "" && (root == null || root.SceneFilePath != cur))
        {
            ei.OpenSceneFromPath(cur);
            await _input.Frames(2);
        }

        // 4. 关掉着色器编辑器里新开的标签（要在删文件之前，免得编辑器弹“文件已丢失”）
        var closedTabs = await CloseNewShaderTabs(_start.ShaderTabs);
        if (closedTabs > 0)
            done.Add($"关闭着色器标签 {closedTabs} 个");
        if (ei.GetScriptEditor().GetOpenScripts().Count > _start.OpenScripts)
            warn.Add("演练中在脚本编辑器里打开了新脚本，需要手动关闭");

        // 5. 删除演练中新建的文件与目录；报告被修改的已有文件
        var before = _start.Files;
        var now = ScanFiles();
        var newDirs = new List<string>();
        foreach (var (path, mtime) in now)
        {
            if (before.TryGetValue(path, out var old))
            {
                if (!path.EndsWith("/") && mtime != old)
                    warn.Add("已有文件在演练中被修改，无法自动回滚：" + path);
                continue;
            }
            if (path.EndsWith("/"))
            {
                newDirs.Add(path);
            }
            else
            {
                var err = DirAccess.RemoveAbsolute(ProjectSettings.GlobalizePath(path));
                done.Add($"删除新文件 {path}" + (err == Error.Ok ? "" : $"（失败：{err}）"));
            }
        }
        newDirs.Sort(StringComparer.Ordinal);
        newDirs.Reverse();
        bool dirsRemoved = false;
        foreach (var d in newDirs)
        {
            if (DirAccess.RemoveAbsolute(ProjectSettings.GlobalizePath(d.TrimEnd('/'))) == Error.Ok)
            {
                done.Add("删除新目录 " + d);
                dirsRemoved = true;
            }
        }
        // 只通知编辑器被删的文件，不做整盘扫描：整盘扫描会顺带重新加载所有被外部修改过的脚本。
        var efs = ei.GetResourceFilesystem();
        foreach (var path in now.Keys)
            if (!before.ContainsKey(path) && !path.EndsWith("/"))
                efs.UpdateFile(path);
        if (dirsRemoved)
            efs.Scan();

        // 6. 恢复选中节点与主屏幕
        var sel = ei.GetSelection();
        sel.Clear();
        root = ei.GetEditedSceneRoot();
        if (root != null)
        {
            foreach (var p in _start.Selection)
            {
                var n = root.GetNodeOrNull(new NodePath(p));
                if (n != null)
                    sel.AddNode(n);
            }
        }
        if (_start.MainScreen != "")
            ei.SetMainScreenEditor(_start.MainScreen);
        if (_projectSettingsChanged)
            warn.Add("项目设置在演练中被修改，没有回滚");
        if (_editorSettingsChanged)
            warn.Add("编辑器设置在演练中被修改，没有回滚");
        await _input.Frames(2);
    }

    private Node? ShaderEditor() => _ui.TreeRoot().FindChild("Shader Editor", true, false);

    private TabContainer? ShaderTabs()
    {
        var se = ShaderEditor();
        if (se == null)
            return null;
        foreach (var n in se.FindChildren("*", "TabContainer", true, false))
            return n as TabContainer;
        return null;
    }

    private int ShaderTabCount() => ShaderTabs()?.GetTabCount() ?? 0;

    /// <summary>用着色器编辑器自己的“文件 → 关闭”关掉新开的标签（新标签总在末尾）。</summary>
    private async Task<int> CloseNewShaderTabs(int startCount)
    {
        int closed = 0;
        var se = ShaderEditor();
        if (se == null)
            return 0;
        MenuButton? fileMenu = null;
        foreach (var mb in se.FindChildren("*", "MenuButton", true, false))
        {
            if (mb is MenuButton m && m.Text == "File")
            {
                fileMenu = m;
                break;
            }
        }
        if (fileMenu == null)
            return 0;
        var pm = fileMenu.GetPopup();
        int closeId = -1;
        for (int i = 0; i < pm.ItemCount; i++)
            if (pm.GetItemText(i) == "Close")
                closeId = pm.GetItemId(i);
        if (closeId < 0)
            return 0;
        for (int guard = 0; guard < 20; guard++)
        {
            var tc = ShaderTabs();
            if (tc == null || tc.GetTabCount() <= startCount)
                break;
            tc.CurrentTab = tc.GetTabCount() - 1;
            await _input.Frames(1);
            pm.EmitSignal(PopupMenu.SignalName.IdPressed, closeId);
            await _input.Frames(2);
            closed++;
        }
        return closed;
    }

    // ============================================================ 配方

    private static string RecipePath(string name)
    {
        var safe = name.Trim().Replace("/", "_").Replace("\\", "_").Replace(":", "_").Replace("..", "_");
        return RecipeDir.PathJoin(safe + ".json");
    }

    public static string SaveRecipe(string name, string recipeTitle, List<object?> steps)
    {
        DirAccess.MakeDirRecursiveAbsolute(ProjectSettings.GlobalizePath(RecipeDir));
        var path = RecipePath(name);
        using var f = FileAccess.Open(path, FileAccess.ModeFlags.Write);
        if (f == null)
            return "";
        f.StoreString(J.Stringify(new Dict
        {
            ["name"] = name,
            ["title"] = recipeTitle,
            ["steps"] = steps,
            ["godot"] = Engine.GetVersionInfo()["string"].AsString(),
            ["created"] = Time.GetDatetimeStringFromSystem(),
        }, true));
        return path;
    }

    public static Dict LoadRecipe(string name)
    {
        var path = RecipePath(name);
        if (!FileAccess.FileExists(path))
            return new Dict();
        return J.Parse(FileAccess.GetFileAsString(path)) as Dict ?? new Dict();
    }

    public static List<object?> ListRecipes()
    {
        var outL = new List<object?>();
        if (!DirAccess.DirExistsAbsolute(ProjectSettings.GlobalizePath(RecipeDir)))
            return outL;
        foreach (var f in DirAccess.GetFilesAt(RecipeDir))
        {
            if (!f.EndsWith(".json"))
                continue;
            var name = f[..^5];
            var d = LoadRecipe(name);
            outL.Add(new Dict
            {
                ["name"] = name,
                ["title"] = J.Str(d, "title"),
                ["steps"] = J.AsList(d.GetValueOrDefault("steps")).Count,
                ["created"] = J.Str(d, "created"),
            });
        }
        return outL;
    }
}
#endif
