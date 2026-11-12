# AI Guide — s&box

这是 **RailgunHamster/AIGuide** 的 s&box 实现，与 `Godot/` 并列。源码、内置示例、测试和通用文档都由本仓库的 Git 管理。

```text
AIGuide/
├── Godot/
└── Sbox/
    ├── Editor/AIGuide/     插件代码、原生适配层、内置教程
    ├── Tools/             集成测试
    ├── docs/              通用使用手册与架构
    └── .sbproj            标准 Library 入口
```

## 兼容官方与自定义引擎

插件以 Windows x64 的官方 s&box 编辑器为基线，当前实际验证版本为 Steam `26.10.27c`；同时兼容 LTN 自定义引擎与 railgunhamster 所用旧版 SDK。`MapNode.NodeId`、`Hammer.HasUnsavedChanges` 等便利接口通过能力检测适配，缺少这些成员不会再造成整个项目编译失败。

原生层支持 `BuildInfo.json` 明确记录的 Qt 5.15.2 模块组合，校验适配 DLL 自身与整组 Qt DLL。当前适配层已在自定义引擎、官方 Steam 引擎和使用 railgunhamster 旧 Qt 的本机隔离编辑器中各通过 37 项集成检查；旧版托管 SDK 另做完整插件编译检查。没有替换团队机器上的引擎 DLL。重建适配 DLL 后需重新验证其他 Qt 组合。

1.2.1 的高亮使用独立、点击穿透且不抢焦点的透明工具窗口，由桌面合成器绘制，不再覆盖 Hammer 原生 GPU 子窗口。定时检查仅在目标位置或内容变化时刷新，不再每 80 毫秒隐藏、显示或置顶。更新后需重启编辑器以加载新的原生适配层。

1.3.0 补齐 Godot 风格的交互流程：默认点击推进、操作条件检测、失效定位 fallback、返回步骤后的确认、滚动定位，以及整窗目标可见的边框。开始教程和切换步骤时自动呈现目标窗口；气泡提供“定位”按钮。教程作者可用 `open_assets` 打开所需地图/材质，`project_path` 在同事自己的工作区生成保存路径。可自动检测的操作应编写真实完成条件，不能把整篇教程写成手动下一步。

1.3.2 合并了 railgunhamster 的教程性能改动，在 Hammer 顶部加入 **AI Guide** 菜单，并支持团队引擎 `387a5b026` 的 Qt 模块。教程缓存当前目标、降低控件树扫描频率，并只更新发生变化的检查器属性。原生层加载失败时，编辑器状态和文件桥仍可使用，界面会报告加载失败原因。

1.3.3 支持按步骤预填文件名和搜索框。保存仍需用户确认，已输入的文字会保留。

1.3.4 允许教程限制点击范围。高亮目标、步骤明确允许的控件和教程导航按钮仍可操作。

## 标准 Library

`Sbox/.sbproj` 声明 `railgunhamster.aiguide` Library。项目使用 Verso Git 依赖，将本仓库 `Sbox/` 映射到 `Libraries/AIGuide/`，固定到锁文件指定的提交。

成员完成 Verso 项目更新并打开编辑器后，s&box 自动发现并编译 Library。场景编辑器和 Hammer 都可从 **AI Guide → 教程与状态**打开教程面板。插件源码由 Library 独立管理。

C# 编译产物由编辑器生成，不入库。原生 Qt 适配 DLL 与验证信息随 Git 提供；它只在主项目 `.ai_guide/native-cache/` 中建立运行缓存。内置教程从 Library 读取，项目教程仍在 `Config/AIGuide/Recipes/`，同名项目教程优先。本机端口、会话和截图在 `.ai_guide/`，不共享。

主项目若保留历史部署副本，可在自己的 `Metadata.Compiler.IgnoreFolders` 中排除 `AIGuide` / `AIDebug`；Library 采用独立编译配置。更新插件前保存并关闭编辑器，完成 Verso 更新后重新打开。修改插件请在原 Git 工作区或 Verso 依赖开发模式中进行，再升级主项目锁文件。

## 可选运行时调试

[AIDebug/Sbox](https://github.com/RailgunHamster/AIDebug/tree/main/Sbox) 是独立的 Library 依赖。项目声明并获取该依赖后，本服务额外列出 19 个 `ai_debug_*` 工具，转发到同进程中已激活的调试模块；不会为了显示工具而加载或启动它。仅使用 AI Guide 时仍为 17 项工具。调试配置位于 `.ai_debug/`，与 `.ai_guide/` 分开。旧的 `debug_enabled` 配置不再使用。

业务存档和输入回放不属于该调试接口；s&box 也不提供 GDScript 求值。演练回退仍用于编辑器的场景撤销和文件恢复，但跳过全部点目录，保留调试会话、VCS 和其他本机工具状态。

## 开发与验证

```powershell
& ./Sbox/Tools/Test-AIGuide.ps1 -Project 'C:/path/to/game'
& ./Sbox/Tools/Test-Compatibility.ps1 -Engine 'C:/path/to/sbox/game'
```

集成测试连接已打开项目的编辑器，请使用专用验证实例和测试地图；它会操作界面与演练回退。用 `-SkipHammer` 仅验证通用功能。SDK 兼容检查由维护者运行，不属于日常使用步骤。

重建原生适配层时明确指定引擎 Qt DLL 目录：

```powershell
& ./Sbox/Editor/AIGuide/Native/Build.ps1 `
  -QtSdk 'C:/Qt/5.15.2/msvc2019_64' `
  -EngineBin 'C:/path/to/sbox/bin/win64'
```

它针对 Windows x64 和已验证的 Facepunch/Valve Qt ABI，加载前核对二进制。头文件和分配器接口有变化时需先适配再重建。

详见 [使用手册与架构](docs/使用手册/AI%20Guide编辑器插件.md)。Hammer 删除原节点后的自动重建不在现有公开快照 API 的能力内，回退会明确报告未完成；这类操作需要原生 Undo。AIDebug 以独立标准 Library 提供运行时调试。
