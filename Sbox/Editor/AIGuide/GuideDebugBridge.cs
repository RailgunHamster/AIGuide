using System;
using System.IO;
using System.Linq;
using System.Text.Json.Nodes;
using System.Threading.Tasks;

namespace AIGuide.Sbox;

/// <summary>Optional bridge: AI Guide neither installs nor activates the separate runtime debugger.</summary>
internal static class GuideDebugBridge
{
    internal static JsonArray Definitions()
    {
        var result=new JsonArray();
        var type=AppDomain.CurrentDomain.GetAssemblies().Select(a=>a.GetType("AIDebug.Sbox.EditorIntegration.DebugEditor")).FirstOrDefault(t=>t!=null);
        if(type==null)return result;
        foreach(var entry in ((JsonNode)type.GetMethod("Tools").Invoke(null,null)).AsArray()){
            var tool=entry.DeepClone().AsObject();tool["name"]="ai_debug_"+tool["name"].GetValue<string>();
            result.Add(tool);
        }
        return result;
    }
    internal static async Task<JsonNode> Call(string tool,JsonObject args)
    {
        var type=AppDomain.CurrentDomain.GetAssemblies().FirstOrDefault(a=>a.GetName().Name=="AIDebug.Sbox")?.GetType("AIDebug.Sbox.RuntimeService");
        if(type==null)throw new InvalidOperationException("Load the AI Debug Library and activate AI Debug through its editor menu first.");
        var task=(Task<string>)type.GetMethod("CallToolJson").Invoke(null,new object[]{tool,args.ToJsonString()});
        return JsonNode.Parse(await task);
    }
}
