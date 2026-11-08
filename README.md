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

## 在游戏项目里使用（以 Godot 为例）

插件就是 `Godot/addons/ai_guide` 这一个目录，放进游戏项目的 `addons/ai_guide` 即可：直接拷贝，或者用你习惯的依赖管理方式引用本仓库的这个子目录（建议固定到发布的版本标签，如 `godot-v0.2.1`）。

详见 [Godot/README.md](Godot/README.md)。

s&box 使用 `Sbox/Install.ps1 -Project <项目目录>` 安装。源码、测试和通用手册在本仓库的 `Sbox/` 中维护；游戏项目内是部署副本。详见 [Sbox/README.md](Sbox/README.md)。

## 仓库约定
- 不放游戏文件、令牌和编辑器缓存（`.godot/` 等）。
- 引擎改动以补丁形式放在各引擎目录的 `patches/` 下，插件在未打补丁的官方引擎上也能工作（有降级行为，见各自 README）。
