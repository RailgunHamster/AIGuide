#if TOOLS
#nullable enable
using System;
using System.Collections.Generic;
using Godot;
using Dict = System.Collections.Generic.Dictionary<string, object?>;

namespace AIGuide;

/// <summary>
/// AI Guide 插件入口：启动内置 MCP（Streamable HTTP）服务与文件桥，挂载工具与覆盖层，
/// 并在编辑器顶部菜单栏「帮助」右边加一个「AI」菜单（不用问 AI，直接播放已存好的引导教程）。
///
/// C# 插件在程序集重新加载（重新构建后）时，Godot 会保留插件对象、替换代码；
/// 此时 OnBeforeSerialize 先拆掉全部运行时对象，OnAfterDeserialize 再按新代码重建。
/// </summary>
[Tool]
public partial class AIGuidePlugin : EditorPlugin, ISerializationListener
{
    private const string SettingPort = "ai_guide/server/port";
    private const int DefaultPort = 7650;
    private const int IdStop = 100000;
    private const int IdCopyUrl = 100001;
    private const string FallbackMenuName = "AI 引导教程";

    private Tools? _tools;
    private McpServer? _server;
    private FileBridge? _bridge;
    private PopupMenu? _aiMenu;     // 顶层「AI」菜单
    private PopupMenu? _menu;       // 「AI > 引导教程」子菜单
    private bool _menuInToolSubmenu;
    private readonly List<string> _menuNames = new();
    private int _port = DefaultPort;

    public override void _EnterTree() => Setup();

    public override void _ExitTree() => Teardown();

    public void OnBeforeSerialize() => Teardown();

    public void OnAfterDeserialize() => Setup();

    private void Setup()
    {
        if (_tools != null)
            return;
        var settings = EditorInterface.Singleton.GetEditorSettings();
        if (!settings.HasSetting(SettingPort))
            settings.SetSetting(SettingPort, DefaultPort);
        settings.SetInitialValue(SettingPort, DefaultPort, false);
        var port = settings.GetSetting(SettingPort).AsInt32();
        var envPort = OS.GetEnvironment("GODOT_AI_GUIDE_PORT");
        if (int.TryParse(envPort, out var ep))
            port = ep;
        _port = port;

        _tools = new Tools { Name = "AIGuideTools" };
        AddChild(_tools);
        MainScreenChanged += OnMainScreenChanged;

        _server = new McpServer { Name = "AIGuideServer", Tools = _tools };
        AddChild(_server);
        _bridge = new FileBridge { Name = "AIGuideFileBridge", Tools = _tools };
        AddChild(_bridge);

        AddAiMenu();

        var err = _server.Start(port);
        if (err == Error.Ok)
            GD.PrintRich($"[color=green][AI Guide][/color] MCP 服务已启动：http://127.0.0.1:{port}/mcp");
        else
            GD.PushError($"[AI Guide] 无法监听端口 {port}（{err}）。可在 编辑器设置 > {SettingPort} 修改端口。");
    }

    private void Teardown()
    {
        if (_tools == null)
            return;
        RemoveAiMenu();
        MainScreenChanged -= OnMainScreenChanged;
        _server?.Stop();
        _tools.Shutdown();
        foreach (Node n in new Node?[] { _bridge, _server, _tools })
        {
            if (n != null && IsInstanceValid(n))
            {
                RemoveChild(n);
                n.QueueFree();
            }
        }
        _tools = null;
        _server = null;
        _bridge = null;
    }

    private void OnMainScreenChanged(string screenName) => _tools?.OnMainScreenChanged(screenName);

    // ============================================================ 「AI」菜单

    /// <summary>
    /// 编辑器主菜单栏（场景/项目/调试/编辑器/帮助）是一个 MenuBar，它的每个子 PopupMenu 就是一个顶层菜单。
    /// 往里加一个名为“AI”的 PopupMenu，就会出现在「帮助」右边。纯插件实现，不改引擎。
    /// </summary>
    private static MenuBar? FindMainMenubar()
    {
        foreach (var mb in EditorInterface.Singleton.GetBaseControl().FindChildren("*", "MenuBar", true, false))
            foreach (var ch in mb.GetChildren())
                if (ch is PopupMenu && ch.Name.ToString() == "Help")
                    return mb as MenuBar;
        return null;
    }

    private void AddAiMenu()
    {
        _menu = new PopupMenu { Name = "Tutorials" };
        _menu.AboutToPopup += FillMenu;
        _menu.IdPressed += OnMenuId;
        var mb = FindMainMenubar();
        if (mb != null)
        {
            _aiMenu = new PopupMenu { Name = "AI" };
            _aiMenu.AddChild(_menu);
            _aiMenu.AboutToPopup += FillAiMenu;
            _aiMenu.IdPressed += OnMenuId;
            mb.AddChild(_aiMenu);
            mb.SetMenuTitle(mb.GetMenuCount() - 1, "AI");
        }
        else
        {
            // 找不到主菜单栏（布局变了）时退回到「项目 > 工具」
            AddToolSubmenuItem(FallbackMenuName, _menu);
            _menuInToolSubmenu = true;
        }
    }

    private void RemoveAiMenu()
    {
        if (_aiMenu != null && IsInstanceValid(_aiMenu))
        {
            _aiMenu.GetParent()?.RemoveChild(_aiMenu);
            _aiMenu.QueueFree();
        }
        else if (_menuInToolSubmenu)
        {
            RemoveToolMenuItem(FallbackMenuName);
        }
        _aiMenu = null;
        _menu = null;
        _menuInToolSubmenu = false;
    }

    private void FillAiMenu()
    {
        if (_aiMenu == null || _menu == null)
            return;
        _aiMenu.Clear();
        _aiMenu.AddSubmenuNodeItem("引导教程", _menu);
        var st = _tools?.Guide.Status() ?? new Dict();
        bool running = J.Str(st, "state") == "running";
        _aiMenu.AddItem("停止当前引导" + (running ? "：" + J.Str(st, "title") : ""), IdStop);
        _aiMenu.SetItemDisabled(_aiMenu.ItemCount - 1, !running);
        _aiMenu.AddSeparator();
        _aiMenu.AddItem("复制 MCP 地址（给 Codex / Claude 等 Agent 连接用）", IdCopyUrl);
    }

    /// <summary>每次打开菜单时重新读取配方目录，新存的教程立刻出现。</summary>
    private void FillMenu()
    {
        if (_menu == null)
            return;
        _menu.Clear();
        _menuNames.Clear();
        var list = Rehearsal.ListRecipes();
        foreach (var r in list)
        {
            var title = J.Str(r, "title");
            _menu.AddItem($"{(title != "" ? title : J.Str(r, "name"))}（{J.Int(r, "steps", 0)} 步）", _menuNames.Count);
            _menuNames.Add(J.Str(r, "name"));
        }
        if (list.Count == 0)
        {
            _menu.AddItem("还没有教程：让 AI 演练一遍并保存（save_as）后会出现在这里", -1);
            _menu.SetItemDisabled(_menu.ItemCount - 1, true);
        }
        if (_menuInToolSubmenu)
        {
            // 退回到「项目 > 工具」时，停止与复制地址也放在这个菜单里
            var st = _tools?.Guide.Status() ?? new Dict();
            if (J.Str(st, "state") == "running")
            {
                _menu.AddSeparator();
                _menu.AddItem("停止当前引导：" + J.Str(st, "title"), IdStop);
            }
        }
    }

    private void OnMenuId(long id)
    {
        if (_tools == null)
            return;
        if (id == IdStop)
        {
            _tools.Guide.StopGuide();
            return;
        }
        if (id == IdCopyUrl)
        {
            var url = $"http://127.0.0.1:{_port}/mcp";
            DisplayServer.ClipboardSet(url);
            GD.PrintRich($"[color=green][AI Guide][/color] 已复制 MCP 地址：{url}");
            return;
        }
        if (id >= 0 && id < _menuNames.Count)
            _tools.RecipePlay(new Dict { ["name"] = _menuNames[(int)id] });
    }
}
#endif
