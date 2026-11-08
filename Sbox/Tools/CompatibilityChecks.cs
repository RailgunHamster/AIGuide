using System;
using System.Collections.Generic;
using System.Linq;
using System.Text.Json.Nodes;
using AIGuide.Sbox;

var checks=0;
void Check(bool condition,string name){if(!condition)throw new Exception(name);checks++;Console.WriteLine("PASS "+name);}
Check(GuideCompatibility.NodeId(new PublicNode())==23,"Public MapNode.NodeId");
Check(GuideCompatibility.NodeId(new LegacyNode())==41,"Legacy MapNode private native.GetNodeID");
Check(GuideCompatibility.NodeId(new DerivedLegacyNode())==41,"Legacy native handle inherited from MapNode");
Check(GuideCompatibility.NodeId(new object())==null,"Unavailable node IDs remain unknown");
Check(GuideCompatibility.NodeId(new PointerNode())==-8192,"Oldest wrappers use document-lifetime native handle identity");
Check(GuideCompatibility.HammerModified(typeof(PublicHammer))==true,"Public Hammer dirty state");
Check(GuideCompatibility.HammerModified(typeof(LegacyHammer))==true,"Legacy native Hammer dirty state");
Check(GuideCompatibility.HammerModified(typeof(NoHammerApi))==null,"Unavailable dirty state remains unknown");
var build=JsonNode.Parse(System.IO.File.ReadAllText(args[0])).AsObject();
Dictionary<string,string> Modules(JsonObject value)=>value.ToDictionary(p=>p.Key,p=>p.Value.GetValue<string>(),StringComparer.OrdinalIgnoreCase);
Check(GuideCompatibility.QtProfile(build,Modules(build["modules"].AsObject()))=="build","Exact build Qt module set");
foreach(var profile in build["compatible_module_sets"].AsArray().OfType<JsonObject>())
    Check(GuideCompatibility.QtProfile(build,Modules(profile["modules"].AsObject()))==profile["engine"].GetValue<string>(),"Exact compatible Qt module set");
var mixed=Modules(build["modules"].AsObject());mixed["Qt5Core.dll"]="unknown";
var rejected=false;try{GuideCompatibility.QtProfile(build,mixed);}catch(NotSupportedException){rejected=true;}
Check(rejected,"Mixed or unknown Qt modules are rejected");
mixed.Remove("Qt5Core.dll");rejected=false;try{GuideCompatibility.QtProfile(build,mixed);}catch(NotSupportedException){rejected=true;}
Check(rejected,"Missing Qt module is rejected");
Console.WriteLine($"Passed {checks} compatibility checks.");

class PublicNode{public int NodeId=>23;}
class LegacyNode{private NativeNode native=new();}
class DerivedLegacyNode:LegacyNode{}
class PointerNode{private NativePointer native=new();}
struct NativePointer{private IntPtr self=new(8192);public NativePointer(){}}
struct NativeNode{internal int GetNodeID()=>41;}
static class PublicHammer{public static bool HasUnsavedChanges=>true;}
static class LegacyHammer{private static NativeApp App=new();}
struct NativeApp{internal bool IsActiveMapModified()=>true;}
static class NoHammerApi{}
