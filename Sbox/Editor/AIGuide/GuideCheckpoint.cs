using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Reflection;
using System.Text.Json.Nodes;
using System.Threading.Tasks;
using Editor;
using Sandbox;

namespace AIGuide.Sbox;

/// <summary>File transactions and editor undo checkpoints are independent from tutorial recording.</summary>
internal sealed class GuideCheckpoint
{
    private readonly string root;
    private readonly string backup;
    private readonly string blobs;
    private readonly Dictionary<string, string> hashes = new(StringComparer.OrdinalIgnoreCase);
    private readonly Dictionary<SceneEditorSession, int> undo = new();
    private readonly Dictionary<SceneEditorSession, string> selections = new();
    private readonly Dictionary<SceneEditorSession, bool> dirty = new();
    private readonly Dictionary<SceneEditorSession, JsonObject> sceneSnapshots = new();
    private readonly Dictionary<SceneEditorSession, string> scenePaths = new();
    private readonly Dictionary<SceneEditorSession, string> sceneNames = new();
    private readonly SceneEditorSession activeSession;
    private readonly List<string> skipped = new();
    private GuideHammerCheckpoint hammer;
    private readonly string[] scenes;
    private readonly string active;
    private readonly HashSet<string> windows;
    private bool restored;
    internal string Title { get; }
    internal static async Task<GuideCheckpoint> Capture(string root,string title)
    {
        var checkpoint=await GuideHost.Main(()=>new GuideCheckpoint(root,title));
        try{await Task.Run(checkpoint.BackupFiles);return checkpoint;}
        catch{await GuideHost.Main(()=>{checkpoint.Release();return true;});throw;}
    }
    internal void Release()=>hammer?.Release();
    internal GuideCheckpoint(string root, string title)
    {
        this.root = root; Title = title;
        backup = Path.Combine(root, "Art", "build", "aiguide-checkpoints", Guid.NewGuid().ToString("N"));
        blobs=Path.Combine(root,"Art","build","aiguide-checkpoint-blobs");Directory.CreateDirectory(blobs);
        Directory.CreateDirectory(backup);
        foreach (var s in SceneEditorSession.All){undo[s]=s.UndoSystem.Back.Count;selections[s]=s.SerializeSelection();dirty[s]=s.HasUnsavedChanges;sceneSnapshots[s]=s.Scene.Serialize();scenePaths[s]=s.Scene.Source?.ResourcePath;sceneNames[s]=s.Scene.Name;}
        activeSession=SceneEditorSession.Active;
        scenes = SceneEditorSession.All.Select(s => s.Scene?.Source?.ResourcePath).Where(p => !string.IsNullOrEmpty(p)).ToArray();
        active = SceneEditorSession.Active?.Scene?.Source?.ResourcePath;
        windows = GuideNative.Call("state").AsObject().A("windows").Select(w => w["ref"].GetValue<string>()).ToHashSet();
        hammer=GuideHammerCheckpoint.Capture();
    }
    private void BackupFiles()
    {
        long bytes=0;
        foreach (var path in Files(root))
        {
            var rel = Path.GetRelativePath(root, path);
            var info = new FileInfo(path);
            if (info.Length > 64 * 1024 * 1024 || bytes+info.Length>512L*1024*1024) {skipped.Add(rel);continue;}
            bytes+=info.Length;
            var hash=Convert.ToHexString(System.Security.Cryptography.SHA256.HashData(File.ReadAllBytes(path)));
            var dest=Path.Combine(blobs,hash);if(!File.Exists(dest)){try{File.Copy(path,dest);}catch(IOException){if(!File.Exists(dest))throw;}}
            hashes[rel]=hash;
        }
        File.WriteAllText(Path.Combine(backup,"manifest.json"),GJson.Node(new{root,Title,hashes,skipped,blob_store=blobs}).ToJsonString(GJson.Options));
    }
    private static IEnumerable<string> Files(string root)
    {
        var pending = new Stack<string>(); pending.Push(root);
        var ignore = new HashSet<string>(StringComparer.OrdinalIgnoreCase) { ".betterlore", ".ai_guide", ".ai_guide_bridge", ".sbox", ".source2", ".vs", "obj", "bin", "node_modules", ".git", "Transients", ".intermediate" };
        while (pending.TryPop(out var dir))
        {
            foreach (var path in Directory.EnumerateFiles(dir)) if ((File.GetAttributes(path)&FileAttributes.ReparsePoint)==0 && !path.EndsWith("_c") && !path.EndsWith(".log") && !path.EndsWith(".tmp")) yield return path;
            foreach (var path in Directory.EnumerateDirectories(dir))
            {
                var rel = Path.GetRelativePath(root, path).Replace('\\', '/');
                if (Path.GetFileName(path).StartsWith('.') || ignore.Contains(Path.GetFileName(path)) || rel.StartsWith("Art/build", StringComparison.OrdinalIgnoreCase) || rel.StartsWith("Art/baseline", StringComparison.OrdinalIgnoreCase)) continue;
                if ((File.GetAttributes(path) & FileAttributes.ReparsePoint) == 0) pending.Push(path);
            }
        }
    }
    internal async Task<JsonNode> Restore()
    {
        if (restored) return new JsonObject { ["ok"] = true, ["already_restored"] = true };
        var report = new JsonArray(); var warnings = new JsonArray();
        var state = await GuideNative.Async("state");
        foreach (var w in state.AsObject().A("windows")) if (!windows.Contains(w["ref"].GetValue<string>()))
        {
            await GuideNative.Async("close_window", new() { ["target"] = w["ref"].Copy() });
        }
        await GuideHost.Main(() =>
        {
            foreach (var pair in undo)
            {
                if (!SceneEditorSession.All.Contains(pair.Key)) continue;
                var system = pair.Key.UndoSystem;
                if (system.Back.Count < pair.Value) { warnings.Add("Undo history moved before the checkpoint; original undo entries were preserved."); continue; }
                var count = 0; while (system.Back.Count > pair.Value && count++ < 2000) if (!system.Undo()) break;
                if (count > 0) report.Add($"Undid {count} scene operations");
            }
            // Restore only files belonging to this captured workspace. Never traverse junctions.
            foreach (var path in Files(root).ToArray())
            {
                var rel = Path.GetRelativePath(root, path);
                if (!hashes.ContainsKey(rel)&&!skipped.Contains(rel,StringComparer.OrdinalIgnoreCase))
                {
                    if (new FileInfo(path).Length > 64 * 1024 * 1024) { warnings.Add("Large untracked file retained: " + rel); continue; }
                    // Keep a reversible copy of files created by the rehearsal.
                    var created = Path.Combine(backup, "_created", rel); Directory.CreateDirectory(Path.GetDirectoryName(created));
                    File.Move(path, created); report.Add("Moved rehearsal-created file into checkpoint: " + rel);
                }
            }
            foreach (var pair in hashes)
            {
                var path = Path.GetFullPath(Path.Combine(root, pair.Key));
                if (!path.StartsWith(root + Path.DirectorySeparatorChar, StringComparison.OrdinalIgnoreCase)) throw new InvalidOperationException("Checkpoint path escaped project root.");
                if (File.Exists(path) && Convert.ToHexString(System.Security.Cryptography.SHA256.HashData(File.ReadAllBytes(path))) == pair.Value) continue;
                Directory.CreateDirectory(Path.GetDirectoryName(path)); File.Copy(Path.Combine(blobs, pair.Value), path, true); report.Add("Restored " + pair.Key);
            }
            foreach(var s in SceneEditorSession.All.ToArray())if(!undo.ContainsKey(s))s.Destroy();
            foreach(var s in undo.Keys){
                var target=s;
                if(!SceneEditorSession.All.Contains(s)){
                    target=string.IsNullOrEmpty(scenePaths[s])?null:SceneEditorSession.CreateFromPath(scenePaths[s]);target??=SceneEditorSession.CreateDefault();
                    target.Scene.Deserialize(sceneSnapshots[s].DeepClone().AsObject());
                    target.Scene.Name=sceneNames[s];
                    report.Add("Reopened original scene from its in-memory checkpoint: "+(scenePaths[s]??"untitled"));warnings.Add("A closed scene's prior undo history could not be reconstructed.");
                }
                target.DeserializeSelection(selections[s]);target.HasUnsavedChanges=dirty[s];
                if(s==activeSession)target.MakeActive();
            }
            hammer?.Restore(report,warnings);
            return GJson.Node(new { ok = true });
        });
        restored = true;
        if(skipped.Count>0)warnings.Add($"{skipped.Count} original files exceed the backup limits (64 MiB per file / 512 MiB total); their contents were not restored.");
        return new JsonObject { ["ok"] = true, ["complete"] = warnings.Count==0, ["rolled_back"] = report, ["warnings"] = warnings, ["backup"] = backup, ["skipped_files"]=GJson.Node(skipped), ["original_scene"] = active, ["original_open_scenes"] = GJson.Node(scenes) };
    }
}

/// <summary>Uses Hammer's own native saved-object facility, through the existing managed interop.</summary>
internal sealed class GuideHammerCheckpoint
{
    private Editor.MapDoc.MapDocument document;
    private object saved;
    private Type savedType;
    private HashSet<long> ids;
    private long[] selected;
    private Editor.MapEditor.SelectMode mode;
    // Match Hammer's own MCP node enumeration. Prefab contents belong to other worlds
    // with separate ID spaces and must never be removed through this map document.
    private static IEnumerable<Editor.MapDoc.MapNode> Walk(Editor.MapDoc.MapWorld world){yield return world;foreach(var node in world.Children.Where(n=>n.World==world))yield return node;}
    private static long Id(Editor.MapDoc.MapNode node)=>GuideCompatibility.NodeId(node)??throw new NotSupportedException("This engine cannot expose Hammer node identity; native Hammer checkpoint rollback is unavailable.");
    internal static GuideHammerCheckpoint Capture()
    {
        if(!Editor.MapEditor.Hammer.Open||Editor.MapEditor.Hammer.ActiveMap is not {} map)return null;
        var value=new GuideHammerCheckpoint{document=map,ids=Walk(map.World).Select(Id).ToHashSet(),selected=Editor.MapEditor.Selection.All.Where(n=>n.World==map.World).Select(Id).ToArray(),mode=Editor.MapEditor.Selection.SelectMode};
        value.savedType=typeof(Editor.MapDoc.MapDocument).Assembly.GetType("NativeHammer.CSavedObjects")??throw new NotSupportedException("Hammer saved-object interop is unavailable in this engine.");
        value.saved=value.Invoke("Create");
        foreach(var child in map.World.Children.Where(n=>n.World==map.World))value.Invoke("SaveObject",child);
        return value;
    }
    private object Invoke(string name,params object[] args)=>savedType.GetMethod(name,BindingFlags.Static|BindingFlags.Instance|BindingFlags.NonPublic|BindingFlags.Public).Invoke(saved,args);
    internal void Restore(JsonArray report,JsonArray warnings)
    {
        try{
            if(!Editor.MapEditor.Hammer.Open||Editor.MapEditor.Hammer.ActiveMap!=document){warnings.Add("The original Hammer document is no longer active; its native checkpoint was retained without touching another map.");return;}
            foreach(var node in Walk(document.World).Reverse().ToArray())if(node.IsValid()&&!ids.Contains(Id(node)))document.DeleteNode(node);
            Invoke("RestoreObjects");
            var remaining=Walk(document.World).Select(Id).ToHashSet();
            var missing=ids.Where(id=>!remaining.Contains(id)).ToArray();
            if(missing.Length>0)warnings.Add("Hammer cannot reconstruct deleted original nodes through its saved-object API. Use native Undo for these IDs: "+string.Join(", ",missing));
            Editor.MapEditor.Selection.Clear();Editor.MapEditor.Selection.SelectMode=mode;
            foreach(var id in selected)if(Walk(document.World).FirstOrDefault(n=>Id(n)==id) is {} node)Editor.MapEditor.Selection.Add(node);
            report.Add("Restored surviving Hammer objects and object selection; native undo history/settings are not rewound.");
        }catch(Exception e){warnings.Add("Hammer rollback: "+e.GetBaseException().Message);}
        finally{Release();}
    }
    internal void Release(){if(saved!=null){Invoke("DeleteThis");saved=null;}}
}
