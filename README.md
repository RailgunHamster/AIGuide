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

只检出 `Godot/` 就包含安装所需的脚本、插件源码和开发工程模板：

```powershell
git clone --filter=blob:none --sparse https://github.com/RailgunHamster/AIGuide.git AIGuide
git -C AIGuide sparse-checkout set Godot
git -C AIGuide checkout <包含安装脚本的固定tag或commit>

# 在 AIGuide 仓库根目录执行；目标为已有 Godot C# 项目：
python Godot/install.py check --project "../MyGame"
python Godot/install.py install --project "../MyGame"
```

安装脚本复制 `Godot/addons/ai_guide`、保留其他插件配置、启用 AI Guide 并执行 `dotnet build`。已有 C# 项目直接使用上面的命令，无需额外参数。纯 GDScript 项目可显式添加 `--create-csharp-host`，创建最小 C# 宿主；仍需使用 Godot 4.8 .NET 编辑器和 .NET 10 SDK。

要在安装后验证真实编辑器启动，加上 `--editor <Godot.NET编辑器路径>`。安装及更新会保护本地修改，支持只检查和重复执行。旧 `godot-v0.2.1` 不含安装脚本，可继续手动复制插件目录，或切换到包含安装脚本的版本。

详见 [Godot/README.md](Godot/README.md)。

## 仓库约定
- 不放游戏文件、令牌和编辑器缓存（`.godot/` 等）。
- 引擎改动以补丁形式放在各引擎目录的 `patches/` 下，插件在未打补丁的官方引擎上也能工作（有降级行为，见各自 README）。
