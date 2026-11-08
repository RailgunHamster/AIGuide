# AI Guide — s&box 项目编辑器插件

本插件参考 Godot AI Guide 的功能与教程流程，使用 s&box 编辑器 API、C# 和项目内的 Qt 原生适配层实现。没有依赖 Godot 引擎，也不修改 Hammer 或引擎二进制。

使用说明见 [AI Guide 编辑器插件](../../docs/使用手册/AI%20Guide编辑器插件.md)。

- 编辑器菜单：`编辑器顶部 AI Guide → 教程与状态`。
- MCP：`http://127.0.0.1:7660/mcp`，或现有 sbox-cli 中的 `aiguide_call` / `aiguide_tools`。
- 文件桥：项目 `.ai_guide_bridge/in/*.json` → `out/*.json`。
- 教程：`Config/AIGuide/Recipes/*.json`（项目教程）；插件 `Recipes/` 为 Git 管理的内置示例。
- 本机配置：`.ai_guide/config.json`；本机状态、截图与请求不入库。
- 验证：AIGuide 仓库的 `Sbox/Tools/Test-AIGuide.ps1 -Project <项目目录>`。在专用测试编辑器中运行，测试会操作界面并演练文件/场景回退。

整个 `Editor/AIGuide` 目录必须一起更新。C# 自动热加载；替换 `Native/aiguide_qt5.dll` 后须重启编辑器。插件仅在编辑器运行，不能把它当作游戏运行时 API。

`ai_debug` 暂时只作为功能参考，不在本插件中公开调试工具。
