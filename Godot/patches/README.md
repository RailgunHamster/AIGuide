# 引擎补丁

## ai_guide_synthetic_hover.patch
- 文件：`scene/main/viewport.cpp`，`Viewport::_update_mouse_over(const Ref<InputEventMouse> &)`，代码里搜索 `[AI Guide patch]`。
- 作用：操作系统光标不在任何 Godot 窗口上时，程序合成的鼠标事件（`Input.ParseInputEvent` / `Window.PushInput`）也能更新目标窗口的悬停状态。
  没有这个补丁时，按钮（要求按下时处于悬停）不会响应合成点击，AI Guide 只能临时移动真实光标去点，用户同时在用鼠标时就会失败。
- 不影响：真实硬件输入；光标在某个 Godot 窗口上时的原有逻辑；引擎内部的被动悬停事件（`DEVICE_ID_INTERNAL`）。
- 此补丁供引擎维护者维护悬停行为；集成编辑器的使用者无需处理补丁。它不包含 AIGuide 的完整内置启动入口。

## 引擎维护（Windows，已验证：4.8-beta2）

在维护新的 Godot 源码分支时，先审阅补丁并用 `git apply --check <补丁路径>` 验证适用性，再将对应改动纳入引擎版本控制。已有悬停改动的集成引擎无需重复应用。

已接入内置插件的引擎使用自己的构建工具：

```powershell
cd <godot 源码>
uvx scons -j28 platform=windows target=editor module_mono_enabled=yes accesskit=no
& ./bin/godot.windows.editor.x86_64.mono.exe --headless --generate-mono-glue modules/mono/glue
python modules\mono\build_scripts\build_assemblies.py --godot-output-dir=bin
```
分发编辑器时，提供 `bin\godot.windows.editor.x86_64.mono.exe` 及其 `bin\GodotSharp\` 依赖，包含内置 `Godot.AIGuide.dll`。

注意：
- 构建前保存并关闭使用该引擎的编辑器，避免输出 DLL 被进程占用。
- 已有非 .NET 版的编译产物时，加 `module_mono_enabled=yes` 增量编译约 2 分钟。
- Godot 4.8 的 C# 工程目标框架是 `net10.0`。
