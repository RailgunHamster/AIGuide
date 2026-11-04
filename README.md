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

## 在游戏项目里使用（以 Godot 为例）

用 Verso 的「项目依赖」把插件装进游戏：
- Git 来源：`https://github.com/RailgunHamster/AIGuide.git`
- 源目录：`Godot/addons/ai_guide`，项目内目录：`addons/ai_guide`
- 更新目标：发布的版本标签（如 `godot-v0.2.0`）

详见 [Godot/README.md](Godot/README.md)。

## 仓库约定
- 不放游戏文件、令牌、`.godot/`、`.verso/`。
- 引擎改动以补丁形式放在各引擎目录的 `patches/` 下，插件在未打补丁的官方引擎上也能工作（有降级行为，见各自 README）。
