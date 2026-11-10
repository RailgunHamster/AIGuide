# AIGuide

让任意外部 AI Agent（Claude Code、Codex 等，通过 MCP）**看见、操作并可视化指引**编辑器里人能操作的一切：
问“在哪里改这个角色的材质”，AI 直接在编辑器界面上高亮要点的位置，一步一步带你点完。

做法是“先演练，再指引”：AI 先在编辑器里把操作完整走一遍，插件录下每一步并自动把现场还原，
再把录制变成引导教程交给你，也可以存下来以后直接从菜单播放。

## 引擎

每个引擎一个子目录，彼此独立：

| 目录 | 引擎 | 状态 |
|---|---|---|
| [`Godot/`](Godot/README.md) | Godot 4.8（.NET 版编辑器，C# 插件） | 可用 |
| [`Sbox/`](Sbox/README.md) | s&box Windows x64（项目编辑器插件，含 Hammer 原生 Qt 界面） | 可用 |

## 使用方式

Godot 使用已集成 AIGuide 的 Godot 4.8 .NET 编辑器：打开项目后自动加载，引导菜单和 MCP 服务直接可用。GDScript、C# 项目都无需安装项目插件，GDScript 项目无需创建 C# 宿主，也无需运行 Python 安装器。内置程序集随编辑器提供；普通使用者无需为 AIGuide 编译项目。

详见 [Godot/README.md](Godot/README.md)。

s&box 使用 `Sbox/.sbproj` 标准 Library，由 Verso 将本仓库的 `Sbox/` 映射到项目 `Libraries/AIGuide/`。成员更新项目后打开编辑器即可使用，无需安装脚本。详见 [Sbox/README.md](Sbox/README.md)。

## 仓库约定
- 不放游戏文件、令牌和编辑器缓存（`.godot/` 等）。
- 引擎改动以补丁形式放在各引擎目录的 `patches/` 下，插件在未打补丁的官方引擎上也能工作（有降级行为，见各自 README）。
- Godot 的免安装入口依赖集成编辑器中的原生引导和 `Godot.AIGuide.dll`；官方发行版不会因检出本仓库而自动获得该入口。引擎集成和插件开发由维护者完成。
