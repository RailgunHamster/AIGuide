#if TOOLS
#nullable enable
using System;
using System.Collections;
using System.Collections.Generic;
using System.Globalization;
using System.Text;
using System.Text.Json;
using Godot;
using Dict = System.Collections.Generic.Dictionary<string, object?>;
using List = System.Collections.Generic.List<object?>;

namespace AIGuide;

/// <summary>
/// 极简 JSON 工具：把 JSON 解析成“松散对象”（Dict / List / string / long / double / bool / null），
/// 以及把这些对象（含 Godot 的 Vector2、Rect2 等值类型）序列化回 JSON。
/// 插件内部的报告、录制、配方都用这种松散结构，便于与 MCP 的 JSON 互转。
/// </summary>
public static class J
{
    // ------------------------------------------------------------------ 解析

    public static object? Parse(string text)
    {
        try
        {
            using var doc = JsonDocument.Parse(text, new JsonDocumentOptions { AllowTrailingCommas = true, CommentHandling = JsonCommentHandling.Skip });
            return From(doc.RootElement);
        }
        catch (Exception)
        {
            return null;
        }
    }

    private static object? From(JsonElement e)
    {
        switch (e.ValueKind)
        {
            case JsonValueKind.Object:
                var d = new Dict();
                foreach (var p in e.EnumerateObject())
                    d[p.Name] = From(p.Value);
                return d;
            case JsonValueKind.Array:
                var l = new List();
                foreach (var x in e.EnumerateArray())
                    l.Add(From(x));
                return l;
            case JsonValueKind.String:
                return e.GetString();
            case JsonValueKind.Number:
                return e.TryGetInt64(out var i) ? i : e.GetDouble();
            case JsonValueKind.True:
                return true;
            case JsonValueKind.False:
                return false;
            default:
                return null;
        }
    }

    // ------------------------------------------------------------------ 序列化

    public static string Stringify(object? v, bool indent = false)
    {
        var sb = new StringBuilder();
        Write(sb, v, indent ? "  " : null, 0);
        return sb.ToString();
    }

    private static void Write(StringBuilder sb, object? v, string? ind, int depth)
    {
        switch (v)
        {
            case null:
                sb.Append("null");
                return;
            case string s:
                Quote(sb, s);
                return;
            case StringName sn:
                Quote(sb, sn.ToString());
                return;
            case NodePath np:
                Quote(sb, np.ToString());
                return;
            case bool b:
                sb.Append(b ? "true" : "false");
                return;
            case int or long or short or byte or uint or ulong or ushort or sbyte:
                sb.Append(Convert.ToString(v, CultureInfo.InvariantCulture));
                return;
            case float or double or decimal:
                var dv = Convert.ToDouble(v, CultureInfo.InvariantCulture);
                if (double.IsNaN(dv) || double.IsInfinity(dv))
                    sb.Append("null");
                else if (Math.Abs(dv) < 1e15 && dv == Math.Floor(dv))
                    sb.Append(((long)dv).ToString(CultureInfo.InvariantCulture));
                else
                    sb.Append(dv.ToString("R", CultureInfo.InvariantCulture));
                return;
            case Enum en:
                sb.Append(Convert.ToInt64(en, CultureInfo.InvariantCulture).ToString(CultureInfo.InvariantCulture));
                return;
            case Variant vv:
                Write(sb, FromVariant(vv), ind, depth);
                return;
            case IDictionary<string, object?> dict:
                WriteObject(sb, dict, ind, depth);
                return;
            case IDictionary gdict:
                var tmp = new Dict();
                foreach (DictionaryEntry kv in gdict)
                    tmp[kv.Key?.ToString() ?? "null"] = kv.Value;
                WriteObject(sb, tmp, ind, depth);
                return;
            case IEnumerable seq:
                var items = new List();
                foreach (var x in seq)
                    items.Add(x);
                if (items.Count == 0)
                {
                    sb.Append("[]");
                    return;
                }
                sb.Append('[');
                for (int i = 0; i < items.Count; i++)
                {
                    if (i > 0)
                        sb.Append(',');
                    NewLine(sb, ind, depth + 1);
                    Write(sb, items[i], ind, depth + 1);
                }
                NewLine(sb, ind, depth);
                sb.Append(']');
                return;
            default:
                // Vector2、Rect2、Color 等 Godot 值类型：用它们自己的文字表示
                Quote(sb, v.ToString() ?? "");
                return;
        }
    }

    private static void WriteObject(StringBuilder sb, IDictionary<string, object?> dict, string? ind, int depth)
    {
        if (dict.Count == 0)
        {
            sb.Append("{}");
            return;
        }
        sb.Append('{');
        bool first = true;
        foreach (var kv in dict)
        {
            if (!first)
                sb.Append(',');
            first = false;
            NewLine(sb, ind, depth + 1);
            Quote(sb, kv.Key);
            sb.Append(ind != null ? ": " : ":");
            Write(sb, kv.Value, ind, depth + 1);
        }
        NewLine(sb, ind, depth);
        sb.Append('}');
    }

    private static void NewLine(StringBuilder sb, string? ind, int depth)
    {
        if (ind == null)
            return;
        sb.Append('\n');
        for (int i = 0; i < depth; i++)
            sb.Append(ind);
    }

    public static void Quote(StringBuilder sb, string s)
    {
        sb.Append('"');
        foreach (var ch in s)
        {
            switch (ch)
            {
                case '"': sb.Append("\\\""); break;
                case '\\': sb.Append("\\\\"); break;
                case '\n': sb.Append("\\n"); break;
                case '\r': sb.Append("\\r"); break;
                case '\t': sb.Append("\\t"); break;
                case '\b': sb.Append("\\b"); break;
                case '\f': sb.Append("\\f"); break;
                default:
                    if (ch < 0x20)
                        sb.Append("\\u").Append(((int)ch).ToString("x4", CultureInfo.InvariantCulture));
                    else
                        sb.Append(ch);
                    break;
            }
        }
        sb.Append('"');
    }

    public static string Q(string s)
    {
        var sb = new StringBuilder();
        Quote(sb, s);
        return sb.ToString();
    }

    /// <summary>Godot Variant → 松散对象（用于 editor_exec 的返回值等）。</summary>
    public static object? FromVariant(Variant v)
    {
        switch (v.VariantType)
        {
            case Variant.Type.Nil: return null;
            case Variant.Type.Bool: return v.AsBool();
            case Variant.Type.Int: return v.AsInt64();
            case Variant.Type.Float: return v.AsDouble();
            case Variant.Type.String: return v.AsString();
            case Variant.Type.StringName: return v.AsStringName().ToString();
            case Variant.Type.NodePath: return v.AsNodePath().ToString();
            case Variant.Type.Dictionary:
                var d = new Dict();
                foreach (var kv in v.AsGodotDictionary())
                    d[kv.Key.ToString()] = FromVariant(kv.Value);
                return d;
            case Variant.Type.Array:
                var l = new List();
                foreach (var x in v.AsGodotArray())
                    l.Add(FromVariant(x));
                return l;
            case Variant.Type.PackedStringArray:
                return new List(v.AsStringArray());
            case Variant.Type.Object:
                var o = v.AsGodotObject();
                return o == null ? null : $"{o.GetClass()} {o}";
            default:
                return v.ToString();
        }
    }

    // ------------------------------------------------------------------ 读取松散对象

    public static object? Get(object? d, string key) =>
        d is IDictionary<string, object?> dict && dict.TryGetValue(key, out var v) ? v : null;

    public static bool Has(object? d, string key) => d is IDictionary<string, object?> dict && dict.ContainsKey(key);

    public static string Str(object? v) => v switch
    {
        null => "",
        string s => s,
        bool b => b ? "true" : "false",
        double or float => Stringify(v),
        _ => v.ToString() ?? "",
    };

    public static string Str(object? d, string key, string def = "") => Has(d, key) && Get(d, key) != null ? Str(Get(d, key)) : def;

    public static long Int(object? v, long def = 0) => v switch
    {
        long l => l,
        int i => i,
        double db => (long)db,
        float f => (long)f,
        string s when long.TryParse(s, NumberStyles.Integer, CultureInfo.InvariantCulture, out var r) => r,
        bool b => b ? 1 : 0,
        _ => def,
    };

    public static long Int(object? d, string key, long def) => Has(d, key) ? Int(Get(d, key), def) : def;

    public static double Num(object? v, double def = 0) => v switch
    {
        long l => l,
        int i => i,
        double db => db,
        float f => f,
        string s when double.TryParse(s, NumberStyles.Float, CultureInfo.InvariantCulture, out var r) => r,
        _ => def,
    };

    public static bool Bool(object? d, string key, bool def) => Get(d, key) switch
    {
        bool b => b,
        long l => l != 0,
        string s => s == "true",
        null => def,
        _ => def,
    };

    public static Dict? AsDict(object? v) => v as Dict;

    public static List AsList(object? v) => v switch
    {
        List l => l,
        IEnumerable e when v is not string => ToList(e),
        _ => new List(),
    };

    private static List ToList(IEnumerable e)
    {
        var l = new List();
        foreach (var x in e)
            l.Add(x);
        return l;
    }

    /// <summary>键/值是序号吗（JSON 数字解析成 long，也可能是 int / double）。</summary>
    public static bool IsIndex(object? v, out int idx)
    {
        switch (v)
        {
            case int i: idx = i; return true;
            case long l: idx = (int)l; return true;
            case double d when d == Math.Floor(d): idx = (int)d; return true;
            default: idx = -1; return false;
        }
    }

    /// <summary>深拷贝松散对象。</summary>
    public static object? Clone(object? v) => v switch
    {
        Dict d => CloneDict(d),
        List l => l.ConvertAll(Clone),
        _ => v,
    };

    public static Dict CloneDict(Dict d)
    {
        var o = new Dict();
        foreach (var kv in d)
            o[kv.Key] = Clone(kv.Value);
        return o;
    }

    public static bool DeepEquals(object? a, object? b) => Stringify(a) == Stringify(b);
}
#endif
