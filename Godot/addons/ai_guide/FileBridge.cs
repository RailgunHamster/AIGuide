#if TOOLS
#nullable enable
using System;
using System.Collections.Generic;
using System.Threading.Tasks;
using Godot;
using Dict = System.Collections.Generic.Dictionary<string, object?>;

namespace AIGuide;

/// <summary>
/// 文件桥：给连不上 127.0.0.1 的 Agent（沙盒、远程 VM、同步盘）用的备用通道。
/// 仅当项目里存在 res://.ai_guide_bridge/in/ 目录时启用（Godot 会忽略以 . 开头的目录，不会导入）。
/// 请求：把 JSON-RPC 消息写入 in/&lt;任意名&gt;.json（先写 .tmp 再改名，避免读到半个文件）。
/// 回复：插件处理后写入 out/&lt;同名&gt;.json，并删除请求文件。
/// </summary>
[Tool]
public partial class FileBridge : Node
{
    private const double PollSec = 0.2;
    private const ulong RequestTimeoutMs = 30000;

    public Tools? Tools;
    private string _dirIn = "";
    private string _dirOut = "";
    private double _acc;
    private readonly HashSet<string> _busy = new();

    public override void _Ready()
    {
        var b = ProjectSettings.GlobalizePath("res://.ai_guide_bridge");
        _dirIn = b.PathJoin("in");
        _dirOut = b.PathJoin("out");
    }

    public override void _Process(double delta)
    {
        if (Tools == null)
            return;
        _acc += delta;
        if (_acc < PollSec)
            return;
        _acc = 0;
        if (!DirAccess.DirExistsAbsolute(_dirIn))
            return;
        foreach (var f in DirAccess.GetFilesAt(_dirIn))
        {
            if (!f.EndsWith(".json") || _busy.Contains(f))
                continue;
            _busy.Add(f);
            _ = Handle(f);
        }
    }

    private async Task Handle(string f)
    {
        try
        {
            var path = _dirIn.PathJoin(f);
            var text = FileAccess.GetFileAsString(path);
            DirAccess.RemoveAbsolute(path);
            object? reply;
            if (J.Parse(text) is Dict msg)
                reply = await WithTimeout(msg);
            else
                reply = new Dict { ["jsonrpc"] = "2.0", ["id"] = null, ["error"] = new Dict { ["code"] = -32700, ["message"] = "Parse error" } };
            DirAccess.MakeDirRecursiveAbsolute(_dirOut);
            var tmp = _dirOut.PathJoin(f + ".tmp");
            using (var outF = FileAccess.Open(tmp, FileAccess.ModeFlags.Write))
            {
                if (outF == null)
                    return;
                outF.StoreString(reply != null ? J.Stringify(reply) : "{}");
            }
            DirAccess.RenameAbsolute(tmp, _dirOut.PathJoin(f));
        }
        catch (Exception e)
        {
            GD.PushWarning($"[AI Guide] 文件桥请求 {f} 处理出错：{e}");
        }
        finally
        {
            _busy.Remove(f);
        }
    }

    /// <summary>与 HTTP 服务端相同的兜底：处理超时也要写回一个错误，客户端不必一直等。</summary>
    private async Task<object?> WithTimeout(Dict msg)
    {
        var task = Tools!.HandleRpc(msg);
        var t0 = Time.GetTicksMsec();
        while (!task.IsCompleted && Time.GetTicksMsec() - t0 < RequestTimeoutMs)
            await ToSignal(GetTree(), SceneTree.SignalName.ProcessFrame);
        if (task.IsCompleted)
        {
            if (task.IsFaulted)
                return new Dict { ["jsonrpc"] = "2.0", ["id"] = msg.GetValueOrDefault("id"), ["error"] = new Dict { ["code"] = -32603, ["message"] = task.Exception?.GetBaseException().Message ?? "error" } };
            return task.Result;
        }
        GD.PushWarning($"[AI Guide] 文件桥请求超过 {RequestTimeoutMs / 1000} 秒未完成：{UiTree.Left(J.Stringify(msg.GetValueOrDefault("params") ?? new Dict()), 300)}");
        return new Dict { ["jsonrpc"] = "2.0", ["id"] = msg.GetValueOrDefault("id"), ["error"] = new Dict { ["code"] = -32603, ["message"] = McpServer.TimeoutMessage } };
    }
}
#endif
