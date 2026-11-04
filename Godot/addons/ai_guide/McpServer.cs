#if TOOLS
#nullable enable
using System;
using System.Collections.Generic;
using System.Text;
using System.Threading.Tasks;
using Godot;
using Dict = System.Collections.Generic.Dictionary<string, object?>;
using List = System.Collections.Generic.List<object?>;

namespace AIGuide;

/// <summary>
/// 极简 MCP Streamable HTTP 服务（只返回 application/json，不提供 SSE 流）。
/// 只监听 127.0.0.1，并拒绝来自非本地网页的 Origin，防止 DNS rebinding。
/// 全部在主线程里轮询（Godot 的 TcpServer），工具调用因此可以直接操作编辑器。
/// </summary>
[Tool]
public partial class McpServer : Node
{
    private const int MaxBody = 16 * 1024 * 1024;
    private const ulong IdleTimeoutMs = 30000;
    private const ulong BusyTimeoutMs = 30000;
    public const string TimeoutMessage = "工具执行超时，可重试。最常见原因：执行期间插件被重新加载（改完插件源码重新构建时，正在执行的请求会被丢弃）";

    private static readonly Dictionary<int, string> StatusText = new()
    {
        [200] = "OK", [202] = "Accepted", [204] = "No Content", [400] = "Bad Request", [403] = "Forbidden",
        [404] = "Not Found", [405] = "Method Not Allowed", [413] = "Payload Too Large", [500] = "Internal Server Error",
    };

    /// <summary>工具分发对象。</summary>
    public Tools? Tools;

    private sealed class Client
    {
        public StreamPeerTcp Peer = null!;
        public List<byte> Buf = new();
        public bool Busy;
        public ulong Born;
        public ulong BusySince;
        public ulong CloseAt;
        public int HeadEnd = -1;
        public string RpcSummary = "";
        public object? RpcId;
    }

    private sealed class Request
    {
        public bool Bad;
        public bool TooBig;
        public string Method = "";
        public string Path = "";
        public Dictionary<string, string> Headers = new();
        public string Body = "";
    }

    private TcpServer? _tcp;
    private readonly List<Client> _clients = new();
    private string _sessionId = "";

    public Error Start(int port)
    {
        _tcp = new TcpServer();
        _sessionId = $"{GD.Randi():x8}{Time.GetTicksUsec() & 0x7fffffff:x8}";
        return _tcp.Listen((ushort)port, "127.0.0.1");
    }

    public void Stop()
    {
        foreach (var c in _clients)
            c.Peer.DisconnectFromHost();
        _clients.Clear();
        _tcp?.Stop();
    }

    public override void _Process(double delta)
    {
        if (_tcp == null || Tools == null)
            return;
        while (_tcp.IsListening() && _tcp.IsConnectionAvailable())
            _clients.Add(new Client { Peer = _tcp.TakeConnection(), Born = Time.GetTicksMsec() });

        var now = Time.GetTicksMsec();
        for (int i = _clients.Count - 1; i >= 0; i--)
        {
            var c = _clients[i];
            var peer = c.Peer;
            peer.Poll();
            var st = peer.GetStatus();
            if (c.CloseAt > 0)
            {
                if (now >= c.CloseAt || st != StreamPeerTcp.Status.Connected)
                {
                    peer.DisconnectFromHost();
                    _clients.RemoveAt(i);
                }
                continue;
            }
            if (st == StreamPeerTcp.Status.Error || st == StreamPeerTcp.Status.None)
            {
                _clients.RemoveAt(i);
                continue;
            }
            if (c.Busy)
            {
                // 工具若迟迟不返回，超时后回一个错误，避免客户端挂起。
                if (now - c.BusySince > BusyTimeoutMs)
                {
                    GD.PushWarning($"[AI Guide] 请求超过 {BusyTimeoutMs / 1000} 秒未完成：{c.RpcSummary}");
                    RespondJson(c, new Dict { ["jsonrpc"] = "2.0", ["id"] = c.RpcId, ["error"] = new Dict { ["code"] = -32603, ["message"] = TimeoutMessage } });
                }
                continue;
            }
            var avail = peer.GetAvailableBytes();
            if (avail > 0)
            {
                var got = peer.GetPartialData(avail);
                if ((Error)got[0].AsInt32() == Error.Ok)
                    c.Buf.AddRange(got[1].AsByteArray());
            }
            var req = TryParse(c);
            if (req == null)
            {
                if (now - c.Born > IdleTimeoutMs)
                {
                    peer.DisconnectFromHost();
                    _clients.RemoveAt(i);
                }
                continue;
            }
            c.Busy = true;
            c.BusySince = now;
            _ = Serve(c, req);
        }
    }

    private static int HeaderEnd(List<byte> buf)
    {
        for (int i = 0; i + 3 < buf.Count; i++)
            if (buf[i] == 13 && buf[i + 1] == 10 && buf[i + 2] == 13 && buf[i + 3] == 10)
                return i;
        return -1;
    }

    private static Request? TryParse(Client c)
    {
        var buf = c.Buf;
        if (c.HeadEnd < 0)
        {
            c.HeadEnd = HeaderEnd(buf);
            if (c.HeadEnd < 0)
                return null;
        }
        var head = Encoding.UTF8.GetString(buf.GetRange(0, c.HeadEnd).ToArray());
        var lines = head.Split("\r\n");
        var first = lines[0].Split(' ');
        if (first.Length < 2)
            return new Request { Bad = true };
        var headers = new Dictionary<string, string>();
        for (int j = 1; j < lines.Length; j++)
        {
            var k = lines[j].IndexOf(':');
            if (k > 0)
                headers[lines[j][..k].Trim().ToLowerInvariant()] = lines[j][(k + 1)..].Trim();
        }
        int.TryParse(headers.GetValueOrDefault("content-length", "0"), out var length);
        if (length > MaxBody)
            return new Request { TooBig = true, Method = first[0], Path = first[1], Headers = headers };
        int bodyStart = c.HeadEnd + 4;
        if (buf.Count < bodyStart + length)
            return null;
        return new Request
        {
            Method = first[0],
            Path = first[1],
            Headers = headers,
            Body = Encoding.UTF8.GetString(buf.GetRange(bodyStart, length).ToArray()),
        };
    }

    private static bool IsLocalOrigin(string origin)
    {
        foreach (var prefix in new[] { "http://127.0.0.1", "http://localhost", "http://[::1]", "https://127.0.0.1", "https://localhost" })
            if (origin == prefix || origin.StartsWith(prefix + ":") || origin.StartsWith(prefix + "/"))
                return true;
        return false;
    }

    private async Task Serve(Client c, Request req)
    {
        try
        {
            if (req.Bad)
            {
                Respond(c, 400, "");
                return;
            }
            if (req.TooBig)
            {
                Respond(c, 413, "");
                return;
            }
            var origin = req.Headers.GetValueOrDefault("origin", "");
            if (origin != "" && !IsLocalOrigin(origin))
            {
                Respond(c, 403, "Forbidden origin");
                return;
            }
            var path = req.Path.Split('?')[0];
            if (path != "/mcp" && path != "/mcp/" && path != "/")
            {
                Respond(c, 404, "Use /mcp");
                return;
            }
            switch (req.Method)
            {
                case "OPTIONS":
                    Respond(c, 204, "", new() { ["Allow"] = "POST, GET, DELETE, OPTIONS" });
                    break;
                case "GET":
                    // 本服务不开放服务器主动推送的 SSE 流，按规范返回 405。
                    Respond(c, 405, "", new() { ["Allow"] = "POST, DELETE" });
                    break;
                case "DELETE":
                    Respond(c, 200, "");
                    break;
                case "POST":
                    await ServePost(c, req.Body);
                    break;
                default:
                    Respond(c, 405, "", new() { ["Allow"] = "POST, GET, DELETE, OPTIONS" });
                    break;
            }
        }
        catch (Exception e)
        {
            GD.PushWarning($"[AI Guide] HTTP 请求处理出错：{e}");
            if (c.CloseAt == 0)
                RespondJson(c, new Dict { ["jsonrpc"] = "2.0", ["id"] = c.RpcId, ["error"] = new Dict { ["code"] = -32603, ["message"] = e.Message } });
        }
    }

    private async Task ServePost(Client c, string body)
    {
        var parsed = J.Parse(body);
        if (parsed is Dict pd)
        {
            c.RpcSummary = UiTree.Left(J.Stringify(pd.GetValueOrDefault("params") ?? new Dict()), 300);
            c.RpcId = pd.GetValueOrDefault("id");
        }
        if (parsed == null)
        {
            RespondJson(c, new Dict { ["jsonrpc"] = "2.0", ["id"] = null, ["error"] = new Dict { ["code"] = -32700, ["message"] = "Parse error" } });
            return;
        }
        object? outV = null;
        if (parsed is List arr)
        {
            var results = new List();
            foreach (var m in arr)
            {
                var r = await Dispatch(m);
                if (r != null)
                    results.Add(r);
            }
            if (results.Count > 0)
                outV = results;
        }
        else
        {
            outV = await Dispatch(parsed);
        }
        if (c.CloseAt > 0)
            return; // 已经因为超时回复过了
        if (outV == null)
            Respond(c, 202, "");
        else
            RespondJson(c, outV);
    }

    private async Task<object?> Dispatch(object? m)
    {
        if (m is not Dict msg)
            return new Dict { ["jsonrpc"] = "2.0", ["id"] = null, ["error"] = new Dict { ["code"] = -32600, ["message"] = "Invalid Request" } };
        var res = await Tools!.HandleRpc(msg);
        return msg.ContainsKey("id") ? res : null;
    }

    private void RespondJson(Client c, object data) =>
        Respond(c, 200, J.Stringify(data), new() { ["Content-Type"] = "application/json; charset=utf-8", ["Mcp-Session-Id"] = _sessionId });

    private static void Respond(Client c, int code, string body, Dictionary<string, string>? extra = null)
    {
        extra ??= new();
        var peer = c.Peer;
        var bytes = Encoding.UTF8.GetBytes(body);
        var head = new StringBuilder();
        head.Append($"HTTP/1.1 {code} {StatusText.GetValueOrDefault(code, "OK")}\r\n");
        head.Append($"Content-Length: {bytes.Length}\r\nConnection: close\r\nCache-Control: no-store\r\n");
        if (!extra.ContainsKey("Content-Type") && bytes.Length > 0)
            head.Append("Content-Type: text/plain; charset=utf-8\r\n");
        foreach (var (k, v) in extra)
            head.Append($"{k}: {v}\r\n");
        head.Append("\r\n");
        if (peer.GetStatus() == StreamPeerTcp.Status.Connected)
        {
            peer.PutData(Encoding.UTF8.GetBytes(head.ToString()));
            if (bytes.Length > 0)
                peer.PutData(bytes);
        }
        // 延迟一小段时间再断开，避免数据还在发送缓冲区时被 RST。
        c.CloseAt = Time.GetTicksMsec() + 100;
    }
}
#endif
