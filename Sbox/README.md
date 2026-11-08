# AI Guide — s&box

这是 **RailgunHamster/AIGuide** 的 s&box 实现，与 `Godot/` 并列。源码、内置示例、测试和通用文档都由本仓库的 Git 管理。

```text
AIGuide/
├── Godot/
└── Sbox/
    ├── Editor/AIGuide/     插件代码、原生适配层、内置教程
    ├── Tools/             集成测试
    ├── docs/              通用使用手册与架构
    └── Install.ps1        安装到游戏项目
```

## 安装

在 PowerShell 7 中，从本仓库执行：

```powershell
& ./Sbox/Install.ps1 -Project 'C:/path/to/game'
& ./Sbox/Install.ps1 -Project 'C:/path/to/game' -Action Check
& ./Sbox/Install.ps1 -Project 'C:/path/to/game' -DryRun
& ./Sbox/Install.ps1 -Project 'C:/path/to/game' -Action Verify -Engine 'C:/path/to/sbox/game'
```

目标项目必须有 `.sbproj`。安装前关闭编辑器；安装器把插件复制到项目 `Editor/AIGuide/`，保留旧部署备份，并将部署目录加入项目的 `.loreignore` 或 `.gitignore`。**请在本仓库修改插件，再重新安装；项目中的部署副本不独立维护。**

支持名为 `.sbproj` 或 `game.sbproj` 的项目文件，目标目录须只有一份项目配置。1.1 安装器参考 Godot 安装流程（`a04957c`）补上本地修改保护、只检查、安装预演和实际编辑器验证。`Check` 与 `DryRun` 不写项目；已安装文件有本地改动，或存在来源不明的额外文件时，会拒绝覆盖。可在安装时加 `-Verify -Engine ...`，验证编译、原生适配层和 MCP schema；失败会恢复部署、忽略规则和安装记录。已有打开的编辑器只在独立 `Verify` 时复用，不替用户关闭它。

源仓库版本由调用方固定，安装器不获取或切换 Git。只检出 `Sbox/` 即可安装，不依赖 `Godot/`。

内置示例位于插件 `Recipes/`；项目自己录制的教程写入 `Config/AIGuide/Recipes/`，由游戏项目自己的版本控制管理，同名教程优先使用项目版本。本机端口、令牌、会话和安装来源记录在 `.ai_guide/`，不共享。

安装后重启编辑器，从顶部 `AI Guide → 教程与状态` 打开。HTTP MCP 默认地址是 `http://127.0.0.1:7660/mcp`。

## 可选运行时调试

[AIDebug/Sbox](https://github.com/RailgunHamster/AIDebug/tree/main/Sbox) 独立管理和安装，不是 AI Guide 的必需依赖。安装后，本服务额外列出 19 个 `ai_debug_*` 工具，转发到同进程中已激活的调试模块；不会为了显示工具而加载或启动它。单独安装 AI Guide 仍为 17 项工具。调试配置位于 `.ai_debug/`，与 `.ai_guide/` 分开。旧的 `debug_enabled` 配置不再使用。

业务存档和输入回放不属于该调试接口；s&box 也不提供 GDScript 求值。演练回退仍用于编辑器的场景撤销和文件恢复，但跳过全部点目录，保留调试会话、VCS 和其他本机工具状态。

## 开发与验证

```powershell
& ./Sbox/Tools/Test-AIGuide.ps1 -Project 'C:/path/to/game'
& ./Sbox/Tools/Test-Install.ps1
```

集成测试连接已打开项目的编辑器，请使用专用验证实例和测试地图；它会操作界面与演练回退。用 `-SkipHammer` 仅验证通用功能。安装器测试独立运行，不启动编辑器。

重建原生适配层时明确指定引擎 Qt DLL 目录：

```powershell
& ./Sbox/Editor/AIGuide/Native/Build.ps1 `
  -QtSdk 'C:/Qt/5.15.2/msvc2019_64' `
  -EngineBin 'C:/path/to/sbox/bin/win64'
```

它针对 Windows x64 和已验证的 Facepunch/Valve Qt ABI，加载前核对二进制。头文件和分配器接口有变化时需先适配再重建。

详见 [使用手册与架构](docs/使用手册/AI%20Guide编辑器插件.md)。Hammer 删除原节点后的自动重建不在现有公开快照 API 的能力内，回退会明确报告未完成；这类操作需要原生 Undo。`ai_debug` 当前仅作功能参考。
