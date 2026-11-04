# 引擎补丁

## ai_guide_synthetic_hover.patch
- 文件：`scene/main/viewport.cpp`，`Viewport::_update_mouse_over(const Ref<InputEventMouse> &)`，代码里搜索 `[AI Guide patch]`。
- 作用：操作系统光标不在任何 Godot 窗口上时，程序合成的鼠标事件（`Input.ParseInputEvent` / `Window.PushInput`）也能更新目标窗口的悬停状态。
  没有这个补丁时，按钮（要求按下时处于悬停）不会响应合成点击，AI Guide 只能临时移动真实光标去点，用户同时在用鼠标时就会失败。
- 不影响：真实硬件输入；光标在某个 Godot 窗口上时的原有逻辑；引擎内部的被动悬停事件（`DEVICE_ID_INTERNAL`）。
- 应用：在 Godot 源码根目录执行 `git apply <本目录>/ai_guide_synthetic_hover.patch`。

## 编译 .NET 版编辑器（Windows，已验证：4.8-beta2）
```powershell
cd <godot 源码>
git apply <本目录>\ai_guide_synthetic_hover.patch
python misc\scripts\install_d3d12_sdk_windows.py        # 首次：D3D12 编译依赖
uvx scons -j28 platform=windows target=editor module_mono_enabled=yes accesskit=no
# 生成 C# 胶水代码（注意用 GUI 版 exe + start /wait；console 包装器在部分机器上报 CreateProcess 193）
start "" /wait bin\godot.windows.editor.x86_64.mono.exe --headless --generate-mono-glue modules\mono\glue
python modules\mono\build_scripts\build_assemblies.py --godot-output-dir=bin
# 让 NuGet 找到本机编译出的 Godot.NET.Sdk / GodotSharp（版本号形如 4.8.0-beta）
dotnet nuget add source "<godot 源码>\bin\GodotSharp\Tools\nupkgs" --name GodotCustom
```
产物：`bin\godot.windows.editor.x86_64.mono.exe`（连同 `bin\GodotSharp\`）。

注意：
- 已经运行着的编辑器会占用 `bin\D3D12Core.dll`，scons 拷贝时报“拒绝访问”。已加载的 DLL 可以改名不能覆盖，先改名再编译即可。
- 已有非 .NET 版的编译产物时，加 `module_mono_enabled=yes` 增量编译约 2 分钟。
- Godot 4.8 的 C# 工程目标框架是 `net10.0`。
