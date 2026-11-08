using System;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text.Json.Nodes;
using System.Threading;
using System.Threading.Tasks;
using Editor;
using Editor.Mcp;
using Sandbox;

namespace AIGuide.Sbox;

/// <summary>Project editor plugin. Native UI continues to work inside Qt modal loops.</summary>
public static class GuideHost
{
    public const string Version = "1.1.1";
    private static readonly ConcurrentQueue<Action> main = new();
    private static GuideTransport transport;
    private static string root;
    private static string initializationError;
    private static DateTime next;
    private static int lastEvent;
    private static GuideCheckpoint rehearsal, checkpoint;
    private static JsonArray recorded = new();
    private static JsonObject lastGuide;
    private static bool restartPending;
    internal static string Root => root ?? Path.GetFullPath(Project.Current.GetRootPath());
    internal static string Data => Path.Combine(Root, ".ai_guide");
    internal static JsonObject Config { get; private set; } = new();
    public static string Address => $"http://127.0.0.1:{Config.I("port",7660)}/mcp";
    public static string InitializationError => initializationError;
    internal static Task<T> Main<T>(Func<T> work)
    {
        if(GuideNative.Loaded)return GuideNative.Main(work);
        var completion = new TaskCompletionSource<T>(TaskCreationOptions.RunContinuationsAsynchronously);
        main.Enqueue(() => { try { completion.TrySetResult(work()); } catch (Exception e) { completion.TrySetException(e); } });
        return completion.Task.WaitAsync(TimeSpan.FromSeconds(20));
    }
    [EditorEvent.Hotload]
    private static void Hotload(){transport?.Dispose();transport=null;root=null;initializationError=null;next=DateTime.MinValue;}
    [EditorEvent.Frame]
    public static void Frame()
    {
        for (var i=0; i<16 && main.TryDequeue(out var action); i++) action();
        if (DateTime.UtcNow < next || Project.Current is null) return;
        next = DateTime.UtcNow.AddMilliseconds(200);
        try
        {
            var current = Path.GetFullPath(Project.Current.GetRootPath());
            if (root != current || !GuideNative.Loaded || (Config.B("enabled",true) && transport is null))
            {
                transport?.Dispose(); transport=null; root=current; Directory.CreateDirectory(Data);
                var path=Path.Combine(Data,"config.json");
                Config=File.Exists(path)?JsonNode.Parse(File.ReadAllText(path)).AsObject():new JsonObject { ["enabled"]=true,["port"]=7660 };
                GuideNative.Load(root);
                if(Config.B("enabled",true)) { transport=new GuideTransport(Config.I("port",7660)); transport.Start(); }
                File.WriteAllText(Path.Combine(Data,"session.json"),new JsonObject { ["pid"]=Environment.ProcessId,["url"]=Address,["version"]=Version,["project"]=root }.ToJsonString());
                initializationError=null;
            }
            GuideNative.Call("context", Context());
        }
        catch(Exception e){initializationError=e.GetBaseException().Message;next=DateTime.UtcNow.AddSeconds(2);}
    }
    internal static async Task PollBackground()
    {
        if(!GuideNative.Loaded)return;
        await Main(()=>GuideNative.Call("context",Context()));
        foreach(var v in (await GuideNative.Async("events")).AsArray()){
            var e=v.AsObject();var id=e.I("seq");if(id<=lastEvent)continue;lastEvent=id;
            if(e.S("event")=="restart_requested"&&!restartPending){restartPending=true;try{await Restart();}finally{restartPending=false;}}
        }
    }
    internal static JsonObject Context()
    {
        // Annotate managed inspector controls for the native Qt tree, including their child editors.
        var objects=typeof(Editor.QObject).GetField("AllObjects",System.Reflection.BindingFlags.Static|System.Reflection.BindingFlags.NonPublic)?.GetValue(null) as System.Collections.IDictionary;
        if(objects!=null)foreach(var control in objects.Values.OfType<ControlWidget>().ToArray())if(control.IsValid&&control.SerializedProperty is {} property){try{control.SetProperty("aiguideProperty",property.Name);control.SetProperty("aiguideValue",property.GetValue<object>()?.ToString()??"null");}catch{}}
        var s=SceneEditorSession.Active;
        var selected=new JsonArray();
        if(s is not null)foreach(var o in s.Selection.OfType<GameObject>()){selected.Add(o.Id.ToString());selected.Add(o.Name);}
        var inspecting=new JsonArray();var inspected=EditorUtility.InspectorObject;
        if(inspected!=null){inspecting.Add(inspected.GetType().FullName);inspecting.Add(inspected.ToString());if(inspected is GameObject go){inspecting.Add(go.Id.ToString());inspecting.Add(go.Name);}if(inspected is Component component)inspecting.Add(component.Id.ToString());if(inspected is Editor.MapDoc.MapNode node){if(GuideCompatibility.NodeId(node) is {} nodeId)inspecting.Add(nodeId.ToString());inspecting.Add(node.Name);}}
        JsonObject hammer=null;if(Editor.MapEditor.Hammer.Open&&Editor.MapEditor.Hammer.ActiveMap is {} map){var nodes=Editor.MapEditor.Selection.All.ToArray();foreach(var node in nodes){if(GuideCompatibility.NodeId(node) is {} nodeId)selected.Add(nodeId.ToString());selected.Add(node.Name);}hammer=new(){["file"]=map.PathName,["modified"]=GuideCompatibility.HammerModified(typeof(Editor.MapEditor.Hammer)),["selected"]=GJson.Node(nodes.Select(n=>new{NodeId=GuideCompatibility.NodeId(n),n.Name}))};}
        return new JsonObject { ["project"]=Root,["selected"]=selected,["main_screen"]=Editor.MapEditor.Hammer.Window?.IsActiveWindow==true?"Hammer":s?.Scene?.Name??"",["inspecting"]=inspecting,["scene"]=s?.Scene?.Source?.ResourcePath,["open_scenes"]=GJson.Node(SceneEditorSession.All.Select(x=>new{path=x.Scene?.Source?.ResourcePath,name=x.Scene?.Name,modified=x.HasUnsavedChanges})),["hammer"]=hammer,["playing"]=Game.IsPlaying };
    }
    internal static JsonNode EditorState()=>new JsonObject { ["version"]=Version,["project"]=Root,["editor"]=Context(),["ui"]=GuideNative.Call("state"),["native"]=GuideNative.Call("info"),["transport"]=Address,["rehearsing"]=rehearsal!=null };
    private static string RecipeDir=>Path.Combine(Root,"Config","AIGuide","Recipes");
    private static string BuiltInRecipeDir=>Path.Combine(Root,"Editor","AIGuide","Recipes");
    private static string RecipePath(string name)
    {
        if(string.IsNullOrWhiteSpace(name)||name.IndexOfAny(Path.GetInvalidFileNameChars())>=0||name.Contains("..")||name.Contains('/')||name.Contains('\\'))throw new ArgumentException("Recipe name must be one file name without an extension or path.");
        return Path.Combine(RecipeDir,name+".json");
    }
    internal static JsonArray Recipes()
    {
        var result=new JsonArray();var paths=new Dictionary<string,string>(StringComparer.OrdinalIgnoreCase);
        foreach(var dir in new[]{BuiltInRecipeDir,RecipeDir})if(Directory.Exists(dir))foreach(var path in Directory.EnumerateFiles(dir,"*.json"))paths[Path.GetFileNameWithoutExtension(path)]=path;
        foreach(var pair in paths){var path=pair.Value;try{var r=JsonNode.Parse(File.ReadAllText(path)).AsObject();result.Add(new JsonObject{["name"]=pair.Key,["title"]=r.S("title"),["steps"]=r.A("steps").Count,["created"]=r.S("created"),["source"]=path.StartsWith(RecipeDir,StringComparison.OrdinalIgnoreCase)?"project":"builtin"});}catch(Exception e){result.Add(new JsonObject{["name"]=pair.Key,["error"]=e.Message});}}
        return result;
    }
    private static JsonObject LoadRecipe(string name)
    {
        var p=RecipePath(name);if(!File.Exists(p))p=Path.Combine(BuiltInRecipeDir,name+".json");if(!File.Exists(p))throw new FileNotFoundException("Recipe not found",p);return JsonNode.Parse(File.ReadAllText(p)).AsObject();
    }
    private static async Task<JsonNode> BeginGuide(JsonObject a)
    {
        if(a.A("steps").Count==0)throw new ArgumentException("Guide requires at least one step.");
        await GuideNative.Async("clear");await Main(()=>{checkpoint?.Release();return true;});
        checkpoint=await GuideCheckpoint.Capture(Root,a.S("title"));
        lastGuide=a.DeepClone().AsObject();return await GuideNative.Async("guide",a);
    }
    private static async Task<JsonNode> Restart()
    {
        if(checkpoint==null||lastGuide==null)throw new InvalidOperationException("No guide checkpoint.");
        var report=await checkpoint.Restore();checkpoint=await GuideCheckpoint.Capture(Root,lastGuide.S("title"));
        await GuideNative.Async("guide",lastGuide);return report;
    }
    internal static async Task<JsonNode> Call(string name,JsonObject a=null)
    {
        a??=new JsonObject();
        if(name.StartsWith("ai_debug_",StringComparison.Ordinal))return await GuideDebugBridge.Call(name[9..],a);
        if(name.StartsWith("aiguide_",StringComparison.Ordinal))name=name[8..];
        switch(name)
        {
            case "editor_state":return await Main(EditorState);
            case "scene_tree":return await Main(()=>GuideSemantic.Tree(a));
            case "editor_exec":return await GuideSemantic.Exec(a);
            case "ui_snapshot":return await GuideNative.Async("snapshot",a);
            case "ui_find":return await GuideNative.Async("find",a);
            case "ui_describe":return await GuideNative.Async("describe",a);
            case "ui_highlight":return await GuideNative.Async("highlight",a);
            case "ui_act":{
                var r=await GuideNative.Async("act",a);
                await Task.Delay(a.B("show")?450:100);
                var delivery=await GuideNative.Async("action_status",new(){["id"]=r["action_id"].Copy()});
                r["delivery"]=delivery;
                if(delivery is JsonObject d&&!d.B("ok"))r["ok"]=false;
                if(rehearsal!=null&&r["ok"]?.GetValue<bool>()==true)recorded.Add(r["record"].Copy());
                return r;
            }
            case "ui_guide":return await BeginGuide(a);
            case "guide_status":return await GuideNative.Async("guide_status");
            case "guide_control":{
                if(a.S("action")=="restart"){
                    if(a.B("rollback",true))return await Restart();
                    return await GuideNative.Async("guide",lastGuide??throw new InvalidOperationException("No guide to restart."));
                }
                if(a.S("action")=="do_step"){
                    var status=(await GuideNative.Async("guide_status")).AsObject();var step=status.A("steps").ElementAtOrDefault(status.I("index"))?.AsObject()??throw new InvalidOperationException("No current guide step.");
                    var action=step["action"] is JsonObject o?o.DeepClone().AsObject():new JsonObject{["action"]=step.S("action","click")};
                    action["target"]=step["target"].Copy();action["show"]=true;
                    var result=await Call("ui_act",action);
                    if(result?["ok"]?.GetValue<bool>()!=true)return result;
                    var current=(await GuideNative.Async("guide_status")).AsObject();
                    if(current.I("index")==status.I("index")&&current.S("state")=="running")await GuideNative.Async("guide_control",new(){["action"]="next"});
                    return await GuideNative.Async("guide_status");
                }
                return await GuideNative.Async("guide_control",a);
            }
            case "editor_screenshot":{
                var dir=Path.Combine(Data,"screenshots");Directory.CreateDirectory(dir);var args=a.DeepClone().AsObject();args["path"]=Path.Combine(dir,Guid.NewGuid().ToString("N")+".png");
                return await GuideNative.Async("screenshot",args);
            }
            case "rehearsal_start":{
                if(rehearsal!=null)throw new InvalidOperationException("A rehearsal is already running.");
                rehearsal=await GuideCheckpoint.Capture(Root,a.S("title","Rehearsal"));recorded=new();return new JsonObject{["ok"]=true,["title"]=rehearsal.Title};
            }
            case "rehearsal_stop":{
                if(rehearsal==null)throw new InvalidOperationException("No rehearsal is running.");
                var old=rehearsal;rehearsal=null;var steps=new JsonArray();int i=0;
                foreach(var entry in recorded){var record=entry.AsObject();var action=record.S("action");var step=new JsonObject{["target"]=record["target"].Copy(),["text"]=a.A("texts").ElementAtOrDefault(i)?.GetValue<string>()??$"{action}: {record.O("target_info").S("text")}",["action"]=record.Copy()};
                    if(action=="key")step["until"]=new JsonObject{["key"]=record.S("keys")};
                    if(action=="type"||action=="paste"){step["clipboard"]=record.S("text");step["until"]=new JsonObject{["text_contains"]=new JsonObject{["target"]=record["target"].Copy(),["text"]=record.S("text")}};}
                    steps.Add(step);i++;}
                var guide=new JsonObject{["title"]=a.S("title",old.Title),["steps"]=steps,["dim"]=a.B("dim",true),["created"]=DateTimeOffset.UtcNow.ToString("O"),["version"]=Version};
                JsonNode report;
                if(a.B("rollback",true))report=await old.Restore();else{await Main(()=>{old.Release();return true;});report=new JsonObject{["rolled_back"]=false};}
                if(a.S("save_as") is {Length:>0} recipe){Directory.CreateDirectory(RecipeDir);File.WriteAllText(RecipePath(recipe),guide.ToJsonString(GJson.Options));}
                if(a.B("start_guide"))await BeginGuide(guide);
                return new JsonObject{["ok"]=true,["guide"]=guide,["rollback"]=report};
            }
            case "recipe_list":return Recipes();
            case "recipe_play":{
                var recipe=LoadRecipe(a.S("name"));foreach(var k in new[]{"title","dim"})if(a.ContainsKey(k))recipe[k]=a[k].Copy();
                var texts=a.A("texts");for(int i=0;i<Math.Min(texts.Count,recipe.A("steps").Count);i++)if(texts[i]?.GetValue<string>() is {Length:>0} text)recipe.A("steps")[i]["text"]=text;
                return await BeginGuide(recipe);
            }
            case "ai_guide_reload":{
                await GuideNative.Async("clear");await Main(()=>{root=null;transport?.Dispose();transport=null;return true;});return new JsonObject{["ok"]=true,["note"]="Plugin services rebuild on the next editor frame. C# source hotloads through s&box; native adapter updates require restarting the editor."};
            }
            default:throw new ArgumentException("Unknown AI Guide tool: "+name);
        }
    }
    [Menu("Editor","AI Guide/教程与状态","school")]
    public static void ShowPanel(){var w=new GuidePanel();w.Show();}
    [Menu("Editor","AI Guide/停止引导","stop")]
    public static void StopGuide(){GuideNative.Call("clear");}
    [Menu("Editor","AI Guide/复制 MCP 地址","link")]
    public static void CopyAddress(){EditorUtility.Clipboard.Copy(Address);}
}

internal sealed class GuidePanel:Window
{
    public GuidePanel()
    {
        Title="AI Guide";Size=new Vector2(480,460);
        var content=new Widget(this);Canvas=content;content.Layout=Layout.Column();
        var layout=content.Layout;layout.Margin=14;layout.Spacing=10;
        layout.Add(new Label("AI Guide · "+GuideHost.Version,content));
        layout.Add(new Label(GuideHost.Address,content));
        if(GuideHost.InitializationError is {Length:>0} error)layout.Add(new Label(error,content));
        layout.Add(new Button("停止当前引导",content){Clicked=GuideHost.StopGuide});
        foreach(var recipe in GuideHost.Recipes()){
            var name=recipe["name"].GetValue<string>();
            layout.Add(new Button(name,content){Clicked=()=>_=GuideHost.Call("recipe_play",new(){["name"]=name})});
        }
        layout.AddStretchCell();
    }
}

[McpToolset("aiguide","Project AI Guide: native Qt UI discovery, real input, overlays, tutorials, rehearsal and rollback")]
public static class GuideTools
{
    /// <summary>Call an AI Guide tool. Use aiguide_tools to discover its JSON schema.</summary>
    [McpTool("aiguide_call")]
    public static async Task<object> Call(string tool,JsonObject args=null)=>await GuideHost.Call(tool,args);
    /// <summary>List every tool with its original JSON schema, also exposed by the standalone HTTP/file MCP endpoint.</summary>
    [McpTool.ReadOnly("aiguide_tools")]
    public static object Tools()=>GuideTransport.Definitions();
    [McpTool.ReadOnly("aiguide_status")]
    public static object Status()=>new {GuideHost.Version,GuideHost.Address,GuideHost.InitializationError,Native=GuideNative.Loaded};
}
