using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Reflection;
using System.Reflection.Metadata;
using System.Runtime.Loader;
using System.Text.Json.Nodes;
using System.Threading;
using System.Threading.Tasks;
using Editor;
using Microsoft.CodeAnalysis;
using Microsoft.CodeAnalysis.CSharp;
using Sandbox;
using AssemblyMetadata = Microsoft.CodeAnalysis.AssemblyMetadata;

namespace AIGuide.Sbox;

internal static class GuideSemantic
{
    internal static JsonNode Tree(JsonObject a)
    {
        var scene=SceneEditorSession.Active?.Scene??throw new InvalidOperationException("No editor scene.");
        var root=scene.GetAllObjects(true).Prepend(scene).FirstOrDefault(o=>a.S("root")==""||o.Id.ToString()==a.S("root")||o.Name==a.S("root"))??throw new ArgumentException("Scene root not found");
        JsonNode Walk(GameObject o,int depth){var children=new JsonArray();if(depth<a.I("depth",6))foreach(var child in o.Children)children.Add(Walk(child,depth+1));var components=new JsonArray();foreach(var c in o.Components.GetAll()){var item=new JsonObject{["id"]=c.Id.ToString(),["type"]=c.GetType().FullName,["enabled"]=c.Enabled};if(a.B("include_properties"))item["properties"]=c.Serialize();components.Add(item);}return new JsonObject{["id"]=o.Id.ToString(),["name"]=o.Name,["position"]=o.WorldPosition.ToString(),["components"]=components,["children"]=children};}
        return Walk(root,0);
    }
    internal static async Task<JsonNode> Exec(JsonObject a)
    {
        var code=a.S("code");if(code.Length==0)throw new ArgumentException("An async C# method body is required");
        var current=new[]{typeof(GuideHost).Assembly}.Concat(AssemblyLoadContext.GetLoadContext(typeof(GuideHost).Assembly)?.Assemblies??Enumerable.Empty<Assembly>()).Concat(AppDomain.CurrentDomain.GetAssemblies()).Where(t=>!t.IsDynamic&&!t.GetName().Name.StartsWith("aiguide-script-")).DistinctBy(t=>t.GetName().Name).ToArray();
        using var cancellation=new CancellationTokenSource(TimeSpan.FromMilliseconds(a.I("timeout_ms",15000)));
        var bytes=await Task.Run(()=>Compile(code,current,cancellation.Token));
        var context=new AssemblyLoadContext("AI Guide script "+Guid.NewGuid(),true);
        context.Resolving+=(_,name)=>current.FirstOrDefault(x=>AssemblyName.ReferenceMatchesDefinition(x.GetName(),name));
        try{
            using var stream=new MemoryStream(bytes);var assembly=context.LoadFromStream(stream);var method=assembly.GetType("GuideScript").GetMethod("Run");
            var ui=new GuideScriptContext();
            var task=await GuideHost.Main(()=>ui.Run(()=>{cancellation.Token.ThrowIfCancellationRequested();return (Task<object>)method.Invoke(null,new object[]{cancellation.Token});}));
            var result=await task.WaitAsync(cancellation.Token);
            return result is JsonNode json?json.DeepClone():result is GameObject go?new JsonObject{["id"]=go.Id.ToString(),["name"]=go.Name}:GJson.Node(result);
        }finally{context.Unload();}
    }
    private static unsafe byte[] Compile(string code,Assembly[] assemblies,CancellationToken cancellation)
    {
        var source="using System;using System.Linq;using System.Collections.Generic;using System.Threading;using System.Threading.Tasks;using Sandbox;using Editor;using static AIGuide.Sbox.GuideScriptApi;public static class GuideScript{public static async Task<object> Run(CancellationToken cancellationToken){\n"+code+"\nreturn null;}}";
        var metadata=new List<AssemblyMetadata>();try{
            foreach(var assembly in assemblies)if(assembly.TryGetRawMetadata(out var ptr,out var size))metadata.Add(AssemblyMetadata.Create(ModuleMetadata.CreateFromMetadata((IntPtr)ptr,size)));
            var compilation=CSharpCompilation.Create("aiguide-script-"+Guid.NewGuid().ToString("N"),new[]{CSharpSyntaxTree.ParseText(source,cancellationToken:cancellation)},metadata.Select(m=>m.GetReference()),new CSharpCompilationOptions(OutputKind.DynamicallyLinkedLibrary,allowUnsafe:true));
            using var output=new MemoryStream();var result=compilation.Emit(output,cancellationToken:cancellation);if(!result.Success)throw new ArgumentException(string.Join("\n",result.Diagnostics.Where(d=>d.Severity==DiagnosticSeverity.Error)));return output.ToArray();
        }finally{foreach(var m in metadata)m.Dispose();GC.KeepAlive(assemblies);}
    }
}

/// <summary>Script await continuations must also run while Qt owns a nested modal loop.</summary>
internal sealed class GuideScriptContext:SynchronizationContext
{
    internal T Run<T>(Func<T> callback){var previous=Current;SetSynchronizationContext(this);try{return callback();}finally{SetSynchronizationContext(previous);}}
    public override void Post(SendOrPostCallback callback,object state)
    {
        _=Task.Run(()=>GuideHost.Main(()=>Run(()=>{callback(state);return true;})));
    }
    public override SynchronizationContext CreateCopy()=>this;
}

/// <summary>Convenience globals for trusted editor_exec snippets.</summary>
public static class GuideScriptApi
{
    public static Scene Root=>SceneEditorSession.Active?.Scene;
    public static Scene Tree=>Root;
    public static GameObject Node(string name)=>Root?.GetAllObjects(true).FirstOrDefault(o=>o.Name==name||o.Id.ToString()==name);
    public static IEnumerable<T> FindAll<T>() where T:class=>Root?.GetAllObjects(true).SelectMany(o=>new object[]{o}.Concat(o.Components.GetAll())).OfType<T>()??Enumerable.Empty<T>();
}
