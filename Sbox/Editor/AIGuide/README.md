# AI Guide — s&box 项目编辑器插件

本插件参考 Godot AI Guide 的功能与教程流程，使用 s&box 编辑器 API、C# 和项目内的 Qt 原生适配层实现。没有依赖 Godot 引擎，也不修改 Hammer 或引擎二进制。

使用说明见 [AI Guide 编辑器插件](../../docs/使用手册/AI%20Guide编辑器插件.md)。

- 编辑器菜单：`编辑器顶部 AI Guide → 教程与状态`。
- MCP：`http://127.0.0.1:7660/mcp`，或现有 sbox-cli 中的 `aiguide_call` / `aiguide_tools`。
- 文件桥：项目 `.ai_guide_bridge/in/*.json` → `out/*.json`。
- 教程：`Config/AIGuide/Recipes/*.json`（项目教程）；插件 `Recipes/` 为 Git 管理的内置示例。
- 本机配置：`.ai_guide/config.json`；本机状态、截图与请求不入库。
- 验证：AIGuide 仓库的 `Sbox/Tools/Test-AIGuide.ps1 -Project <项目目录>`。在专用测试编辑器中运行，测试会操作界面并演练文件/场景回退。

Library 版本由 Verso 统一更新。C# 自动热加载；更新 `Native/aiguide_qt5.dll` 后须重启编辑器。插件仅在编辑器运行，不能把它当作游戏运行时 API。

AIDebug 是可选的独立 Library；项目获取该依赖后，可转发其已激活的本地调试服务。

1.3.0 与 Godot 引导约定一致：默认点击完成、条件判断自动推进、上一步保留确认、定位失败使用 fallback。开始或切换步骤时呈现目标窗口，目标被滚动裁剪时尝试滚动；气泡提供“定位”，不会每帧抢焦点。教程和演练期间临时使用可定位控件的 Qt 文件对话框，结束后恢复之前的设置。
