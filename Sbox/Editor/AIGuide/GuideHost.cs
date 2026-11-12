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
    public const string Version = "1.3.3";
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
    // Environment.TickCount64 of the last context push from EditorEvent.Frame. The background poller only
    // pushes context itself while Frame is not running (Qt modal loops), instead of doubling the work.
    private static long lastFrameContext;
    private static long nextAnnotation;
    private static DateTime nextNativeAttempt;
    private static System.Reflection.FieldInfo allObjectsField;
    private static readonly System.Runtime.CompilerServices.ConditionalWeakTable<ControlWidget,string> annotated=new();
    internal static string Root => root ?? Path.GetFullPath(Project.Current.GetRootPath());
    internal static string LibraryRoot => EditorUtility.Projects.GetAll()
        .FirstOrDefault(p => p.Config.FullIdent == "railgunhamster.aiguide")?.GetRootPath()
        ?? throw new DirectoryNotFoundException("AI Guide library is missing. Finish the Verso project update before opening the editor.");
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
    private static void Hotload(){if(GuideNative.Loaded)_=GuideNative.Async("rehearsal_mode",new(){["enabled"]=false});transport?.Dispose();transport=null;root=null;initializationError=null;next=DateTime.MinValue;}
    [EditorEvent.Frame]
    public static void Frame()
    {
        for (var i=0; i<16 && main.TryDequeue(out var action); i++) action();
        if (DateTime.UtcNow < next || Project.Current is null) return;
        next = DateTime.UtcNow.AddMilliseconds(200);
        try
        {
            var current = Path.GetFullPath(Project.Current.GetRootPath());
            var changed=false;
            if (root != current || (Config.B("enabled",true) && transport is null))
            {
                transport?.Dispose(); transport=null; root=current; Directory.CreateDirectory(Data);
                var path=Path.Combine(Data,"config.json");
                Config=File.Exists(path)?JsonNode.Parse(File.ReadAllText(path)).AsObject():new JsonObject { ["enabled"]=true,["port"]=7660 };
                if(Config.B("enabled",true)) { transport=new GuideTransport(Config.I("port",7660)); transport.Start(); }
                nextNativeAttempt=DateTime.MinValue;changed=true;
            }
            // The Qt adapter is optional for the transport: editor_state, editor_exec and the file bridge keep working, and the
            // status reports why native UI is unavailable (e.g. an engine update shipped Qt DLLs without a verified profile).
            // A failed load is retried every 10 s rather than every frame, because it hashes the engine's Qt DLLs.
            if(!GuideNative.Loaded&&DateTime.UtcNow>=nextNativeAttempt)
            {
                var previous=GuideNative.LoadError;
                try{GuideNative.Load(LibraryRoot, root);GuideNative.LoadError=null;}
                catch(Exception e){GuideNative.LoadError=e.GetBaseException().Message;nextNativeAttempt=DateTime.UtcNow.AddSeconds(10);if(previous!=GuideNative.LoadError)Log.Warning("AI Guide native UI unavailable: "+GuideNative.LoadError);}
                changed|=previous!=GuideNative.LoadError||GuideNative.Loaded;
                initializationError=GuideNative.LoadError;
            }
            if(changed)File.WriteAllText(Path.Combine(Data,"session.json"),new JsonObject { ["pid"]=Environment.ProcessId,["url"]=Address,["version"]=Version,["project"]=root,["native_error"]=GuideNative.LoadError }.ToJsonString());
            if(GuideNative.Loaded){GuideNative.Call("context", Context());Volatile.Write(ref lastFrameContext,Environment.TickCount64);}
        }
        catch(Exception e){initializationError=e.GetBaseException().Message;next=DateTime.UtcNow.AddSeconds(2);}
    }
    internal static async Task PollBackground()
    {
        if(!GuideNative.Loaded)return;
        if(Environment.TickCount64-Volatile.Read(ref lastFrameContext)>500)await Main(()=>GuideNative.Call("context",Context()));
        // Only events newer than the last one handled; older adapters ignore "since" and return all.
        foreach(var v in (await GuideNative.Async("events",new(){["since"]=lastEvent})).AsArray()){
            var e=v.AsObject();var id=e.I("seq");if(id<=lastEvent)continue;lastEvent=id;
            if(e.S("event")=="restart_requested"&&!restartPending){restartPending=true;try{await Restart();}finally{restartPending=false;}}
        }
    }
    internal static JsonObject Context()
    {
        AnnotateControls();
        var s=SceneEditorSession.Active;
        var selected=new JsonArray();
        if(s is not null)foreach(var o in s.Selection.OfType<GameObject>()){selected.Add(o.Id.ToString());selected.Add(o.Name);}
        var inspecting=new JsonArray();var inspected=EditorUtility.InspectorObject;
        if(inspected!=null){inspecting.Add(inspected.GetType().FullName);inspecting.Add(inspected.ToString());if(inspected is GameObject go){inspecting.Add(go.Id.ToString());inspecting.Add(go.Name);}if(inspected is Component component)inspecting.Add(component.Id.ToString());if(inspected is Editor.MapDoc.MapNode node){if(GuideCompatibility.NodeId(node) is {} nodeId)inspecting.Add(nodeId.ToString());inspecting.Add(node.Name);}}
        JsonObject hammer=null;if(Editor.MapEditor.Hammer.Open&&Editor.MapEditor.Hammer.ActiveMap is {} map){var nodes=Editor.MapEditor.Selection.All.ToArray();foreach(var node in nodes){if(GuideCompatibility.NodeId(node) is {} nodeId)selected.Add(nodeId.ToString());selected.Add(node.Name);}hammer=new(){["file"]=map.PathName,["modified"]=GuideCompatibility.HammerModified(typeof(Editor.MapEditor.Hammer)),["selection_mode"]=Editor.MapEditor.Selection.SelectMode.ToString(),["current_material"]=Editor.MapEditor.Hammer.CurrentMaterial?.ResourcePath,["selected"]=GJson.Node(nodes.Select(n=>new{NodeId=GuideCompatibility.NodeId(n),n.Name}))};}
        return new JsonObject { ["project"]=Root,["selected"]=selected,["main_screen"]=Editor.MapEditor.Hammer.Window?.IsActiveWindow==true?"Hammer":s?.Scene?.Name??"",["inspecting"]=inspecting,["scene"]=s?.Scene?.Source?.ResourcePath,["open_scenes"]=GJson.Node(SceneEditorSession.All.Select(x=>new{path=x.Scene?.Source?.ResourcePath,name=x.Scene?.Name,modified=x.HasUnsavedChanges})),["hammer"]=hammer,["playing"]=Game.IsPlaying };
    }
    /// <summary>Annotate managed inspector controls for the native Qt tree, including their child editors.</summary>
    /// <remarks>Reads every live ControlWidget's property value, so it runs at most once a second and only
    /// touches Qt properties whose text changed (each SetProperty is a native call plus a Qt event).</remarks>
    private static void AnnotateControls()
    {
        var now=Environment.TickCount64;if(now<nextAnnotation)return;nextAnnotation=now+1000;
        allObjectsField??=typeof(Editor.QObject).GetField("AllObjects",System.Reflection.BindingFlags.Static|System.Reflection.BindingFlags.NonPublic);
        if(allObjectsField?.GetValue(null) is not System.Collections.IDictionary objects)return;
        foreach(var control in objects.Values.OfType<ControlWidget>().ToArray())
        {
            if(!control.IsValid||control.SerializedProperty is not {} property)continue;
            try
            {
                var name=property.Name;var value=property.GetValue<object>()?.ToString()??"null";var key=name+"\n"+value;
                if(annotated.TryGetValue(control,out var previous)&&previous==key)continue;
                control.SetProperty("aiguideProperty",name);control.SetProperty("aiguideValue",value);annotated.AddOrUpdate(control,key);
            }
            catch{}
        }
    }
    internal static JsonNode EditorState()=>new JsonObject { ["version"]=Version,["project"]=Root,["editor"]=Context(),["ui"]=GuideNative.Loaded?GuideNative.Call("state"):null,["native"]=GuideNative.Loaded?GuideNative.Call("info"):new JsonObject{["error"]=GuideNative.LoadError},["transport"]=Address,["rehearsing"]=rehearsal!=null };
    private static string RecipeDir=>Path.Combine(Root,"Config","AIGuide","Recipes");
    private static string BuiltInRecipeDir=>Path.Combine(LibraryRoot,"Editor","AIGuide","Recipes");
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
        lastGuide=a.DeepClone().AsObject();await PrepareGuide(a);return await GuideNative.Async("guide",a);
    }
    private static async Task PrepareGuide(JsonObject recipe)
    {
        foreach(var entry in recipe.A("open_assets"))
        {
            var path=entry.GetValue<string>().Replace('\\','/');
            if(Path.IsPathRooted(path)||path.Split('/').Any(p=>p==".."))throw new ArgumentException("Tutorial assets must use project resource paths.");
            await Main(()=>{var asset=Editor.AssetSystem.FindByPath(path)??throw new FileNotFoundException("教程需要的资源不存在："+path);asset.OpenInEditor();return true;});
            var extension=Path.GetExtension(path);if(extension!=".vmap"&&extension!=".vmat")continue;
            var until=DateTime.UtcNow.AddSeconds(15);var ready=false;
            do
            {
                if(extension==".vmap")ready=await Main(()=>Editor.MapEditor.Hammer.Open&&Editor.MapEditor.Hammer.ActiveMap is {} map&&map.PathName.Replace('\\','/').EndsWith(path,StringComparison.OrdinalIgnoreCase));
                else{var found=(await GuideNative.Async("find",new(){["class"]="CQMaterialEditor",["text"]=Path.GetFileNameWithoutExtension(path),["kind"]="control"})).AsObject();ready=found.I("total")>0;}
                if(!ready)await Task.Delay(100);
            }while(!ready&&DateTime.UtcNow<until);
            if(!ready)throw new TimeoutException("等待教程资源窗口超时："+path+"。请先处理当前保存或提示对话框。");
        }
    }
    private static async Task<JsonNode> Restart()
    {
        if(checkpoint==null||lastGuide==null)throw new InvalidOperationException("No guide checkpoint.");
        var report=await checkpoint.Restore();checkpoint=await GuideCheckpoint.Capture(Root,lastGuide.S("title"));
        await PrepareGuide(lastGuide);await GuideNative.Async("guide",lastGuide);return report;
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
                    if(step.S("project_path") is {Length:>0} projectPath){var full=Path.GetFullPath(Path.Combine(Root,projectPath));if(!full.StartsWith(Root.TrimEnd(Path.DirectorySeparatorChar)+Path.DirectorySeparatorChar,StringComparison.OrdinalIgnoreCase))throw new ArgumentException("Tutorial path escapes the project.");action["text"]=full;}
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
                rehearsal=await GuideCheckpoint.Capture(Root,a.S("title","Rehearsal"));recorded=new();await GuideNative.Async("rehearsal_mode",new(){["enabled"]=true});return new JsonObject{["ok"]=true,["title"]=rehearsal.Title};
            }
            case "rehearsal_stop":{
                if(rehearsal==null)throw new InvalidOperationException("No rehearsal is running.");
                var old=rehearsal;rehearsal=null;await GuideNative.Async("rehearsal_mode",new(){["enabled"]=false});var steps=new JsonArray();int i=0;
                foreach(var entry in recorded){var record=entry.AsObject();var action=record.S("action");var step=new JsonObject{["target"]=record["target"].Copy(),["fallback"]=record["fallback"].Copy(),["text"]=a.A("texts").ElementAtOrDefault(i)?.GetValue<string>()??$"{action}: {record.O("target_info").S("text")}",["until"]="click",["action"]=record.Copy()};
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
                var recipe=LoadRecipe(a.S("name"));foreach(var k in new[]{"title","dim","activate_window"})if(a.ContainsKey(k))recipe[k]=a[k].Copy();
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
    [Menu("Hammer","AI Guide/教程与状态","school")]
    public static void ShowPanel(){var w=new GuidePanel();w.Show();}
    [Menu("Editor","AI Guide/停止引导","stop")]
    [Menu("Hammer","AI Guide/停止引导","stop")]
    public static void StopGuide(){if(GuideNative.Loaded)GuideNative.Call("clear");}
    [Menu("Editor","AI Guide/复制 MCP 地址","link")]
    [Menu("Hammer","AI Guide/复制 MCP 地址","link")]
    public static void CopyAddress(){EditorUtility.Clipboard.Copy(Address);}
}

internal sealed class GuidePanel:Window
{
    private readonly List<Button> recipeButtons=new();
    private readonly Label status;
    public GuidePanel()
    {
        Title="AI Guide";Size=new Vector2(480,460);
        var content=new Widget(this);Canvas=content;content.Layout=Layout.Column();
        var layout=content.Layout;layout.Margin=14;layout.Spacing=10;
        layout.Add(new Label("AI Guide · "+GuideHost.Version,content));
        layout.Add(new Label(GuideHost.Address,content));
        status=new Label("选择教程后会高亮目标，并自动切换到对应窗口。",content){WordWrap=true};layout.Add(status);
        status.SetStyles("color:#ffb74d;");
        if(GuideHost.InitializationError is {Length:>0} error)layout.Add(new Label(error,content));
        layout.Add(new Button("停止当前引导",content){Clicked=GuideHost.StopGuide});
        foreach(var recipe in GuideHost.Recipes()){
            var name=recipe["name"].GetValue<string>();
            var button=new Button(name,content){Clicked=()=>_=StartRecipe(name)};layout.Add(button);recipeButtons.Add(button);
        }
        layout.AddStretchCell();
    }
    private async Task StartRecipe(string name)
    {
        foreach(var button in recipeButtons)if(button.IsValid)button.Enabled=false;
        status.Text="正在准备教程与回退检查点…完成后自动定位到第一个目标。";
        try{await GuideHost.Call("recipe_play",new(){["name"]=name});if(IsValid)status.Text="教程已启动。正确操作后会自动推进，也可使用上一步、下一步和定位。";}
        catch(Exception e){if(IsValid)status.Text="教程未能启动："+e.GetBaseException().Message;}
        finally{if(IsValid)foreach(var button in recipeButtons)if(button.IsValid)button.Enabled=true;}
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
