using System;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Net;
using System.Net.Sockets;
using System.Text;
using System.Text.Json.Nodes;
using System.Threading;
using System.Threading.Tasks;

namespace AIGuide.Sbox;

internal sealed class GuideTransport:IDisposable
{
    private readonly TcpListener listener;
    private readonly CancellationTokenSource stop=new();
    private readonly ConcurrentDictionary<string,byte> processing=new();
    private int clients;
    private Timer fileTimer;
    private int polling;
    internal GuideTransport(int port){if(port is <1024 or >65535)throw new ArgumentOutOfRangeException(nameof(port));listener=new TcpListener(IPAddress.Loopback,port);}
    // Start outside the editor SynchronizationContext: its frame pump stops in QDialog.exec().
    internal void Start(){listener.Start(32);_=Task.Run(Accept);fileTimer=new Timer(_=>_=Task.Run(Poll),null,200,200);}
    private async Task Poll(){if(Interlocked.Exchange(ref polling,1)!=0)return;try{PollFiles();await GuideHost.PollBackground();}catch{}finally{Interlocked.Exchange(ref polling,0);}}
    public void Dispose(){fileTimer?.Dispose();stop.Cancel();listener.Stop();}
    private async Task Accept()
    {
        while(!stop.IsCancellationRequested)
        {
            try{var client=await listener.AcceptTcpClientAsync(stop.Token);if(Interlocked.Increment(ref clients)>32){client.Dispose();Interlocked.Decrement(ref clients);continue;}_=Serve(client);}
            catch(OperationCanceledException){break;}catch(ObjectDisposedException){break;}catch{await Task.Delay(100);}
        }
    }
    private async Task Serve(TcpClient client)
    {
        using(client)using(var deadline=CancellationTokenSource.CreateLinkedTokenSource(stop.Token))
        {
            deadline.CancelAfter(TimeSpan.FromSeconds(45));
            try
            {
                var stream=client.GetStream();var header=new List<byte>();var one=new byte[1];
                while(header.Count<32768){if(await stream.ReadAsync(one,deadline.Token)==0)return;header.Add(one[0]);var n=header.Count;if(n>=4&&header[n-4]==13&&header[n-3]==10&&header[n-2]==13&&header[n-1]==10)break;}
                var lines=Encoding.ASCII.GetString(header.ToArray()).Split("\r\n");var request=lines[0].Split(' ');
                var headers=new Dictionary<string,string>(StringComparer.OrdinalIgnoreCase);foreach(var line in lines.Skip(1)){var i=line.IndexOf(':');if(i>0)headers[line[..i].Trim()]=line[(i+1)..].Trim();}
                // Consume the bounded request body before a rejection; disposing an unread TCP
                // request can reset the connection before the client receives our HTTP error.
                var hasLength=headers.TryGetValue("Content-Length",out var length);var size=0;
                if(hasLength&&(!int.TryParse(length,out size)||size<0||size>8*1024*1024)){await Reply(stream,413,new(){["error"]="Invalid Content-Length"});return;}
                var body=new byte[size];if(size>0)await stream.ReadExactlyAsync(body,deadline.Token);
                if(request.Length<2||request[1]!="/mcp"){await Reply(stream,404,new(){["error"]="Not found"});return;}
                if(!headers.TryGetValue("Host",out var host)||!Uri.TryCreate("http://"+host,UriKind.Absolute,out var uri)||!uri.IsLoopback){await Reply(stream,403,new(){["error"]="Loopback Host required"});return;}
                if(headers.TryGetValue("Origin",out var origin)&&(!Uri.TryCreate(origin,UriKind.Absolute,out var originUri)||!originUri.IsLoopback)){await Reply(stream,403,new(){["error"]="Origin rejected"});return;}
                var token=GuideHost.Config.S("token");if(token.Length>0&&headers.GetValueOrDefault("Authorization")!="Bearer "+token){await Reply(stream,401,new(){["error"]="Token required"});return;}
                if(request[0]!="POST"){await Reply(stream,405,new(){["error"]="Use stateless MCP POST; progress is available through guide_status"});return;}
                if(!hasLength){await Reply(stream,413,new(){["error"]="Content-Length required"});return;}
                var result=await Rpc(JsonNode.Parse(body).AsObject());await Reply(stream,result==null?202:200,result);
            }
            catch(Exception e){try{await Reply(client.GetStream(),400,GJson.Error(e));}catch{}}
            finally{Interlocked.Decrement(ref clients);}
        }
    }
    private static async Task Reply(NetworkStream stream,int status,JsonObject value)
    {
        var body=value==null?Array.Empty<byte>():Encoding.UTF8.GetBytes(value.ToJsonString());
        var header=Encoding.ASCII.GetBytes($"HTTP/1.1 {status} Response\r\nContent-Type: application/json; charset=utf-8\r\nContent-Length: {body.Length}\r\nConnection: close\r\nCache-Control: no-store\r\n\r\n");
        await stream.WriteAsync(header);await stream.WriteAsync(body);
    }
    internal static JsonArray Definitions()
    {
        var path=Path.Combine(GuideHost.Root,"Editor","AIGuide","ToolDefinitions.json");
        var result=JsonNode.Parse(File.ReadAllText(path)).AsArray();
        foreach(var tool in GuideDebugBridge.Definitions())result.Add(tool.DeepClone());
        return result;
    }
    internal static async Task<JsonObject> Rpc(JsonObject message)
    {
        var id=message["id"].Copy();var method=message.S("method");if(method.StartsWith("notifications/",StringComparison.Ordinal))return null;
        JsonNode result;
        try
        {
            switch(method)
            {
                case "initialize":result=new JsonObject{["protocolVersion"]=message.O("params").S("protocolVersion","2025-11-25"),["capabilities"]=new JsonObject{["tools"]=new JsonObject{["listChanged"]=false}},["serverInfo"]=new JsonObject{["name"]="sbox-ai-guide",["version"]=GuideHost.Version},["instructions"]="Use editor_state, then ui_snapshot/ui_find and ui_describe. Teach by rehearsing with ui_act, rehearsal_stop(rollback:true,save_as:...), then recipe_play or ui_guide. UI targets use live refs or persistent /qt/ locators. editor_exec accepts an async C# method body and has no automatic undo. This is trusted local editor automation."};break;
                case "ping":result=new JsonObject();break;
                case "tools/list":result=new JsonObject{["tools"]=Definitions()};break;
                case "resources/list":result=new JsonObject{["resources"]=new JsonArray()};break;
                case "resources/templates/list":result=new JsonObject{["resourceTemplates"]=new JsonArray()};break;
                case "prompts/list":result=new JsonObject{["prompts"]=new JsonArray()};break;
                case "tools/call":{
                    var args=message.O("params");var name=args.S("name");
                    try{
                        var output=await GuideHost.Call(name,args.O("arguments"));
                        if(name=="editor_screenshot"&&output?["path"] is JsonValue path){var bytes=File.ReadAllBytes(path.GetValue<string>());result=new JsonObject{["content"]=new JsonArray(new JsonObject{["type"]="image",["mimeType"]="image/png",["data"]=Convert.ToBase64String(bytes)},new JsonObject{["type"]="text",["text"]=output.ToJsonString()})};}
                        else result=GJson.TextResult(output,output is JsonObject o&&o.ContainsKey("ok")&&!o.B("ok"));
                    }catch(Exception e){result=GJson.TextResult(GJson.Error(e),true);}break;
                }
                default:return new(){["jsonrpc"]="2.0",["id"]=id,["error"]=new JsonObject{["code"]=-32601,["message"]="Unknown method: "+method}};
            }
            return new(){["jsonrpc"]="2.0",["id"]=id,["result"]=result};
        }
        catch(Exception e){return new(){["jsonrpc"]="2.0",["id"]=id,["error"]=new JsonObject{["code"]=-32603,["message"]=e.GetBaseException().Message}};}
    }
    internal void PollFiles()
    {
        var incoming=Path.Combine(GuideHost.Root,".ai_guide_bridge","in");if(!Directory.Exists(incoming))return;
        foreach(var path in Directory.EnumerateFiles(incoming,"*.json").Take(16))if(processing.TryAdd(path,0))_=Task.Run(async()=>{
            try{
                if(new FileInfo(path).Length>8*1024*1024)throw new InvalidOperationException("Bridge request exceeds 8 MiB");
                var message=JsonNode.Parse(await File.ReadAllTextAsync(path)).AsObject();var reply=await Rpc(message);
                var dir=Path.Combine(GuideHost.Root,".ai_guide_bridge","out");Directory.CreateDirectory(dir);var dest=Path.Combine(dir,Path.GetFileName(path));await File.WriteAllTextAsync(dest+".tmp",reply?.ToJsonString()??"null");File.Move(dest+".tmp",dest,true);
                var processed=Path.Combine(GuideHost.Root,".ai_guide_bridge","processed");Directory.CreateDirectory(processed);File.Move(path,Path.Combine(processed,Path.GetFileName(path)),true);
            }catch(IOException){processing.TryRemove(path,out _);}catch(Exception e){var dir=Path.Combine(GuideHost.Root,".ai_guide_bridge","out");Directory.CreateDirectory(dir);await File.WriteAllTextAsync(Path.Combine(dir,Path.GetFileName(path)),GJson.Error(e).ToJsonString());}
        });
    }
}
