using System;
using System.Collections.Generic;
using System.Linq;
using System.Reflection;
using System.Text.Json.Nodes;

namespace AIGuide.Sbox;

/// <summary>Optional API adapters: missing convenience members must not prevent the plugin from compiling.</summary>
internal static class GuideCompatibility
{
    private const BindingFlags Members=BindingFlags.Public|BindingFlags.NonPublic|BindingFlags.Instance|BindingFlags.Static;
    private static object Field(object value,string name)
    {
        for(var type=value.GetType();type!=null;type=type.BaseType)
            if(type.GetField(name,Members|BindingFlags.DeclaredOnly) is {} field)return field.GetValue(value);
        return null;
    }
    internal static long? NodeId(object node)
    {
        if(node==null)return null;
        var property=node.GetType().GetProperty("NodeId",Members);
        if(property?.GetValue(node) is int id)return id;
        var native=Field(node,"native");
        var method=native?.GetType().GetMethod("GetNodeID",Members,Type.EmptyTypes);
        if(method?.Invoke(native,null) is int legacyId)return legacyId;
        // Older wrappers may not bind GetNodeID either. Native handles identify
        // surviving objects for this open document; never persist these IDs.
        return native!=null&&Field(native,"self") is IntPtr pointer&&pointer!=IntPtr.Zero?-pointer.ToInt64():null;
    }
    internal static bool? HammerModified(Type hammer)
    {
        if(hammer.GetProperty("HasUnsavedChanges",Members)?.GetValue(null) is bool value)return value;
        var app=hammer.GetField("App",Members)?.GetValue(null);
        return app?.GetType().GetMethod("IsActiveMapModified",Members,Type.EmptyTypes)?.Invoke(app,null) is bool modified?modified:null;
    }
    internal static string QtProfile(JsonObject build,IReadOnlyDictionary<string,string> loaded)
    {
        bool Matches(JsonObject modules)=>modules is {Count:>0}&&modules.All(p=>loaded.TryGetValue(p.Key,out var hash)&&hash.Equals(p.Value?.GetValue<string>(),StringComparison.OrdinalIgnoreCase));
        if(Matches(build["modules"] as JsonObject))return "build";
        if(build["compatible_module_sets"] is JsonArray profiles)foreach(var profile in profiles.OfType<JsonObject>())
            if(Matches(profile["modules"] as JsonObject))return profile["engine"]?.GetValue<string>()??"compatible";
        throw new NotSupportedException("AI Guide has no verified Qt ABI profile for this engine. Update the plugin's adapter profile; do not replace engine DLLs.");
    }
}
