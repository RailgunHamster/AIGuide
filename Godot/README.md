# AI Guide for Godot（C# 编辑器插件）

让任意外部 AI Agent 通过 MCP **看见、操作、并可视化指引** Godot 编辑器的 UI。
插件自带 MCP 服务（Streamable HTTP），无需另装 MCP 服务进程。

## 要求
- **Godot 4.8 .NET 版编辑器**、**.NET 10 SDK**。插件是 C# 写的，需要项目中有使用 `Godot.NET.Sdk`、目标框架为 `net10.0` 的 `.csproj`。
  纯 GDScript 项目可用安装脚本的 `--create-csharp` 创建最小 C# 宿主。
- 安装脚本需要 **Python 3.10+**，只使用标准库；Git 用于获取和固定仓库版本。
- 建议使用打了补丁的引擎：[`patches/ai_guide_synthetic_hover.patch`](patches/README.md)。不打补丁时按钮类控件只在系统光标位于该窗口内时才响应合成点击，插件会临时移动你的真实光标去点（你同时在用鼠标时可能失败）；打补丁后完全不碰光标。
- 所有代码都包在 `#if TOOLS` 里，只在编辑器里编译，不进入导出的游戏。

## 安装

### 获取 Godot 目录

```powershell
git clone --filter=blob:none --sparse https://github.com/RailgunHamster/AIGuide.git AIGuide
git -C AIGuide sparse-checkout set Godot
git -C AIGuide checkout <包含安装脚本的固定tag或commit>
```

稀疏检出会保留 `Godot/` 和必要的仓库根文件。安装脚本不依赖其他引擎目录、不自动切换 Git 版本。旧 `godot-v0.2.1` 没有安装脚本；使用该标签时可按下面的手动方式安装。

### 安装到项目

先关闭目标项目的编辑器。以下命令在 **AIGuide 仓库根目录**执行，项目路径可以包含空格：

```powershell
# 只检查目标和将修改的文件，不写入：
python Godot/install.py check --project "../MyGame"
python Godot/install.py install --project "../MyGame" --dry-run

# 已有 C# 项目：复制、启用插件、编译项目：
python Godot/install.py install --project "../MyGame"

# 纯 GDScript 项目：显式创建最小 C# 宿主，再安装与编译：
python Godot/install.py install --project "../MyGame" --create-csharp
```

安装器只把 `Godot/addons/ai_guide` 里的源码放入项目 `addons/ai_guide`，不会复制仓库自带的开发工程。它保留现有 `.csproj`、其他插件的启用项和其他项目设置。`--create-csharp` 仅在没有 C# 项目时创建最小 `.csproj` 并配置 C# 特性和程序集名。多个 `.csproj` 且无法从项目设置确定目标时，用 `--csproj <文件>` 选择。

安装记录在 `addons/ai_guide/.aiguide-install.json`，包括源码提交和每个已安装文件的摘要，不记录本机绝对路径。建议随插件源码一起保存到游戏项目的版本控制中。再次运行 `install` 可以更新未被本地修改的文件；碰到本地定制代码或不受安装器管理的不同版本会拒绝覆盖。构建或验证失败会恢复本次修改的插件源码、项目设置和新建 `.csproj`；编译缓存可能保留。

目录链接或其他依赖工具管理的插件需要继续用原工具管理；安装器会拒绝向链接目录写入。手动安装时直接复制 `Godot/addons/ai_guide`，构建 C# 工程，再在项目设置 → 插件中启用 **AI Guide**。

### 验证和日常使用

```powershell
# 可在 install 后加 --editor 一次完成安装、构建和验证：
python Godot/install.py install --project "../MyGame" --editor "C:/Godot/Godot.NET.exe"
# 独立验证：
python Godot/install.py verify --project "../MyGame" --editor "C:/Godot/Godot.NET.exe"
# 重新编译；只复制与启用而暂不构建时可给 install 添加 --no-build：
python Godot/install.py build --project "../MyGame"
```

`verify` 启动它自己管理的无窗口编辑器进程，在随机回环端口上检查 MCP 握手、工具列表和 `editor_state` 返回的项目路径，随后关闭该进程。它不会关闭其他编辑器。纯 GDScript 项目首次安装时，应同时传入 `--create-csharp`；`--no-build` 不能与安装后验证一起使用。

验证后正常打开项目，AI Guide 已自动启用。输出面板应显示 `MCP 服务已启动：http://127.0.0.1:7650/mcp`。

- 端口：编辑器设置 `ai_guide/server/port`，或环境变量 `GODOT_AI_GUIDE_PORT`。
- 顶部菜单栏「帮助」右边会多一个 **AI** 菜单：引导教程 / 停止当前引导 / 复制 MCP 地址。
- 不要求安装 AIDebug，也不要求使用定制引擎；引擎悬停补丁是可选改进。

## 连接 Agent
- **Claude Code**：`claude mcp add --transport http godot-guide http://127.0.0.1:7650/mcp`
- **Codex**（`~/.codex/config.toml`）：
  ```toml
  [mcp_servers.godot-guide]
  url = "http://127.0.0.1:7650/mcp"
  ```
- 其它支持 Streamable HTTP 的 MCP 客户端：同一个 URL。已用官方 MCP Python SDK 验证（握手、工具列表、调用、图片结果、错误处理、Origin 校验，14/14）。
- **开了系统代理时**：Python 写的客户端（httpx）在 Windows 上会读注册表里的系统代理，连 127.0.0.1 也走代理，结果是 502。启动客户端前设置 `NO_PROXY=127.0.0.1,localhost`。
- 连不上 localhost 的 Agent（沙盒、远程 VM）：在项目里建 `.ai_guide_bridge/in/` 目录即启用**文件桥**：把 JSON-RPC 消息写成 `in/<名字>.json`，回复出现在 `out/<名字>.json`。

## 工具一览
| 工具 | 作用 |
|---|---|
| `editor_state` | 当前场景、选中节点、检查器对象、主屏幕、打开的窗口/弹窗、焦点控件、编辑器语言 |
| `scene_tree` | 编辑中场景的节点树（可附带属性值） |
| `ui_snapshot` | 编辑器 UI 树的紧凑文本（ref、类名、文字、tooltip、属性/值、状态、坐标；含树条目/标签页/菜单项等子项） |
| `ui_find` | 按文本/类名/属性/tooltip 查找控件与子项 |
| `ui_describe` | 单个元素的持久 locator、坐标，以及“为什么看不见”和该点哪里显露它 |
| `ui_act` | 真实输入：点击、双击、右键、悬停、滚轮、拖拽、打字、粘贴、快捷键；`show=true` 先演示高亮 |
| `ui_highlight` | 在若干元素上画编号高亮框和说明气泡 |
| `ui_guide` | 多步交互引导：逐步高亮，用户点中目标或条件满足后自动进入下一步 |
| `guide_status` / `guide_control` | 查看进度和用户点击记录；下一步/上一步/重来（含回滚）/停止/让 AI 代点这一步 |
| `editor_screenshot` | 窗口截图（可裁剪到目标附近，含覆盖层） |
| `editor_exec` | 在编辑器里执行一段 GDScript 片段（`func run():` 的函数体）——插件本身是 C#，这只是给 Agent 查语义/做没有界面的操作用的 |
| `rehearsal_start` / `rehearsal_stop` | 演练：AI 先用 ui_act 把操作走一遍，插件录下每一步；结束时自动回滚现场，并把录制转成引导步骤，可存成配方 |
| `recipe_list` / `recipe_play` | 列出 / 播放已保存的配方（`res://.ai_guide/recipes/*.json`） |
| `ai_guide_reload` | 开发用：后台 `dotnet build`，编辑器检测到新程序集后自动重新加载，插件自行拆除并重建 |

## 目标的三种写法
- **ref**：`e12`（控件）、`e12.40`（树条目、标签页、菜单项等子项）。来自 snapshot/find，元素存活期间一直有效。
- **locator**：以 `/` 开头的语义路径，可以保存下来跨会话复用。例如 `/**/@SceneTreeEditor/**/@Tree::tree="Player/Body"`
- **查询**：`{"text": "保存", "class": "Button"}`；直接写一个普通字符串等同于 `{"text": ...}`。中文显示文字和英文原文都能匹配。

## 推荐流程：先演练，再指引
1. 先 `recipe_list`：做过的操作直接 `recipe_play`。用户也可以不问 AI，从顶部菜单 **AI > 引导教程** 里选一个开始。
2. `rehearsal_start` → AI 用 `ui_act` 把整套操作走一遍（代码/长文本用 `action: "paste"`）。
3. `rehearsal_stop`（`texts` 写每步说明、`save_as` 存配方、`start_guide: true` 立刻开始）。插件自动回滚：撤销演练期间的编辑器操作、删除演练新建的文件、关闭演练打开的场景/窗口/着色器标签、恢复选择与主屏幕；修改了已有文件、项目设置、编辑器设置等无法回滚的列在 `warnings` 里。
4. 用户跟着引导操作，AI 用 `guide_status` 跟进。气泡上有“上一步 / 下一步 / 从头开始 / 退出”：从头开始会把编辑器回滚到引导开始时的样子（和演练用同一套回滚），点两次才执行。

完成条件：点击（默认）、`{"selected": "节点路径"}`、`{"visible": 目标}`、`{"exists": 目标}`、`{"inspecting": ...}`、`{"main_screen": "3D"}`、
`{"text_contains": {"target": ..., "text": ...}}`、`{"key": "ctrl+alt+s"}`、`{"file_saved": {"ext": "gdshader", "contains": "..."}}`、`{"any": [条件…]}`。
演练里录到的保存快捷键会自动生成 `any[按键, 文件已保存]`：用户用别的方式保存，或快捷键被全局热键占用也能过。
进入某一步时条件已经成立的，不会自动跳过，而是提示用户点一下继续。步骤带 `clipboard` 时，进入该步会自动把内容放进剪贴板。

## 开发
- 本目录本身是一个最小 Godot C# 工程（`project.godot` + `AIGuide.Godot.csproj`），用来编译和调试插件：`dotnet build AIGuide.Godot.csproj`。
- 在游戏里调试：把游戏的 `addons/ai_guide` 指向本仓库的工作副本（链接或同步），改完 `.cs` 后调用 `ai_guide_reload`（或在编辑器里点“构建”）。
- C# 程序集重新加载时，Godot 会保留插件对象、替换代码：插件实现了 `ISerializationListener`，在 `OnBeforeSerialize` 里拆掉全部运行时对象（MCP 服务、覆盖层、菜单），`OnAfterDeserialize` 再按新代码重建。
- 正在执行的工具请求会在重新加载时被丢弃，HTTP 服务端与文件桥都有 30 秒兜底，超时会返回错误。

需求与优先级见 [ROADMAP.md](ROADMAP.md)。

## 安装器回归

从仓库根目录运行：

```powershell
python Godot/tests/test_install.py
# 额外验证真实 C# 编译、插件加载、MCP 握手和项目路径：
$env:AIGUIDE_TEST_EDITOR = "C:/Godot/Godot.NET.exe"
python Godot/tests/test_install.py
```

测试使用独立临时项目，覆盖重复安装、其他插件设置、GDScript 宿主、空格路径、Windows 换行及 BOM、本地改动保护、更新清理、失败回滚和真实编辑器启动。实际项目的插件和源码不参与测试修改。
