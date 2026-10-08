#if TOOLS
namespace GUO.Editor;

using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text.Json;
using System.Text.Json.Nodes;
using Godot;

/// <summary>Typed editor operations shared by chat and MCP. Every callback runs on the editor thread.</summary>
internal static class EditorCapabilities
{
    internal static bool Sensitive(string value) => new[] { "password", "token", "secret", "credential", "api_key", "apikey", "auth.json", ".env" }
        .Any(s => (value ?? "").Contains(s, StringComparison.OrdinalIgnoreCase));

    private static string Json(object value) => JsonSerializer.Serialize(value);
    private static string Str(JsonNode a, string key, string fallback = "") => (string)a?[key] ?? fallback;
    private static bool Bool(JsonNode a, string key) => (bool?)a?[key] ?? false;
    private static int Num(JsonNode a, string key, int fallback, int min, int max) => Math.Clamp((int?)a?[key] ?? fallback, min, max);
    private static JsonObject Schema(params (string Name, string Type)[] fields)
    {
        var p = new JsonObject();
        foreach (var f in fields) p[f.Name] = new JsonObject { ["type"] = f.Type };
        return new JsonObject { ["type"] = "object", ["properties"] = p, ["additionalProperties"] = false };
    }

    internal static void Register(AiToolHost host, SearchContext ctx, Func<SearchIndex> index, Func<EditorTour> tour = null)
    {
        void Add(string name, string description, Func<JsonNode, string> run, bool read = true, params (string, string)[] fields) =>
            host.Register(new AiToolHost.Tool { Name = name, Description = description, Parameters = Schema(fields), ReadOnly = read, Run = run, MaxResult = 32000 });

        Add("editor_state", "Read open scenes, selected scene nodes, UO data readiness and F3 indexing progress.", _ => Json(new
        {
            scenes = EditorInterface.Singleton.GetOpenScenes().ToArray(),
            selected = EditorInterface.Singleton.GetSelection().GetSelectedNodes().Select(n => n.GetPath().ToString()).ToArray(),
            assetsReady = ctx.Data?.IsLoaded == true,
            indexReady = index()?.Ready == true,
            indexProgress = index()?.Progress ?? 0,
            indexPending = index()?.Pending,
            multi = ctx.MultiEdit?.Doc is { } d ? new { d.Name, components = d.Parts.Count, d.CanUndo, d.CanRedo } : null,
        }));
        Add("editor_catalog", "Page through the live F3 catalog by kind. Returns stable action keys and labels; discovery never executes actions.", a =>
        {
            SearchIndex i = index() ?? throw new InvalidOperationException("F3 index unavailable");
            i.Refresh();
            string kind = Str(a, "kind");
            int offset = Num(a, "offset", 0, 0, int.MaxValue), limit = Num(a, "limit", 30, 1, 100);
            var entries = i.Providers.SelectMany(p => p.Entries).Where(e => !Sensitive(e.Key + " " + e.Title + " " + e.Hint))
                .Where(e => kind.Length == 0 || e.Kind.Equals(kind, StringComparison.OrdinalIgnoreCase)).OrderBy(e => e.Key, StringComparer.Ordinal).ToList();
            return Json(new { ready = i.Ready, total = entries.Count, offset, entries = entries.Skip(offset).Take(limit).Select(e => new { key = e.Key, kind = e.Kind, title = e.Title, hint = e.Hint, executable = e.Run != null, approvalRequired = true }) });
        }, true, ("kind", "string"), ("offset", "integer"), ("limit", "integer"));
        Add("editor_search", "Search the same F3 index as the editor UI. Results include stable keys; no actions execute.", a =>
        {
            SearchIndex i = index() ?? throw new InvalidOperationException("F3 index unavailable");
            i.Refresh();
            var rows = i.Query(Str(a, "query")).SelectMany(g => g.Items).Where(x => !Sensitive(x.Entry.Key + " " + x.Entry.Hint + " " + x.Entry.Title));
            return Json(new { ready = i.Ready, entries = rows.Take(Num(a, "limit", 20, 1, 50)).Select(x => new { key = x.Entry.Key, kind = x.Entry.Kind, title = x.Entry.Title, hint = x.Entry.Hint, score = x.Score }) });
        }, true, ("query", "string"), ("limit", "integer"));
        Add("editor_invoke", "Request an F3 action by its stable key. Requires approval in GUO; returns dispatch acknowledgement, not operation completion. May open an additional native dialog.", a =>
        {
            SearchIndex i = index() ?? throw new InvalidOperationException("F3 index unavailable");
            i.Refresh();
            string key = Str(a, "key");
            if (key.Length == 0 || Sensitive(key)) throw new ArgumentException("A non-sensitive action key is required");
            SearchEntry entry = i.Resolve(key, Str(a, "query"));
            if (entry?.Run == null) throw new ArgumentException("Action is unavailable; search the live catalog again");
            entry.Run();
            return Json(new { dispatched = true, key });
        }, false, ("key", "string"), ("query", "string"));
        Add("scene_tree", "Read nodes in the currently edited scene. Bounded by depth and count; never reads the editor's AI transcripts.", a =>
        {
            Node root = EditorInterface.Singleton.GetEditedSceneRoot();
            int depth = Num(a, "depth", 4, 0, 12), limit = Num(a, "limit", 100, 1, 300);
            var rows = new List<object>();
            void Walk(Node n, int level)
            {
                if (n == null || rows.Count >= limit) return;
                rows.Add(new { path = n.GetPath().ToString(), name = n.Name.ToString(), type = n.GetClass().ToString(), level });
                if (level < depth) foreach (Node child in n.GetChildren()) Walk(child, level + 1);
            }
            Walk(root, 0);
            return Json(new { nodes = rows, limitReached = rows.Count == limit });
        }, true, ("depth", "integer"), ("limit", "integer"));
        Add("scene_node", "Read a scene node's property names/types. Values require an explicit non-sensitive property name.", a =>
        {
            Node n = SceneNode(Str(a, "path"));
            string property = Str(a, "property");
            if (property.Length > 0)
            {
                if (Sensitive(property)) throw new ArgumentException("Sensitive properties are not exposed");
                bool exists = n.GetPropertyList().Any(p => p["name"].AsString() == property);
                if (!exists) throw new ArgumentException("Unknown property");
                Variant v = n.Get(property);
                // Object getters and scripts are not traversed.
                return Json(new { property, type = v.VariantType.ToString(), value = v.VariantType is Variant.Type.Object or Variant.Type.Dictionary or Variant.Type.Array ? "[structured value omitted]" : v.ToString() });
            }
            return Json(new { path = n.GetPath().ToString(), properties = n.GetPropertyList().Where(p => !Sensitive(p["name"].AsString())).Take(160).Select(p => new { name = p["name"].AsString(), type = p["type"].AsInt32() }) });
        }, true, ("path", "string"), ("property", "string"));
        Add("scene_select", "Select an existing scene node in Godot's inspector. Requires approval because it changes editor selection.", a =>
        {
            Node n = SceneNode(Str(a, "path"));
            EditorInterface.Singleton.GetSelection().Clear();
            EditorInterface.Singleton.GetSelection().AddNode(n);
            EditorInterface.Singleton.EditNode(n);
            return Json(new { selected = n.GetPath().ToString() });
        }, false, ("path", "string"));
        host.Register(new AiToolHost.Tool
        {
            Name = "scene_set_property", Description = "Change one stored scene-node property through Godot undo history. Supports bool, integer, float, string, Vector2/3 and Color; scripts and object properties are refused. Requires approval.",
            ReadOnly = false, MaxResult = 64000,
            Parameters = new JsonObject { ["type"] = "object", ["properties"] = new JsonObject
            {
                ["path"] = new JsonObject { ["type"] = "string" }, ["property"] = new JsonObject { ["type"] = "string" },
                ["value"] = new JsonObject { ["description"] = "Scalar, or numeric array for Vector2 (2), Vector3 (3), Color (4)." },
            }, ["required"] = new JsonArray("path", "property", "value"), ["additionalProperties"] = false },
            Run = a =>
            {
                Node n = SceneNode(Str(a, "path"));
                string property = Str(a, "property");
                var spec = n.GetPropertyList().FirstOrDefault(p => p["name"].AsString() == property);
                if (spec == null || Sensitive(property) || property == "script" || property == "owner" ||
                    (((PropertyUsageFlags)spec["usage"].AsInt64()) & PropertyUsageFlags.Storage) == 0)
                    throw new ArgumentException("Property is not an editable stored value");
                Variant old = n.Get(property);
                JsonNode value = a?["value"] ?? throw new ArgumentException("A value is required");
                float F(int i) => value[i].GetValue<float>();
                Variant next = old.VariantType switch
                {
                    Variant.Type.Bool => Variant.From(value.GetValue<bool>()),
                    Variant.Type.Int => Variant.From(value.GetValue<long>()),
                    Variant.Type.Float => Variant.From(value.GetValue<double>()),
                    Variant.Type.String => Variant.From(value.GetValue<string>()),
                    Variant.Type.Vector2 when value is JsonArray { Count: 2 } => Variant.From(new Vector2(F(0), F(1))),
                    Variant.Type.Vector3 when value is JsonArray { Count: 3 } => Variant.From(new Vector3(F(0), F(1), F(2))),
                    Variant.Type.Color when value is JsonArray { Count: 4 } => Variant.From(new Color(F(0), F(1), F(2), F(3))),
                    _ => throw new ArgumentException("Unsupported property type or value shape"),
                };
                EditorUndoRedoManager undo = ctx.Plugin.GetUndoRedo();
                undo.CreateAction($"AI: {n.Name}.{property}");
                undo.AddDoProperty(n, property, next);
                undo.AddUndoProperty(n, property, old);
                undo.CommitAction();
                return Json(new { changed = true, path = n.GetPath().ToString(), property, value = n.Get(property).ToString(), undoable = true });
            },
        });
        Add("scene_open", "Open a project scene from res://. Requires approval; opening a scene can load its tool scripts.", a =>
        {
            string path = Str(a, "path");
            if (!path.StartsWith("res://", StringComparison.Ordinal) || path.Contains("..") || Sensitive(path) ||
                !(path.EndsWith(".tscn", StringComparison.OrdinalIgnoreCase) || path.EndsWith(".scn", StringComparison.OrdinalIgnoreCase)) || !Godot.FileAccess.FileExists(path))
                throw new ArgumentException("Use an existing res:// scene path without traversal");
            EditorInterface.Singleton.OpenSceneFromPath(path);
            return Json(new { dispatched = true, path });
        }, false, ("path", "string"));
        Add("project_files", "List imported project resource paths and types from Godot's filesystem index; never reads file contents.", a =>
        {
            int offset = Num(a, "offset", 0, 0, int.MaxValue), limit = Num(a, "limit", 40, 1, 100);
            string prefix = Str(a, "prefix", "res://");
            var rows = new List<(string Path, string Type)>();
            void Walk(EditorFileSystemDirectory dir)
            {
                if (dir == null) return;
                for (int j = 0; j < dir.GetFileCount(); j++)
                {
                    string path = dir.GetFilePath(j);
                    if (path.StartsWith(prefix, StringComparison.OrdinalIgnoreCase) && !Sensitive(path)) rows.Add((path, dir.GetFileType(j)));
                }
                for (int j = 0; j < dir.GetSubdirCount(); j++) Walk(dir.GetSubdir(j));
            }
            Walk(EditorInterface.Singleton.GetResourceFilesystem().GetFilesystem());
            return Json(new { total = rows.Count, files = rows.Skip(offset).Take(limit).Select(r => new { path = r.Path, type = r.Type }) });
        }, true, ("prefix", "string"), ("offset", "integer"), ("limit", "integer"));
        Add("project_setting", "Read one explicitly named non-sensitive Godot project setting; returns text and type.", a =>
        {
            string key = Str(a, "key");
            if (key.Length == 0 || Sensitive(key) || !ProjectSettings.HasSetting(key)) throw new ArgumentException("Unknown or sensitive setting");
            Variant value = ProjectSettings.GetSetting(key);
            return Json(new { key, type = value.VariantType.ToString(), value = value.ToString() });
        }, true, ("key", "string"));
        Add("multi_document", "Read the active Multi Editor document, with paginated components and undo state.", a =>
        {
            MultiDocument d = ctx.MultiEdit?.Doc ?? throw new InvalidOperationException("Multi Editor unavailable");
            int offset = Num(a, "offset", 0, 0, int.MaxValue), limit = Num(a, "limit", 50, 1, 100);
            return Json(new { d.Name, d.Source, total = d.Parts.Count, d.CanUndo, d.CanRedo, components = d.Parts.Skip(offset).Take(limit).Select(p => new { p.Uid, p.Id, p.X, p.Y, p.Z, p.Shown, p.Hue }) });
        }, true, ("offset", "integer"), ("limit", "integer"));
        Add("multi_validate", "Validate the active multi with the editor's existing tiledata and house legality rules.", _ =>
        {
            if (ctx.Data?.IsLoaded != true) throw new InvalidOperationException("UO data is not loaded");
            ctx.MultiEdit.ValidateNow();
            ValidationResult r = ctx.MultiEdit.Result;
            return Json(new { valid = !r.HasErrors, findings = r.Findings.Take(100), totalFindings = r.Findings.Count, walkable = r.Walkable.Count });
        });
        Add("multi_open", "Open a generated components description from this checkout's build/multi folder and show Multis. Unsaved changes are refused. Requires approval.", a =>
        {
            if (ctx.MultiEdit.Modified) throw new InvalidOperationException("Save or discard the current multi in GUO before replacing it");
            string file = MultiPath(Str(a, "path"));
            if (!ctx.MultiEdit.OpenDescription(file)) throw new ArgumentException("Not a valid components description");
            EditorInterface.Singleton.SetMainScreenEditor(MultiEditView.TabName);
            ctx.MultiEdit.Visible = true;
            return Json(new { opened = true, ctx.MultiEdit.Doc.Name, components = ctx.MultiEdit.Doc.Parts.Count });
        }, false, ("path", "string"));
        Add("multi_history", "Undo or redo one active multi operation through its existing document history. Requires approval.", a =>
        {
            MultiDocument d = ctx.MultiEdit.Doc;
            string action = Str(a, "action");
            if (action == "undo") d.Undo();
            else if (action == "redo") d.Redo();
            else throw new ArgumentException("action must be undo or redo");
            return Json(new { d.CanUndo, d.CanRedo, components = d.Parts.Count });
        }, false, ("action", "string"));
        if (tour != null)
        {
            host.Register(new AiToolHost.Tool
            {
                Name = "editor_screenshot",
                Description = "Save one frame of the editor window as a PNG under this checkout's build/ (out_dir-style relative or absolute path, .png). Machine paths on screen are scrubbed first. Requires approval unless the launching runner listed it in GUO_EDITOR_MCP_PREAPPROVED.",
                Parameters = Schema(("file", "string")), ReadOnly = false, MaxResult = 2000,
                RunAsync = async (a, _) => tour() is { } t ? await t.ScreenshotAsync(Str(a, "file")) : "error: the editor tour is not available in this editor",
            });
            host.Register(new AiToolHost.Tool
            {
                Name = "human_overlay",
                Description = "The scenario runner's human driver: draw a caption card (text, at most 300 characters, with a step label such as 3/18) over the editor with the tour's overlay, and answer {skip, abort}: the Space and Esc pressed since the last call (reading clears them). hide draws nothing but keeps the keys; clear removes the card; no arguments only reads. Changes nothing but the overlay. Requires approval unless the launching runner listed it in GUO_EDITOR_MCP_PREAPPROVED.",
                Parameters = Schema(("text", "string"), ("step", "string"), ("hide", "boolean"), ("clear", "boolean")), ReadOnly = false, MaxResult = 500,
                RunAsync = async (a, _) => tour() is { } t ? await t.HumanOverlayAsync(Str(a, "text"), Str(a, "step"), Bool(a, "hide"), Bool(a, "clear")) : "error: the editor tour is not available in this editor",
            });
            host.Register(new AiToolHost.Tool
            {
                Name = "tour_segment",
                Description = "Run one segment of the editor tour (ids as in tools/editor_tour) in this editor and return its checks and frame paths. Writes only under this checkout's build/. A segment that takes over 25 s answers state=running: call again with the same id to wait. Requires approval unless the runner that launched the editor listed it in GUO_EDITOR_MCP_PREAPPROVED.",
                Parameters = Schema(("id", "string"), ("out_dir", "string")), ReadOnly = false, MaxResult = 16000,
                RunAsync = async (a, _) => tour() is { } t ? await t.RunSegmentAsync(Str(a, "id"), Str(a, "out_dir")) : "error: the editor tour is not available in this editor",
            });
        }
        host.Register(new AiToolHost.Tool
        {
            Name = "multi_write_stage", Description = "Write and read-back verify the active multi through the existing stage writer; updates the Multis browser. Never writes the client install. Requires approval.",
            Parameters = Schema(), ReadOnly = false, MaxResult = 64000,
            RunAsync = async (_, ct) =>
            {
                SaveResult r = await ctx.MultiEdit.SaveToStageAsync(ct);
                if (!r.Ok) return "error: " + r.Error;
                return Json(new { ok = r.Ok, r.Name, r.Id, r.Components });
            },
        });
    }

    private static Node SceneNode(string path)
    {
        Node root = EditorInterface.Singleton.GetEditedSceneRoot() ?? throw new InvalidOperationException("No scene open");
        Node n = path.Length == 0 || path == "." ? root : root.GetNodeOrNull(new NodePath(path));
        for (Node ancestor = n; ancestor != null; ancestor = ancestor.GetParent()) if (ancestor == root) return n;
        throw new ArgumentException("Node must belong to the edited scene");
    }

    // Reject symlinks/junctions as well as lexical traversal; descriptions stay in generated output.
    internal static string MultiPath(string path)
    {
        string allowed = Path.GetFullPath(Path.Combine(EditorData.RepoRoot, "build", "multi"));
        string full = Path.GetFullPath(Path.IsPathRooted(path) ? path : Path.Combine(EditorData.RepoRoot, path));
        if (!full.StartsWith(allowed + Path.DirectorySeparatorChar, StringComparison.OrdinalIgnoreCase) || !full.EndsWith(".json", StringComparison.OrdinalIgnoreCase) || !File.Exists(full))
            throw new ArgumentException("Use an existing JSON description under build/multi");
        for (FileSystemInfo part = new FileInfo(full); part != null; part = part is FileInfo f ? f.Directory : ((DirectoryInfo)part).Parent)
        {
            if ((part.Attributes & FileAttributes.ReparsePoint) != 0) throw new ArgumentException("Linked description paths are refused");
            if (part.FullName.Equals(Path.GetFullPath(EditorData.RepoRoot), StringComparison.OrdinalIgnoreCase)) break;
        }
        if (new FileInfo(full).Length > 1024 * 1024) throw new ArgumentException("Description exceeds 1 MiB");
        return full;
    }
}
#endif
