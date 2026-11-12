using System;
using System.IO;
using System.Runtime.InteropServices;
using System.Security.Cryptography;
using System.Text.Json.Nodes;
using System.Threading.Tasks;
using Sandbox;

namespace AIGuide.Sbox;

internal static class GuideNative
{
    [UnmanagedFunctionPointer(CallingConvention.Cdecl)] private delegate IntPtr Dispatch([MarshalAs(UnmanagedType.LPUTF8Str)] string request);
    [UnmanagedFunctionPointer(CallingConvention.Cdecl)] private delegate void Free(IntPtr response);
    [UnmanagedFunctionPointer(CallingConvention.Cdecl)] private delegate void ManagedCallback(IntPtr state);
    [UnmanagedFunctionPointer(CallingConvention.Cdecl)] private delegate void InvokeManaged(ManagedCallback callback,IntPtr state);
    [SkipHotload] private static Dispatch dispatch;
    [SkipHotload] private static Free free;
    private static IntPtr library;
    [SkipHotload] private static InvokeManaged invoke;
    internal static bool Loaded => dispatch != null;
    /// <summary>Why the last load failed; null once loaded.</summary>
    internal static string LoadError;
    internal static void Load(string libraryRoot, string projectRoot)
    {
        if (dispatch != null) return;
        if (!OperatingSystem.IsWindows() || !Environment.Is64BitProcess) throw new PlatformNotSupportedException("AI Guide native UI requires Windows x64 and Qt 5.15.2.");
        var source = Path.Combine(libraryRoot, "Editor", "AIGuide", "Native", "aiguide_qt5.dll");
        if (!File.Exists(source)) throw new FileNotFoundException("AI Guide Qt adapter is missing; update the whole plugin directory.", source);
        var info=Path.Combine(libraryRoot,"Editor","AIGuide","Native","BuildInfo.json");
        if(!File.Exists(info))throw new FileNotFoundException("AI Guide native build information is missing; update the whole plugin directory.",info);
        var build=JsonNode.Parse(File.ReadAllText(info)).AsObject();
        if(!Convert.ToHexString(SHA256.HashData(File.ReadAllBytes(source))).Equals(build["adapter_sha256"]?.GetValue<string>(),StringComparison.OrdinalIgnoreCase))
            throw new NotSupportedException("AI Guide adapter does not match its build information; update the complete plugin package.");
        var modules=new System.Collections.Generic.Dictionary<string,string>(StringComparer.OrdinalIgnoreCase);
        using(var process=System.Diagnostics.Process.GetCurrentProcess())foreach(System.Diagnostics.ProcessModule module in process.Modules)
            if(module.ModuleName is "Qt5Core.dll" or "Qt5Gui.dll" or "Qt5Widgets.dll")modules[module.ModuleName]=Convert.ToHexString(SHA256.HashData(File.ReadAllBytes(module.FileName)));
        GuideCompatibility.QtProfile(build,modules);
        var hash = Convert.ToHexString(SHA256.HashData(File.ReadAllBytes(source)))[..16];
        var cache = Path.Combine(projectRoot, ".ai_guide", "native-cache", hash);
        Directory.CreateDirectory(cache);
        var path = Path.Combine(cache, "aiguide_qt5.dll");
        if (!File.Exists(path)) File.Copy(source, path);
        // Keep the module resident: Qt owns callbacks for the lifetime of this process.
        var key="SBOX_AI_GUIDE_NATIVE_"+Environment.ProcessId;
        path=Environment.GetEnvironmentVariable(key)??path;
        library = NativeLibrary.Load(path);
        Environment.SetEnvironmentVariable(key,path);
        dispatch = Marshal.GetDelegateForFunctionPointer<Dispatch>(NativeLibrary.GetExport(library, "guide_qt_json"));
        free = Marshal.GetDelegateForFunctionPointer<Free>(NativeLibrary.GetExport(library, "guide_qt_free"));
        invoke = Marshal.GetDelegateForFunctionPointer<InvokeManaged>(NativeLibrary.GetExport(library, "guide_qt_invoke"));
    }
    internal static JsonNode Call(string command, JsonObject args = null)
    {
        if (dispatch == null) throw new InvalidOperationException(LoadError is null ? "AI Guide has not initialized yet." : "AI Guide native UI is unavailable: " + LoadError);
        var response = dispatch(new JsonObject { ["command"] = command, ["args"] = args.Copy() ?? new JsonObject() }.ToJsonString());
        try
        {
            var result = JsonNode.Parse(Marshal.PtrToStringUTF8(response)).AsObject();
            if (!result.B("ok")) throw new InvalidOperationException(result.S("error"));
            return result["result"].Copy();
        }
        finally { free(response); }
    }
    // Native adapter marshals to QApplication, including while a modal exec() loop is running.
    internal static Task<JsonNode> Async(string command, JsonObject args = null) => Task.Run(() => Call(command, args));
    internal static async Task<T> Main<T>(Func<T> work)
    {
        var completion=new TaskCompletionSource<T>(TaskCreationOptions.RunContinuationsAsynchronously);
        var expired=0;
        ManagedCallback callback=_=>{if(System.Threading.Volatile.Read(ref expired)!=0)return;try{completion.TrySetResult(work());}catch(Exception e){completion.TrySetException(e);}};
        var delivery=Task.Run(()=>{invoke(callback,IntPtr.Zero);GC.KeepAlive(callback);});
        try{return await completion.Task.WaitAsync(TimeSpan.FromSeconds(20));}
        finally{System.Threading.Interlocked.Exchange(ref expired,1);_ = delivery;}
    }
}
