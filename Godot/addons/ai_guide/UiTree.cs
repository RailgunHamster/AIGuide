#if TOOLS
#nullable enable
using System;
using System.Collections.Generic;
using System.Linq;
using Godot;
using Range = Godot.Range;
using Dict = System.Collections.Generic.Dictionary<string, object?>;
using List = System.Collections.Generic.List<object?>;

namespace AIGuide;

/// <summary>控件内的子项：树条目 / 标签页 / 列表项 / 菜单项 / 菜单栏条目。</summary>
public sealed class SubItem
{
    public string Kind = "";
    public object Key = "";
    public string Text = "";
    public Rect2 Local;
    public List<string> Flags = new();
    public string Extra = "";
    public Node? Node;
    public TreeItem? Item;
    public string Shown = "";
}

/// <summary>解析出的目标：节点（+子项）及其当前几何信息。</summary>
public sealed class Geo
{
    public bool Ok = true;
    public string Error = "";
    public Node? Node;
    public string Kind = "";
    public object? Key;
    public Window? Window;
    public Rect2 Rect;
    public Rect2 Vis;
    public bool Visible;
    public string Ref = "";
    public string Text = "";
    /// <summary>复合控件里真正用来操作的子控件区域（AI 代点时点这里）。</summary>
    public Rect2? Hot;
    public List<string>? Flags;
    public TreeItem? Item;
    /// <summary>find() 的排序分数。</summary>
    public int Score;

    public static Geo Fail(string e) => new() { Ok = false, Error = e };
    public BaseRef Base => new(Node!, Kind, Key);
}

/// <summary>目标的稳定引用：节点 + 子项类别 + 子项键。</summary>
public readonly record struct BaseRef(Node Node, string Kind, object? Key);

/// <summary>
/// 编辑器 UI 树：快照、ref、定位符(locator)、子项与目标解析。
///
/// ref：形如 e12（控件）或 e12.40（控件内子项）。同一个对象在其存活期间 ref 不变，可跨快照使用。
/// locator：持久化的语义路径，如 /EditorNode/@Panel/.../Scene/@Tree::tree="Player/Body"
/// 段的写法：
///   Name                 节点名（不以 @ 开头的稳定名字）
///   @Class               按类名匹配（自动命名的节点）
///   @Class{"text":"X"}   按类名 + 身份字段匹配（text / tooltip / property / title / first_property）
///   ...:2                第 n 个匹配（从 0 开始）
///   **                   下一段在任意深度的后代中查找
/// 子项后缀：::tree="A/B" | ::tab="标题"或序号 | ::list=... | ::menu=... | ::bar=...
/// </summary>
public sealed class UiTree
{
    public static readonly StringName OwnMeta = "_ai_guide_own";
    public const int MaxText = 80;

    private readonly Dictionary<string, (ulong Id, string Kind, object? Key)> _refs = new();
    private readonly Dictionary<string, string> _refIndex = new();
    private int _nextRef = 1;

    // ============================================================ 基础

    public Window TreeRoot() => EditorInterface.Singleton.GetBaseControl().GetTree().Root;

    public bool IsOwn(Node? n)
    {
        var p = n;
        while (p != null)
        {
            if (p.HasMeta(OwnMeta))
                return true;
            p = p.GetParent();
        }
        return false;
    }

    private static string KeyStr(object? key) => key == null ? "null" : J.Stringify(key);

    public string RefFor(GodotObject node, string kind = "", object? key = null)
    {
        var k = $"{node.GetInstanceId()}|{kind}|{KeyStr(key)}";
        if (_refIndex.TryGetValue(k, out var existing))
            return existing;
        string r = kind == "" ? $"e{_nextRef}" : $"{RefFor(node)}.{_nextRef}";
        _nextRef++;
        _refs[r] = (node.GetInstanceId(), kind, key);
        _refIndex[k] = r;
        return r;
    }

    public void Prune()
    {
        var dead = _refs.Where(kv => !GodotObject.IsInstanceIdValid(kv.Value.Id)).Select(kv => kv.Key).ToList();
        foreach (var r in dead)
        {
            var e = _refs[r];
            _refIndex.Remove($"{e.Id}|{e.Kind}|{KeyStr(e.Key)}");
            _refs.Remove(r);
        }
    }

    public static string ClipText(string s, int n = MaxText)
    {
        s = s.Replace("\n", "⏎").Trim();
        return s.Length <= n ? s : s[..(n - 1)] + "…";
    }

    private static IEnumerable<Node> Children(Node n) => n.GetChildren(true);

    // ============================================================ 文本与身份

    public string TextOf(Node n)
    {
        switch (n)
        {
            case EditorProperty ep: return ep.Label;
            case Button b: return b.Text;
            case LinkButton lb: return lb.Text;
            case Label l: return l.Text;
            case LineEdit le: return le.Text;
            case TextEdit te: return Left(te.Text, 200);
            case RichTextLabel rt: return Left(rt.GetParsedText(), 200);
            case Window w: return w.Title;
            case Control c: return c.AccessibilityName;
            default: return "";
        }
    }

    public static string Left(string s, int n) => s.Length <= n ? s : s[..n];

    /// <summary>界面上实际显示的文字（编辑器会在绘制时用 atr() 翻译原文）。</summary>
    public static string Shown(Node? n, string t)
    {
        if (t == "" || n == null)
            return t;
        return n.Atr(t);
    }

    /// <summary>快照里的文字表示："显示文字"，若与原文不同再附上 /"原文"。</summary>
    public static string LabelOf(Node n, string t, int maxLen = MaxText)
    {
        var d = Shown(n, t);
        var outS = J.Q(ClipText(d, maxLen));
        if (d != t)
            outS += "/" + J.Q(ClipText(t, maxLen));
        return outS;
    }

    public Dict IdentityOf(Node n)
    {
        if (n is EditorProperty ep)
            return new Dict { ["property"] = ep.GetEditedProperty().ToString() };
        if (n is Window w)
            return w.Title != "" ? new Dict { ["title"] = w.Title } : new Dict();
        if (n.GetClass() == "EditorInspectorSection")
        {
            var first = FirstPropertyIn(n);
            if (first != "")
                return new Dict { ["first_property"] = first };
        }
        var t = TextOf(n);
        if (t != "" && t.Length <= MaxText && n is not LineEdit && n is not TextEdit)
            return new Dict { ["text"] = t };
        if (n is Control c && c.TooltipText != "" && c.TooltipText.Length <= MaxText)
            return new Dict { ["tooltip"] = c.TooltipText };
        return new Dict();
    }

    private string FirstPropertyIn(Node n)
    {
        var q = new Queue<Node>();
        q.Enqueue(n);
        while (q.Count > 0)
        {
            var cur = q.Dequeue();
            if (cur != n && cur is EditorProperty ep)
                return ep.GetEditedProperty().ToString();
            foreach (var ch in Children(cur))
                q.Enqueue(ch);
        }
        return "";
    }

    private List<string> PropertiesIn(Node n, int limit)
    {
        var outL = new List<string>();
        var q = new Queue<Node>();
        q.Enqueue(n);
        while (q.Count > 0 && outL.Count < limit)
        {
            var cur = q.Dequeue();
            if (cur != n && cur is EditorProperty ep)
            {
                outL.Add(ep.GetEditedProperty().ToString());
                continue;
            }
            foreach (var ch in Children(cur))
                q.Enqueue(ch);
        }
        return outL;
    }

    private bool MatchesIdentity(Node n, Dict q)
    {
        if (q.Count == 0)
            return true;
        var ident = IdentityOf(n);
        foreach (var kv in q)
        {
            var want = J.Str(kv.Value);
            if (kv.Key == "text")
            {
                var src = TextOf(n).Trim();
                if (src != want.Trim() && Shown(n, src).Trim() != want.Trim())
                    return false;
            }
            else if (J.Str(ident, kv.Key) != want)
            {
                // 允许 tooltip 作为 text 的替代
                if (!(n is Control c && kv.Key == "tooltip" && c.TooltipText == want))
                    return false;
            }
        }
        return true;
    }

    // ============================================================ 几何

    public static Rect2 GlobalRect(CanvasItem n, Rect2 local) => n.GetGlobalTransformWithCanvas() * local;

    /// <summary>控件在其窗口中真正可见的区域（考虑裁剪祖先与窗口边界）。</summary>
    public Rect2 VisibleRect(Control c, Rect2? localOpt = null)
    {
        var full = new Rect2(Vector2.Zero, c.Size);
        var local = localOpt is { } lr && lr.Size != Vector2.Zero ? lr : full;
        var r = GlobalRect(c, local);
        if (c.ClipContents && local != full)
            r = r.Intersection(GlobalRect(c, full));
        var p = c.GetParent();
        while (p != null && p is not Window)
        {
            if (p is Control pc && (pc.ClipContents || pc is ScrollContainer))
                r = r.Intersection(GlobalRect(pc, new Rect2(Vector2.Zero, pc.Size)));
            p = p.GetParent();
        }
        var w = c.GetWindow();
        if (w != null)
            r = r.Intersection(w.GetVisibleRect());
        return r;
    }

    // ============================================================ 子项

    public List<SubItem> Subitems(Node n, int maxItems = 200)
    {
        var outL = new List<SubItem>();
        switch (n)
        {
            case Tree t:
                TreeItems(t, t.GetRoot(), outL, maxItems);
                break;
            case TabBar tb:
                for (int i = 0; i < tb.TabCount; i++)
                {
                    if (tb.IsTabHidden(i))
                        continue;
                    var flags = new List<string>();
                    if (i == tb.CurrentTab)
                        flags.Add("current");
                    if (tb.IsTabDisabled(i))
                        flags.Add("disabled");
                    var title = tb.GetTabTitle(i);
                    if (title == "")
                        title = tb.GetTabTooltip(i);
                    outL.Add(new SubItem { Kind = "tab", Key = title != "" ? title : i, Text = title, Local = tb.GetTabRect(i), Flags = flags });
                }
                break;
            case ItemList il:
                var scroll = (float)il.GetVScrollBar().Value;
                for (int i = 0; i < Math.Min(il.ItemCount, maxItems); i++)
                {
                    var flags = new List<string>();
                    if (il.IsSelected(i))
                        flags.Add("selected");
                    if (il.IsItemDisabled(i))
                        flags.Add("disabled");
                    var r = il.GetItemRect(i);
                    r.Position = new Vector2(r.Position.X, r.Position.Y - scroll);
                    var t2 = il.GetItemText(i);
                    if (t2 == "")
                        t2 = il.GetItemTooltip(i);
                    outL.Add(new SubItem { Kind = "list", Key = t2 != "" ? t2 : i, Text = t2, Local = r, Flags = flags });
                }
                break;
            case PopupMenu pm:
                for (int i = 0; i < pm.ItemCount; i++)
                {
                    if (pm.IsItemSeparator(i) && pm.GetItemText(i) == "")
                        continue;
                    var flags = new List<string>();
                    if (pm.IsItemDisabled(i))
                        flags.Add("disabled");
                    if (pm.IsItemCheckable(i) || pm.IsItemRadioCheckable(i))
                        flags.Add(pm.IsItemChecked(i) ? "checked" : "unchecked");
                    if (pm.GetItemSubmenuNode(i) != null)
                        flags.Add("submenu");
                    if (pm.IsItemSeparator(i))
                        flags.Add("separator");
                    var extra = "";
                    var sc = pm.GetItemShortcut(i);
                    if (sc != null && sc.HasValidEvent())
                        extra = sc.GetAsText();
                    else if (pm.GetItemAccelerator(i) != Key.None)
                        extra = OS.GetKeycodeString(pm.GetItemAccelerator(i));
                    var t3 = pm.GetItemText(i);
                    outL.Add(new SubItem { Kind = "menu", Key = t3 != "" ? t3 : i, Text = t3, Local = PopupItemRect(pm, i), Flags = flags, Extra = extra });
                }
                break;
            case MenuBar mb:
                for (int i = 0; i < mb.GetMenuCount(); i++)
                {
                    if (mb.IsMenuHidden(i))
                        continue;
                    var flags = new List<string>();
                    if (mb.IsMenuDisabled(i))
                        flags.Add("disabled");
                    var t4 = mb.GetMenuTitle(i);
                    outL.Add(new SubItem { Kind = "bar", Key = t4 != "" ? t4 : i, Text = t4, Local = MenubarItemRect(mb, i), Flags = flags });
                }
                break;
        }
        return outL;
    }

    private void TreeItems(Tree t, TreeItem? item, List<SubItem> outL, int maxItems)
    {
        if (item == null || outL.Count >= maxItems || !item.Visible)
            return;
        bool shownItem = !(item == t.GetRoot() && t.HideRoot);
        if (shownItem)
        {
            var flags = new List<string>();
            if (item.IsSelected(0))
                flags.Add("selected");
            if (item.GetChildCount() > 0)
                flags.Add(item.Collapsed ? "collapsed" : "expanded");
            var extra = "";
            var tip = item.GetTooltipText(0);
            if (tip != "" && tip != item.GetText(0))
                extra = "tip=" + J.Q(ClipText(tip, 60));
            outL.Add(new SubItem { Kind = "tree", Key = TreeItemPath(t, item), Text = item.GetText(0), Local = t.GetItemAreaRect(item, 0), Flags = flags, Extra = extra });
        }
        if (item.Collapsed && shownItem)
            return;
        var ch = item.GetFirstChild();
        while (ch != null && outL.Count < maxItems)
        {
            TreeItems(t, ch, outL, maxItems);
            ch = ch.GetNext();
        }
    }

    public string TreeItemPath(Tree t, TreeItem item)
    {
        var parts = new List<string>();
        var it = item;
        while (it != null)
        {
            if (!(it == t.GetRoot() && t.HideRoot))
                parts.Insert(0, it.GetText(0));
            it = it.GetParent();
        }
        return string.Join("/", parts);
    }

    public TreeItem? FindTreeItem(Tree t, object? key)
    {
        var root = t.GetRoot();
        if (root == null)
            return null;
        if (J.IsIndex(key, out var idx))
        {
            var it = t.HideRoot ? root.GetFirstChild() : root;
            int i = 0;
            while (it != null)
            {
                if (i == idx)
                    return it;
                it = it.GetNextInTree();
                i++;
            }
            return null;
        }
        var parts = J.Str(key).Split('/', StringSplitOptions.RemoveEmptyEntries);
        var cur = root;
        int start = 0;
        if (!t.HideRoot)
        {
            if (parts.Length == 0 || root.GetText(0) != parts[0])
                return null;
            start = 1;
        }
        for (int i = start; i < parts.Length; i++)
        {
            TreeItem? found = null;
            var ch = cur.GetFirstChild();
            while (ch != null)
            {
                if (ch.GetText(0) == parts[i])
                {
                    found = ch;
                    break;
                }
                ch = ch.GetNext();
            }
            if (found == null)
                return null;
            cur = found;
        }
        return cur;
    }

    /// <summary>TreeItem 是否在树中可见（所有祖先都已展开）。</summary>
    public static bool TreeItemShown(Tree t, TreeItem item)
    {
        if (!item.Visible)
            return false;
        var p = item.GetParent();
        while (p != null)
        {
            if (p.Collapsed || !p.Visible)
                return false;
            p = p.GetParent();
        }
        return true;
    }

    public Control? PopupItemsControl(PopupMenu pm)
    {
        var q = new Queue<Node>();
        q.Enqueue(pm);
        while (q.Count > 0)
        {
            var cur = q.Dequeue();
            if (cur is ScrollContainer)
            {
                foreach (var ch in Children(cur))
                    if (ch is Control cc && ch is not ScrollBar)
                        return cc;
            }
            foreach (var ch in Children(cur))
                q.Enqueue(ch);
        }
        return null;
    }

    /// <summary>
    /// 与 PopupMenu::_get_item_height 保持一致（scene/gui/popup_menu.cpp）。
    /// 文本高度必须用 TextLine 实测：中文等回退字体会让行高和 font.get_height 不同，
    /// 每项差几像素，越往下的菜单项累计偏差越大（高亮框会“歪”到下一项）。
    /// </summary>
    private float PopupItemHeight(PopupMenu pm, int i)
    {
        bool sepItem = pm.IsItemSeparator(i);
        var font = pm.GetThemeFont(sepItem ? "font_separator" : "font");
        int fs = pm.GetThemeFontSize(sepItem ? "font_separator_size" : "font_size");
        float textH = 0;
        var txt = pm.GetItemText(i);
        if (txt != "" && font != null)
        {
            var tl = new TextLine();
            tl.AddString(Shown(pm, txt), font, fs);
            textH = Mathf.Floor(tl.GetSize().Y); // 引擎里是 int
        }
        if (textH == 0 && !sepItem)
        {
            var f2 = pm.GetThemeFont("font");
            textH = f2 != null ? Mathf.Floor(f2.GetHeight(pm.GetThemeFontSize("font_size"))) : 16;
        }
        float iconH = 0;
        var icon = pm.GetItemIcon(i);
        if (icon != null)
        {
            var isz = icon.GetSize();
            int maxW = pm.GetThemeConstant("icon_max_width");
            int itemMax = pm.GetItemIconMaxWidth(i);
            if (itemMax > 0 && (maxW <= 0 || itemMax < maxW))
                maxW = itemMax;
            if (maxW > 0 && isz.X > maxW)
                isz.Y = isz.Y * maxW / isz.X;
            iconH = Mathf.Floor(isz.Y);
        }
        if ((pm.IsItemCheckable(i) || pm.IsItemRadioCheckable(i)) && !sepItem)
        {
            foreach (var nm in new[] { "checked", "radio_checked" })
            {
                var chk = pm.GetThemeIcon(nm);
                if (chk != null)
                    iconH = Mathf.Max(iconH, chk.GetHeight());
            }
        }
        float sepH = 0;
        if (sepItem)
        {
            foreach (var sbName in new[] { "separator", "labeled_separator_left", "labeled_separator_right" })
            {
                var sb = pm.GetThemeStylebox(sbName);
                if (sb != null)
                    sepH = Mathf.Max(sepH, sb.GetMinimumSize().Y);
            }
        }
        return Mathf.Max(sepH, Mathf.Max(textH, iconH));
    }

    public Rect2 PopupItemRect(PopupMenu pm, int idx)
    {
        var ctrl = PopupItemsControl(pm);
        float width = ctrl != null ? ctrl.Size.X : pm.Size.X;
        float vsep = pm.GetThemeConstant("v_separation");
        float y = 0;
        bool first = true;
        for (int i = 0; i < pm.ItemCount; i++)
        {
            y += first ? Mathf.Floor(vsep / 2f) : vsep; // 引擎里是 int 除法
            first = false;
            var h = PopupItemHeight(pm, i);
            if (i == idx)
                return new Rect2(0, y - Mathf.Floor(vsep / 2f), width, h + vsep);
            y += h;
        }
        return new Rect2();
    }

    /// <summary>子项的局部矩形所属的 CanvasItem（PopupMenu 是条目控件，其它是控件本身）。</summary>
    public CanvasItem? SubitemCanvas(Node n) => n is PopupMenu pm ? PopupItemsControl(pm) : n as CanvasItem;

    public Rect2 MenubarItemRect(MenuBar mb, int idx)
    {
        var style = mb.GetThemeStylebox("normal");
        var font = mb.GetThemeFont("font");
        int fs = mb.GetThemeFontSize("font_size");
        int hsep = mb.GetThemeConstant("h_separation");
        var minSize = style != null ? style.GetMinimumSize() : Vector2.Zero;
        float offset = 0;
        var size = Vector2.Zero;
        for (int i = 0; i <= idx; i++)
        {
            if (mb.IsMenuHidden(i))
                continue;
            // 菜单栏绘制的是翻译后的标题，宽度要按显示文字算
            float w = font != null ? font.GetStringSize(mb.Atr(mb.GetMenuTitle(i)), HorizontalAlignment.Left, -1, fs).X : 40;
            size = new Vector2(w, font != null ? font.GetHeight(fs) : 16) + minSize;
            if (i < idx)
                offset += size.X + hsep;
        }
        return new Rect2(new Vector2(offset, 0), new Vector2(size.X, Mathf.Max(size.Y, mb.Size.Y)));
    }

    private static bool SubitemMatches(SubItem it, object? key)
    {
        if (J.IsIndex(key, out _))
            return false;
        var k = J.Str(key);
        return J.Str(it.Key) == k || it.Text == k || it.Shown == k;
    }

    /// <summary>在控件上按 key 找子项；key 可以是文本/路径或序号。</summary>
    public SubItem? FindSubitem(Node n, string kind, object? key)
    {
        if (kind == "tab" && n is TabContainer tc)
            n = tc.GetTabBar();
        if (kind == "tree")
        {
            if (n is not Tree t)
                return null;
            var item = FindTreeItem(t, key);
            if (item == null)
                return null;
            var flags = new List<string>();
            if (!TreeItemShown(t, item))
                flags.Add("hidden_by_collapsed_parent");
            return new SubItem { Kind = "tree", Key = TreeItemPath(t, item), Text = item.GetText(0), Local = t.GetItemAreaRect(item, 0), Flags = flags, Node = t, Item = item };
        }
        var items = Subitems(n, 100000);
        foreach (var it in items)
            it.Shown = Shown(n, it.Text);
        if (J.IsIndex(key, out var idx))
        {
            // 序号按原始索引解析，而不是过滤后的列表
            foreach (var it in items)
            {
                if (it.Kind == kind && RawIndex(n, kind, it.Key) == idx)
                {
                    it.Node = n;
                    return it;
                }
            }
            return null;
        }
        foreach (var it in items)
        {
            if (it.Kind == kind && SubitemMatches(it, key))
            {
                it.Node = n;
                return it;
            }
        }
        return null;
    }

    private int RawIndex(Node n, string kind, object? key)
    {
        if (J.IsIndex(key, out var i0))
            return i0;
        var k = J.Str(key);
        switch (kind)
        {
            case "tab" when n is TabBar tb:
                for (int i = 0; i < tb.TabCount; i++)
                    if (tb.GetTabTitle(i) == k || tb.GetTabTooltip(i) == k)
                        return i;
                break;
            case "list" when n is ItemList il:
                for (int i = 0; i < il.ItemCount; i++)
                    if (il.GetItemText(i) == k || il.GetItemTooltip(i) == k)
                        return i;
                break;
            case "menu" when n is PopupMenu pm:
                for (int i = 0; i < pm.ItemCount; i++)
                    if (pm.GetItemText(i) == k)
                        return i;
                break;
            case "bar" when n is MenuBar mb:
                for (int i = 0; i < mb.GetMenuCount(); i++)
                    if (mb.GetMenuTitle(i) == k)
                        return i;
                break;
        }
        return -1;
    }

    public int SubitemIndex(Node n, string kind, object? key) => J.IsIndex(key, out var i) ? i : RawIndex(n, kind, key);

    // ============================================================ locator

    public string LocatorOf(Node node, string kind = "", object? key = null)
    {
        var parts = new List<string>();
        Node? n = node;
        var root = TreeRoot();
        while (n != null && n != root)
        {
            parts.Insert(0, Segment(n));
            n = n.GetParent();
        }
        var loc = "/" + string.Join("/", parts);
        if (kind != "")
            loc += $"::{kind}={J.Stringify(key)}";
        return loc;
    }

    private string Segment(Node n)
    {
        var nm = n.Name.ToString();
        if (!nm.StartsWith("@"))
            return nm;
        var cls = n.GetClass();
        var q = IdentityOf(n);
        var seg = "@" + cls;
        if (q.Count > 0)
            seg += J.Stringify(q);
        int idx = 0;
        var parent = n.GetParent();
        if (parent != null)
        {
            foreach (var sib in Children(parent))
            {
                if (sib == n)
                    break;
                if (sib.Name.ToString().StartsWith("@") && sib.GetClass() == cls && MatchesIdentity(sib, q))
                    idx++;
            }
        }
        if (idx > 0)
            seg += $":{idx}";
        return seg;
    }

    /// <summary>在顶层（不在引号/花括号内）按分隔符切分。</summary>
    public static List<string> SplitTop(string s, string sep)
    {
        var outL = new List<string>();
        int depth = 0;
        bool inStr = false, esc = false;
        int start = 0, i = 0;
        while (i < s.Length)
        {
            var ch = s[i];
            if (inStr)
            {
                if (esc) esc = false;
                else if (ch == '\\') esc = true;
                else if (ch == '"') inStr = false;
            }
            else
            {
                if (ch == '"') inStr = true;
                else if (ch == '{' || ch == '[') depth++;
                else if (ch == '}' || ch == ']') depth--;
                else if (depth == 0 && string.CompareOrdinal(s, i, sep, 0, sep.Length) == 0)
                {
                    outL.Add(s.Substring(start, i - start));
                    i += sep.Length;
                    start = i;
                    continue;
                }
            }
            i++;
        }
        outL.Add(s.Substring(start));
        return outL;
    }

    private sealed class SegSpec
    {
        public bool Deep;
        public string? Name;
        public string Class = "";
        public Dict Q = new();
        public int Nth = -1;
    }

    private static SegSpec ParseSegment(string seg)
    {
        if (seg == "**" || seg == "")
            return new SegSpec { Deep = true };
        if (!seg.StartsWith("@"))
            return new SegSpec { Name = seg };
        var body = seg[1..];
        int nth = -1;
        var parts = SplitTop(body, ":");
        if (parts.Count > 1 && int.TryParse(parts[^1], out var n))
        {
            nth = n;
            body = string.Join(":", parts.Take(parts.Count - 1));
        }
        var cls = body;
        var q = new Dict();
        var brace = body.IndexOf('{');
        if (brace >= 0)
        {
            cls = body[..brace];
            if (J.Parse(body[brace..]) is Dict d)
                q = d;
        }
        return new SegSpec { Class = cls, Q = q, Nth = nth };
    }

    private bool SpecMatches(Node n, SegSpec spec)
    {
        if (spec.Name != null)
            return n.Name.ToString() == spec.Name;
        if (spec.Class != "" && n.GetClass() != spec.Class && !n.IsClass(spec.Class))
            return false;
        return MatchesIdentity(n, spec.Q);
    }

    private Node? FindIn(Node parent, SegSpec spec, bool deep)
    {
        int want = spec.Nth;
        int count = 0;
        if (!deep)
        {
            if (spec.Name != null)
            {
                var direct = parent.GetNodeOrNull(new NodePath(spec.Name));
                if (direct != null)
                    return direct;
            }
            else
            {
                foreach (var ch in Children(parent))
                {
                    if (SpecMatches(ch, spec))
                    {
                        if (want <= 0 || count == want)
                            return ch;
                        count++;
                    }
                }
            }
        }
        // 深度搜索（显式 ** 或直接子节点找不到时的容错，适应中间多一层包装的情况）。
        // 未指定序号时优先返回可见的匹配，避免命中隐藏对话框里的同名节点。
        int maxDepth = deep ? 64 : 4;
        var queue = new Queue<(Node N, int D)>();
        queue.Enqueue((parent, 0));
        count = 0;
        Node? firstHidden = null;
        while (queue.Count > 0)
        {
            var (cur, d) = queue.Dequeue();
            if (d >= maxDepth)
                continue;
            foreach (var ch in Children(cur))
            {
                if (IsOwn(ch))
                    continue;
                if (SpecMatches(ch, spec))
                {
                    if (want > 0)
                    {
                        if (count == want)
                            return ch;
                        count++;
                    }
                    else if (NodeVisible(ch))
                        return ch;
                    else
                        firstHidden ??= ch;
                }
                queue.Enqueue((ch, d + 1));
            }
        }
        return firstHidden;
    }

    private static bool NodeVisible(Node n) => n switch
    {
        Control c => c.IsVisibleInTree() && c.GetWindow().Visible,
        Window w => w.Visible,
        _ => true,
    };

    public (BaseRef? Base, string Error) ResolveLocator(string loc)
    {
        var pieces = SplitTop(loc, "::");
        var main = pieces[0];
        var kind = "";
        object? key = null;
        if (pieces.Count > 1)
        {
            var sub = pieces[1];
            var eq = sub.IndexOf('=');
            if (eq < 0)
                return (null, "子项后缀格式应为 ::kind=值");
            kind = sub[..eq].Trim();
            var raw = sub[(eq + 1)..].Trim();
            var parsed = J.Parse(raw);
            key = parsed ?? raw;
            if (J.IsIndex(key, out var ix) && key is not string)
                key = ix;
        }
        var segs = SplitTop(main.TrimStart('/'), "/");
        Node cur = TreeRoot();
        bool deep = false;
        foreach (var s in segs)
        {
            var spec = ParseSegment(s);
            if (spec.Deep)
            {
                deep = true;
                continue;
            }
            var found = FindIn(cur, spec, deep);
            if (found == null)
                return (null, $"定位失败：在 {LocatorOf(cur)} 下找不到 '{s}'");
            cur = found;
            deep = false;
        }
        return (new BaseRef(cur, kind, key), "");
    }

    // ============================================================ 目标解析

    /// <summary>target 可以是 ref（e12 / e12.40）、locator（以 / 开头）、或查询对象（见 Find()）。</summary>
    public Geo Resolve(object? target)
    {
        if (target is Dict qd)
        {
            var q = J.CloneDict(qd);
            // 引导/操作的目标可能暂时被滚出视野或藏在折叠处：默认连隐藏元素一起找，但可见的优先。
            if (!q.ContainsKey("visible_only"))
                q["visible_only"] = false;
            q["limit"] = 1L;
            var hits = Find(q);
            if (hits.Count == 0)
                return Geo.Fail($"没有匹配 {J.Stringify(target)} 的可见目标");
            return Geometry(hits[0].Base);
        }
        var s = J.Str(target).Trim();
        if (s.StartsWith("/"))
        {
            var (b, err) = ResolveLocator(s);
            return b is { } bb ? Geometry(bb) : Geo.Fail(err);
        }
        if (_refs.TryGetValue(s, out var e))
        {
            var obj = GodotObject.InstanceFromId(e.Id);
            if (obj is not Node node || !node.IsInsideTree())
                return Geo.Fail($"ref {s} 对应的节点已不存在，请重新 ui_snapshot / ui_find");
            return Geometry(new BaseRef(node, e.Kind, e.Key));
        }
        if (s.StartsWith("e") && s.Length > 1 && s[1..].Replace(".", "").All(char.IsDigit))
            return Geo.Fail($"未知 ref {s}（ref 只在本次编辑器会话内有效），请重新 ui_snapshot");
        return Resolve(new Dict { ["text"] = s });
    }

    /// <summary>根据 {node, kind, key} 计算当前几何信息。</summary>
    public Geo Geometry(BaseRef b)
    {
        var node = b.Node;
        var res = new Geo { Node = node, Kind = b.Kind, Key = b.Key };
        if (node is Window w)
        {
            res.Window = w;
            res.Ref = RefFor(w);
            res.Text = w.Title;
            if (b.Kind != "" && node is PopupMenu)
                return SubitemGeometry(res, node, b.Kind, b.Key);
            res.Rect = w.GetVisibleRect();
            res.Vis = res.Rect;
            res.Visible = w.Visible;
            return res;
        }
        if (node is not Control c)
            return Geo.Fail($"目标不是 Control/Window：{node.GetClass()}");
        res.Window = c.GetWindow();
        if (b.Kind != "")
            return SubitemGeometry(res, node, b.Kind, b.Key);
        res.Ref = RefFor(c);
        res.Text = Shown(c, TextOf(c));
        res.Rect = GlobalRect(c, new Rect2(Vector2.Zero, c.Size));
        var vis = VisibleRect(c);
        res.Vis = vis;
        var hot = PrimaryControl(c);
        if (hot != c)
        {
            var hv = VisibleRect(hot);
            if (hv.Size.X >= 1 && hv.Size.Y >= 1)
                res.Hot = hv;
        }
        res.Visible = c.IsVisibleInTree() && vis.Size.X >= 1 && vis.Size.Y >= 1 && res.Window != null && res.Window.Visible;
        return res;
    }

    private Geo SubitemGeometry(Geo res, Node node, string kind, object? key)
    {
        var it = FindSubitem(node, kind, key);
        if (it == null)
            return new Geo { Ok = false, Node = node, Kind = kind, Key = key, Window = res.Window, Error = $"在 {node.GetClass()} 中找不到子项 {kind}={J.Stringify(key)}" };
        var owner = it.Node!;
        var canvas = SubitemCanvas(owner);
        if (canvas == null)
            return new Geo { Ok = false, Node = node, Kind = kind, Key = key, Window = res.Window, Error = "无法计算子项坐标" };
        res.Node = owner;
        res.Kind = kind;
        res.Key = it.Key;
        res.Ref = RefFor(owner, kind, it.Key);
        res.Text = Shown(owner, it.Text);
        res.Rect = GlobalRect(canvas, it.Local);
        res.Flags = it.Flags;
        if (canvas is Control cc)
        {
            var vis = VisibleRect(cc, it.Local);
            res.Vis = vis;
            res.Visible = cc.IsVisibleInTree() && vis.Size.X >= 1 && vis.Size.Y >= 1 && !it.Flags.Contains("hidden_by_collapsed_parent");
        }
        else
        {
            res.Vis = res.Rect;
            res.Visible = true;
        }
        if (owner is Window ow)
        {
            res.Window = ow;
            res.Visible = res.Visible && ow.Visible;
        }
        else
        {
            res.Window = ((Control)owner).GetWindow();
        }
        if (res.Window != null && !res.Window.Visible)
            res.Visible = false;
        res.Item = it.Item;
        return res;
    }

    /// <summary>
    /// 复合控件（检查器属性行等）里真正用来操作的子控件：最大的可见按钮/输入框/数值框。
    /// AI 代点时点它，避免点到行中间的“恢复默认”之类的小按钮。
    /// </summary>
    public Control PrimaryControl(Control c)
    {
        if (!(c is EditorProperty || c.GetClass() == "EditorInspectorSection" || c is Container))
            return c;
        if (c.GetClass() == "EditorInspectorSection")
            return c;
        Control? best = null;
        float bestArea = 0;
        var stack = new Stack<Node>();
        stack.Push(c);
        while (stack.Count > 0)
        {
            var n = stack.Pop();
            foreach (var ch in Children(n))
            {
                if (ch is not Control cc || !cc.IsVisibleInTree())
                    continue;
                if (cc is BaseButton || cc is LineEdit || (cc is Range && cc is not ScrollBar) || cc is TextEdit)
                {
                    var area = cc.Size.X * cc.Size.Y;
                    if (area > bestArea)
                    {
                        bestArea = area;
                        best = cc;
                    }
                }
                else
                {
                    stack.Push(cc);
                }
            }
        }
        return best ?? c;
    }

    // ============================================================ 查找

    /// <summary>
    /// 查询字段：text（默认子串、忽略大小写）、exact、class、name、tooltip、property、kind(control|tree|tab|list|menu|bar|any)、
    /// under（限定在某个目标之下）、visible_only（默认 true）、limit（默认 20）。
    /// </summary>
    public List<Geo> Find(Dict q)
    {
        int limit = (int)J.Int(q, "limit", 20);
        bool visibleOnly = J.Bool(q, "visible_only", true);
        bool exact = J.Bool(q, "exact", false);
        var text = J.Str(q, "text");
        var cls = J.Str(q, "class");
        var nm = J.Str(q, "name");
        var tip = J.Str(q, "tooltip");
        var prop = J.Str(q, "property");
        var wantKind = J.Str(q, "kind", "any");
        Node start = TreeRoot();
        if (q.ContainsKey("under") && q["under"] != null)
        {
            var u = Resolve(q["under"]);
            if (!u.Ok)
                return new List<Geo>();
            start = u.Node!;
        }
        var root = TreeRoot();
        var hits = new List<Geo>();
        var stack = new Stack<Node>();
        stack.Push(start);
        while (stack.Count > 0)
        {
            var n = stack.Pop();
            if (IsOwn(n) || n is SubViewport)
                continue;
            if (n is Window wn && !wn.Visible && n != root)
                continue;
            if (n is Control cn && visibleOnly && !cn.IsVisibleInTree())
                continue;
            // 控件本身
            if ((wantKind == "any" || wantKind == "control") && (n is Control || (n is Window && n != root)))
            {
                bool ok = true;
                if (cls != "" && !n.IsClass(cls))
                    ok = false;
                if (ok && nm != "" && n.Name.ToString() != nm)
                    ok = false;
                if (ok && prop != "" && !(n is EditorProperty ep && ep.GetEditedProperty().ToString() == prop))
                    ok = false;
                if (ok && tip != "" && !(n is Control ct && TextHit(ct.TooltipText, tip, exact)))
                    ok = false;
                if (ok && text != "")
                {
                    var t = TextOf(n);
                    var tt = n is Control c2 ? c2.TooltipText : "";
                    if (!(TextHit(t, text, exact) || TextHit(Shown(n, t), text, exact) || TextHit(tt, text, exact) || TextHit(Shown(n, tt), text, exact)))
                        ok = false;
                }
                if (ok && (text != "" || cls != "" || nm != "" || prop != "" || tip != ""))
                {
                    var g = Geometry(new BaseRef(n, "", null));
                    if (g.Ok && (!visibleOnly || g.Visible))
                    {
                        g.Score = Math.Max(Score(TextOf(n), text), Score(Shown(n, TextOf(n)), text));
                        hits.Add(g);
                    }
                }
            }
            // 子项
            if (wantKind != "control" && text != "" && (n is Tree || n is TabBar || n is ItemList || n is PopupMenu || n is MenuBar))
            {
                int treeLimit = n is Tree ? 5000 : 100000;
                foreach (var it in Subitems(n, treeLimit))
                {
                    if (wantKind != "any" && it.Kind != wantKind)
                        continue;
                    var itText = it.Text;
                    if (TextHit(itText, text, exact) || TextHit(Shown(n, itText), text, exact) || (it.Kind == "tree" && TextHit(J.Str(it.Key), text, exact)))
                    {
                        var g = Geometry(new BaseRef(n, it.Kind, it.Key));
                        if (g.Ok && (!visibleOnly || g.Visible))
                        {
                            g.Score = Math.Max(Score(itText, text), Score(Shown(n, itText), text)) + 1;
                            hits.Add(g);
                        }
                    }
                }
            }
            var children = n.GetChildren(true);
            for (int i = children.Count - 1; i >= 0; i--)
                stack.Push(children[i]);
        }
        foreach (var h in hits)
            if (h.Visible)
                h.Score += 100;
        // 稳定排序（与 GDScript 的 sort_custom 一样按分数降序）
        return hits.Select((h, i) => (h, i)).OrderByDescending(x => x.h.Score).ThenBy(x => x.i).Select(x => x.h).Take(limit).ToList();
    }

    private static bool TextHit(string have, string want, bool exact)
    {
        if (have == "")
            return false;
        if (exact)
            return have.Trim() == want.Trim();
        return have.Contains(want, StringComparison.OrdinalIgnoreCase);
    }

    private static int Score(string have, string want)
    {
        if (want == "")
            return 0;
        if (have == want)
            return 10;
        if (string.Equals(have, want, StringComparison.OrdinalIgnoreCase))
            return 8;
        if (have.StartsWith(want, StringComparison.OrdinalIgnoreCase))
            return 5;
        return 1;
    }

    /// <summary>某窗口中位于 pos（画布坐标）处最深的可见控件。</summary>
    public Control? ControlAt(Window w, Vector2 pos)
    {
        Control? best = null;
        var stack = new Stack<Node>();
        stack.Push(w);
        while (stack.Count > 0)
        {
            var n = stack.Pop();
            if (IsOwn(n) || n is SubViewport)
                continue;
            if (n is Window && n != w)
                continue;
            if (n is Control c)
            {
                if (!c.IsVisibleInTree())
                    continue;
                if (c.MouseFilter != Control.MouseFilterEnum.Ignore && VisibleRect(c).HasPoint(pos))
                    best = c;
            }
            foreach (var ch in Children(n))
                stack.Push(ch);
        }
        return best;
    }

    // ============================================================ 快照

    private sealed class SnapCtx
    {
        public List<string> Lines = new();
        public int Count;
        public int Max;
        public int MaxItems;
        public bool VisibleOnly;
        public bool Compact;
        public int MaxDepth;
        public bool Truncated;
    }

    public string Snapshot(Dict opts, Node? rootNode)
    {
        Prune();
        var start = rootNode ?? TreeRoot();
        var ctx = new SnapCtx
        {
            Max = (int)J.Int(opts, "max_nodes", 600),
            MaxItems = (int)J.Int(opts, "max_items", 40),
            VisibleOnly = J.Bool(opts, "visible_only", true),
            Compact = J.Bool(opts, "compact", true),
            MaxDepth = (int)J.Int(opts, "depth", 200),
        };
        Walk(start, 0, 0, ctx);
        if (ctx.Truncated)
            ctx.Lines.Add("…（已截断：用 root 缩小范围，或调大 max_nodes）");
        return string.Join("\n", ctx.Lines);
    }

    private static bool Interesting(Control c)
    {
        if (c is BaseButton || c is LineEdit || c is TextEdit || c is Range || c is Tree || c is ItemList
            || c is TabBar || c is MenuBar || c is EditorProperty || c is Label || c is RichTextLabel
            || c is GraphEdit || c is SubViewportContainer || c is ColorRect || c is TextureRect)
        {
            if (c is ScrollBar || c is TextureRect || c is ColorRect)
                return c.TooltipText != "";
            return true;
        }
        if (c.GetClass() == "EditorInspectorSection")
            return true;
        if (c.TooltipText != "" || c.AccessibilityName != "")
            return true;
        return !c.Name.ToString().StartsWith("@");
    }

    private static string Indent(int depth) => new(' ', depth * 2);

    private void Walk(Node n, int depth, int level, SnapCtx ctx)
    {
        if (IsOwn(n) || level > ctx.MaxDepth)
            return;
        if (ctx.Count >= ctx.Max)
        {
            ctx.Truncated = true;
            return;
        }
        if (n is SubViewport)
            return;
        int childDepth = depth;
        if (n is Window w)
        {
            if (!w.Visible && ctx.VisibleOnly && w != TreeRoot())
                return;
            ctx.Lines.Add(Indent(depth) + WindowLine(w));
            ctx.Count++;
            childDepth++;
            if (w is PopupMenu)
                AppendSubitems(w, childDepth, ctx);
        }
        else if (n is Control c)
        {
            if (ctx.VisibleOnly && !c.IsVisibleInTree())
                return;
            if (!ctx.Compact || Interesting(c))
            {
                var vis = VisibleRect(c);
                bool offscreen = vis.Size.X < 1 || vis.Size.Y < 1;
                if (!(offscreen && ctx.VisibleOnly && ctx.Compact))
                {
                    ctx.Lines.Add(Indent(depth) + ControlLine(c, offscreen));
                    ctx.Count++;
                    childDepth++;
                    AppendSubitems(c, childDepth, ctx);
                }
            }
        }
        foreach (var ch in Children(n))
            Walk(ch, childDepth, level + 1, ctx);
    }

    private void AppendSubitems(Node n, int depth, SnapCtx ctx)
    {
        if (!(n is Tree || n is TabBar || n is ItemList || n is PopupMenu || n is MenuBar))
            return;
        var items = Subitems(n, ctx.MaxItems + 1);
        var canvas = SubitemCanvas(n);
        for (int i = 0; i < Math.Min(items.Count, ctx.MaxItems); i++)
        {
            var it = items[i];
            var parts = new List<string> { $"[{RefFor(n, it.Kind, it.Key)}]", it.Kind, LabelOf(n, it.Text) };
            if (it.Kind == "tree" && J.Str(it.Key) != it.Text)
                parts.Add("path=" + J.Q(J.Str(it.Key)));
            if (it.Extra != "")
                parts.Add(it.Extra);
            var flags = it.Flags;
            bool visOk = true;
            if (canvas is Control cc)
            {
                var vis = VisibleRect(cc, it.Local);
                visOk = vis.Size.X >= 1 && vis.Size.Y >= 1;
            }
            if (!visOk)
                flags.Add("offscreen");
            if (flags.Count > 0)
                parts.Add("{" + string.Join(",", flags) + "}");
            if (canvas != null)
                parts.Add(RectStr(GlobalRect(canvas, it.Local)));
            ctx.Lines.Add(Indent(depth) + string.Join(" ", parts));
            ctx.Count++;
        }
        if (items.Count > ctx.MaxItems)
            ctx.Lines.Add(Indent(depth) + "…（还有更多子项：用 ui_find 按文本查找，或调大 max_items）");
    }

    public static string RectStr(Rect2 r) =>
        $"@{Mathf.RoundToInt(r.Position.X)},{Mathf.RoundToInt(r.Position.Y)} {Mathf.RoundToInt(r.Size.X)}x{Mathf.RoundToInt(r.Size.Y)}";

    private string WindowLine(Window w)
    {
        var parts = new List<string> { $"[{RefFor(w)}]", w.GetClass() };
        if (!w.Name.ToString().StartsWith("@"))
            parts.Add("#" + w.Name);
        if (w.Title != "")
            parts.Add(LabelOf(w, w.Title));
        var flags = new List<string>();
        if (w == TreeRoot())
            flags.Add("main");
        if (w.HasFocus())
            flags.Add("focused");
        if (w.Exclusive)
            flags.Add("modal");
        if (flags.Count > 0)
            parts.Add("{" + string.Join(",", flags) + "}");
        parts.Add($"screen@{w.Position.X},{w.Position.Y} {w.Size.X}x{w.Size.Y}");
        return string.Join(" ", parts);
    }

    private string ControlLine(Control c, bool offscreen)
    {
        var parts = new List<string> { $"[{RefFor(c)}]", c.GetClass() };
        var nm = c.Name.ToString();
        if (!nm.StartsWith("@"))
            parts.Add("#" + nm);
        var t = TextOf(c);
        if (t != "")
            parts.Add(LabelOf(c, t));
        if (c is LineEdit le && le.PlaceholderText != "" && t == "")
            parts.Add("placeholder=" + J.Q(ClipText(Shown(c, le.PlaceholderText), 40)));
        if (c.TooltipText != "" && c.TooltipText != t)
            parts.Add("tip=" + J.Q(ClipText(Shown(c, c.TooltipText), 60)));
        if (c is EditorProperty ep)
        {
            var prop = ep.GetEditedProperty().ToString();
            parts.Add("prop=" + prop);
            var obj = ep.GetEditedObject();
            if (obj != null && prop != "")
                parts.Add("value=" + J.Q(ClipText(obj.Get(prop).ToString(), 50)));
        }
        else if (c.GetClass() == "EditorInspectorSection")
        {
            var props = PropertiesIn(c, 3);
            if (props.Count > 0)
                parts.Add("props=" + string.Join(",", props));
        }
        else if (c is Range rg && c is not ScrollBar)
        {
            parts.Add("value=" + J.Stringify(rg.Value));
        }
        var flags = new List<string>();
        if (c is BaseButton b)
        {
            if (b.Disabled)
                flags.Add("disabled");
            if (b.ToggleMode)
                flags.Add(b.ButtonPressed ? "on" : "off");
        }
        if (c is LineEdit le2 && !le2.Editable)
            flags.Add("readonly");
        if (c.GetClass() == "EditorInspectorSection")
        {
            var vb = c.Call("get_vbox").AsGodotObject();
            if (vb is Control vbc)
                flags.Add(vbc.Visible ? "expanded" : "folded");
        }
        if (c.HasFocus())
            flags.Add("focused");
        if (offscreen)
            flags.Add("offscreen");
        if (flags.Count > 0)
            parts.Add("{" + string.Join(",", flags) + "}");
        parts.Add(RectStr(GlobalRect(c, new Rect2(Vector2.Zero, c.Size))));
        return string.Join(" ", parts);
    }

    // ============================================================ 描述

    /// <summary>目标的完整信息，含 locator 与“为什么看不见”的提示。</summary>
    public Dict Describe(Geo t)
    {
        var node = t.Node!;
        var outD = new Dict
        {
            ["ref"] = t.Ref,
            ["class"] = node.GetClass(),
            ["name"] = node.Name.ToString(),
            ["text"] = t.Text,
            ["kind"] = t.Kind,
            ["locator"] = LocatorOf(node, t.Kind, t.Key),
            ["rect"] = RectDict(t.Rect),
            ["visible_rect"] = RectDict(t.Vis),
            ["visible"] = t.Visible,
        };
        var w = t.Window;
        if (w != null)
            outD["window"] = new Dict { ["ref"] = RefFor(w), ["class"] = w.GetClass(), ["title"] = w.Title, ["main"] = w == TreeRoot() };
        if (node is Control c && t.Kind == "")
            outD["tooltip"] = c.TooltipText;
        if (node is EditorProperty ep)
            outD["property"] = ep.GetEditedProperty().ToString();
        outD["hidden_because"] = HiddenReasons(t);
        return outD;
    }

    public static Dict RectDict(Rect2 r) => new()
    {
        ["x"] = Mathf.RoundToInt(r.Position.X),
        ["y"] = Mathf.RoundToInt(r.Position.Y),
        ["w"] = Mathf.RoundToInt(r.Size.X),
        ["h"] = Mathf.RoundToInt(r.Size.Y),
    };

    /// <summary>列出让目标不可见的祖先（未选中的标签页、折叠的检查器分组、折叠的树节点、滚动出视野、隐藏窗口）。</summary>
    public List<Dict> HiddenReasons(Geo t)
    {
        var reasons = new List<Dict>();
        var node = t.Node;
        if (node == null)
            return reasons;
        if (t.Item != null && node is Tree tree)
        {
            var p = t.Item.GetParent();
            while (p != null)
            {
                if (p.Collapsed)
                    reasons.Add(new Dict { ["type"] = "collapsed_tree_item", ["target"] = RefFor(node, "tree", TreeItemPath(tree, p)), ["text"] = p.GetText(0) });
                p = p.GetParent();
            }
        }
        Node child = node;
        var cur = node.GetParent();
        while (cur != null)
        {
            if (cur is Window cw)
            {
                if (!cw.Visible)
                    reasons.Add(new Dict { ["type"] = "window_hidden", ["target"] = RefFor(cw), ["text"] = cw.Title });
                break;
            }
            if (cur is TabContainer tc && child is Control cc)
            {
                var idx = tc.GetTabIdxFromControl(cc);
                if (idx >= 0 && idx != tc.CurrentTab)
                    reasons.Add(new Dict { ["type"] = "inactive_tab", ["target"] = RefFor(tc.GetTabBar(), "tab", tc.GetTabTitle(idx)), ["text"] = tc.GetTabTitle(idx) });
            }
            if (cur.GetClass() == "EditorInspectorSection")
            {
                var vb = cur.Call("get_vbox").AsGodotObject();
                if (vb is Control vbc && !vbc.Visible)
                    reasons.Add(new Dict { ["type"] = "folded_section", ["target"] = RefFor(cur), ["text"] = ((Control)cur).TooltipText });
            }
            if (cur is Control hc && !hc.Visible)
                reasons.Add(new Dict { ["type"] = "hidden_ancestor", ["target"] = RefFor(cur), ["class"] = cur.GetClass() });
            child = cur;
            cur = cur.GetParent();
        }
        if (reasons.Count == 0 && !t.Visible)
            reasons.Add(new Dict { ["type"] = "scrolled_out_or_clipped" });
        return reasons;
    }
}
#endif
