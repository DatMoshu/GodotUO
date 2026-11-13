// SPDX-License-Identifier: BSD-2-Clause
#if TOOLS
using System;
using System.Collections;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Reflection;
using System.Security.Cryptography;
using System.Text.Json;
using System.Threading.Tasks;
using Godot;

[Tool]
public partial class NativeProofPlugin : EditorPlugin
{
    private readonly List<string> _passed = new();
    private readonly List<string> _unsupported = new();
    private string _out;
    private Assembly _game;
    private Type _reader, _layerType;
    private Node _dock;
    private bool _control;
    private const BindingFlags Flags = BindingFlags.Public | BindingFlags.NonPublic | BindingFlags.Static | BindingFlags.Instance;
    private static object Field(object obj, string name) => obj.GetType().GetField(name, Flags).GetValue(obj);
    private static void Put(object obj, string name, object value) => obj.GetType().GetField(name, Flags).SetValue(obj, value);
    private static object Call(object obj, string name, params object[] args) => obj.GetType().GetMethod(name, Flags).Invoke(obj, args);
    private object Reader(string name, params object[] args) => _reader.GetMethod(name, Flags).Invoke(null, args);
    private object NewLayer(string image, string id = "witness")
    {
        object layer = Activator.CreateInstance(_layerType);
        Put(layer, "Id", id); Put(layer, "Image", image);
        return layer;
    }
    private void Check(bool ok, string name)
    {
        if (!ok) throw new InvalidOperationException("FAIL " + name);
        _passed.Add(name); GD.Print("PASS " + name);
    }
    private static string Hash(string file) => Convert.ToHexString(SHA256.HashData(File.ReadAllBytes(file)));
    private static string Pixels(Image image) => Convert.ToHexString(SHA256.HashData(image.GetData()));
    private static string Arg(string name)
    {
        string[] args = OS.GetCmdlineUserArgs(); int i = Array.IndexOf(args, name);
        return i < 0 ? null : args[i + 1];
    }
    public override void _EnterTree()
    {
        DisplayServer.WindowSetFlag(DisplayServer.WindowFlags.NoFocus, true);
        Callable.From(Run).CallDeferred();
    }
    private async void Run()
    {
        _out = Arg("--proof-out");
        _control = OS.GetCmdlineUserArgs().Contains("--control");
        string inherited = System.Environment.GetEnvironmentVariable("GUO_LAYER_DIR");
        string failure = null;
        try
        {
            Directory.CreateDirectory(_out);
            _game = _control ? GetType().Assembly : Assembly.Load("GUO");
            _reader = _game.GetType("GUO.Renderer.TerrainLayers", true);
            _layerType = _game.GetType("GUO.Renderer.TerrainLayer", true);
            if (Arg("--prepared-root") is string prepared)
            {
                await ColdConsume(prepared);
                return;
            }
            string root = Path.Combine(_out, "layers");
            Directory.CreateDirectory(Path.Combine(root, "overlays"));
            Directory.CreateDirectory(Path.Combine(root, "underlays"));
            System.Environment.SetEnvironmentVariable("GUO_LAYER_DIR", root);
            using var image = Image.CreateEmpty(4, 4, false, Image.Format.Rgba8);
            image.Fill(new Color(0.8f, 0.1f, 0.25f, 0.75f));
            string outside = Path.Combine(_out, "outside.png");
            Check(image.SavePng(outside) == Error.Ok, "synthetic PNG created");
            string outsideHash = Hash(outside);
            foreach (string path in new[] { "../outside.png", outside })
            {
                using var texture = Reader("LoadTexture", root, NewLayer(path)) as Texture2D;
                Check(_control ? texture != null : texture == null, (_control ? "CONTROL outside read " : "fixed rejects outside read ") + path);
                if (_control)
                {
                    using var decoded = texture.GetImage();
                    decoded.ClearMipmaps();
                    Check(Pixels(decoded) == Pixels(image), "CONTROL decoded outside pixel witness " + path);
                }
            }
            Check(Hash(outside) == outsideHash, "outside PNG byte hash unchanged");
            if (_control)
            {
                string outsideJson = Path.Combine(_out, "outside.json");
                File.WriteAllText(outsideJson, "{\"layers\":[{\"id\":\"outside\",\"image\":\"../outside.png\"}]}");
                string originalHash = Hash(outsideJson);
                File.CreateSymbolicLink(Path.Combine(root, "layers.json"), outsideJson);
                Check(((IList)Reader("Load", new object[] { null })).Count == 1, "CONTROL follows outside manifest link read");
                var empty = (IList)Activator.CreateInstance(typeof(List<>).MakeGenericType(_layerType));
                object defaults = Activator.CreateInstance(_game.GetType("GUO.Renderer.LayerDefaults", true));
                Check((bool)Reader("Save", empty, defaults) && Hash(outsideJson) != originalHash, "CONTROL follows outside manifest link write");
                string linkedDir = Path.Combine(root, "linked");
                Directory.CreateSymbolicLink(linkedDir, _out);
                using var linkedTexture = Reader("LoadTexture", root, NewLayer("linked/outside.png")) as Texture2D;
                Check(linkedTexture != null, "CONTROL follows descendant image link");
                return;
            }

            // Instantiate only the production dock; no game plugin/data loader or AI startup.
            Type ai = _game.GetType("GUO.Editor.AiFeatures", true);
            ai.GetMethod("Apply", Flags).Invoke(null, new object[] { false });
            Check(!(bool)ai.GetProperty("Enabled", Flags).GetValue(null), "AI disabled in production gate snapshot");
            _dock = (Node)Activator.CreateInstance(_game.GetType("GUO.Editor.LayersDock", true));
            AddChild(_dock);
            await Frame();

            string nested = Path.Combine(root, "overlays", "space 雪");
            Directory.CreateDirectory(nested);
            string inside = Path.Combine(nested, "image.png");
            File.Copy(outside, inside);
            Check((string)Stage(inside, false) == "overlays/space 雪/image.png" && Hash(inside) == outsideHash, "safe inside no-copy normalized");
            string staged = (string)Stage(outside, false);
            Check(staged == "overlays/outside.png" && Hash(Path.Combine(root, staged)) == outsideHash, "explicit outside overlay byte-exact import");
            Check((string)Stage(outside, true) == "underlays/outside.png" && Hash(Path.Combine(root, "underlays", "outside.png")) == outsideHash,
                "explicit outside underlay byte-exact import");
            image.Fill(new Color(0.1f, 0.7f, 0.4f, 1f));
            image.SavePng(outside); outsideHash = Hash(outside);
            Stage(outside, false);
            Check(Hash(Path.Combine(root, staged)) == outsideHash, "same-name overwrite byte exact");
            Form("kept", "Kept", false, "overlays\\space 雪\\image.png");
            Check(((string)Call(_dock, "AddFromUi")).StartsWith("Added kept"), "native Add valid backslash image");
            Check(((LineEdit)Field(_dock, "_image")).Text == "overlays/space 雪/image.png", "new persisted image normalized");
            Check((Texture2D)_dock.GetType().GetProperty("Preview").GetValue(_dock) != null, "native dock preview");
            string originalJson = File.ReadAllText(Path.Combine(root, "layers.json"));
            Call(_dock, "Refresh"); Call(_dock, "Select", "kept");
            Check(File.ReadAllText(Path.Combine(root, "layers.json")) == originalJson && Snapshot().Contains("kept"), "same-process form manifest reopen");
            foreach (string bad in new[] { "../outside.png", outside, "overlays/../outside.png" })
            {
                Form("invalid-new", "Invalid path form", false, bad);
                string invalidBefore = Snapshot();
                Check(((string)Call(_dock, "AddFromUi")).StartsWith("Image not found") && Snapshot() == invalidBefore,
                    "Add invalid image preserves selected form/model " + bad);
                Check(((string)Call(_dock, "UpdateSelected")).StartsWith("Image not found") && Snapshot() == invalidBefore,
                    "Update invalid image preserves selected form/model " + bad);
            }
            Call(_dock, "Select", "kept");
            foreach (string kind in new[] { "overlays", "underlays" })
            {
                string relative = kind + "/outside.png";
                using var texture = Reader("LoadTexture", root, NewLayer(relative)) as Texture2D;
                Check(texture != null && texture.GetWidth() == 4 && texture.GetImage().HasMipmaps(), "prepared native texture/mipmap " + kind);
            }
            Check(Reader("LoadTexture", root, NewLayer("missing.png")) == null, "missing image graceful");
            File.WriteAllText(Path.Combine(root, "corrupt.png"), "synthetic corrupt PNG");
            Check(Reader("LoadTexture", root, NewLayer("corrupt.png")) == null, "corrupt image graceful");
            // Metadata stays when only the image is invalid.
            File.WriteAllText(Path.Combine(root, "layers.json"), "{\"layers\":[{\"id\":\"invalid\",\"image\":\"../outside.png\"}]}");
            Check(((IList)Reader("Load", new object[] { null })).Count == 1, "invalid image metadata retained");
            File.WriteAllText(Path.Combine(root, "layers.json"), originalJson);
            Call(_dock, "Refresh"); Call(_dock, "Select", "kept");

            string externalDir = Path.Combine(_out, "outside-dir"); Directory.CreateDirectory(externalDir);
            File.Copy(outside, Path.Combine(externalDir, "outside.png"));
            Directory.CreateSymbolicLink(Path.Combine(root, "linked"), externalDir);
            string before = Snapshot();
            try { Stage(Path.Combine(root, "linked", "outside.png"), false); Check(false, "inside-link should throw"); }
            catch (TargetInvocationException ex) when (ex.InnerException is IOException) { Check(Snapshot() == before, "inside-link rejects without outside-import fallback"); }
            string importLeaf = Path.Combine(root, "overlays", "outside.png");
            string originalLeafHash = Hash(importLeaf);
            File.Delete(importLeaf); File.CreateSymbolicLink(importLeaf, Path.Combine(externalDir, "outside.png"));
            string importTargetHash = Hash(Path.Combine(externalDir, "outside.png"));
            try { Stage(outside, false); Check(false, "linked import leaf should throw"); }
            catch (TargetInvocationException ex) when (ex.InnerException is IOException)
            {
                Check(Hash(Path.Combine(externalDir, "outside.png")) == importTargetHash, "linked import destination leaf rejected before overwrite");
            }
            File.Delete(importLeaf); File.Copy(outside, importLeaf);
            Check(Hash(importLeaf) == originalLeafHash, "synthetic import leaf fixture restored exactly");

            // Both fixed directory preflights reject before creating a missing sibling.
            foreach (string sub in new[] { "underlays", "overlays" })
            {
                string isolated = Path.Combine(_out, "dirs-" + sub); Directory.CreateDirectory(isolated);
                Directory.CreateSymbolicLink(Path.Combine(isolated, sub), externalDir);
                System.Environment.SetEnvironmentVariable("GUO_LAYER_DIR", isolated);
                var empty = (IList)Activator.CreateInstance(typeof(List<>).MakeGenericType(_layerType));
                object defaults = Activator.CreateInstance(_game.GetType("GUO.Renderer.LayerDefaults", true));
                Check(!(bool)Reader("Save", empty, defaults), "Save rejects linked " + sub);
                string other = sub == "underlays" ? "overlays" : "underlays";
                Check(!Directory.Exists(Path.Combine(isolated, other)), "both-directory preflight before any mkdir " + sub);
            }
            System.Environment.SetEnvironmentVariable("GUO_LAYER_DIR", root);

            string manifest = Path.Combine(root, "layers.json");
            string externalJson = Path.Combine(externalDir, "outside.json");
            File.WriteAllText(externalJson, originalJson); string jsonHash = Hash(externalJson);
            File.Delete(manifest); File.CreateSymbolicLink(manifest, externalJson);
            Check(((IList)Reader("Load", new object[] { null })).Count == 0, "manifest link read rejected");
            Form("pending", "Unsaved form", true, "underlays/outside.png");
            before = Snapshot();
            Check(((string)Call(_dock, "AddFromUi")).Contains("Could not write") && Snapshot() == before, "blocked Add preserves selected form/model");
            Check(((string)Call(_dock, "UpdateSelected")).Contains("Could not write") && Snapshot() == before, "blocked Update preserves selected form/model");
            Check(((string)Call(_dock, "RemoveSelected")).Contains("Could not write") && Snapshot() == before, "blocked Remove preserves selected form/model");
            Put(_dock, "_decal", image);
            Form("", "Empty ID unsaved form", false, "overlays/outside.png"); before = Snapshot();
            Check(((string)Call(_dock, "SaveDecalToLayer")).StartsWith("Could not save") && Snapshot() == before
                && !File.Exists(Path.Combine(root, "overlays", "layer_2.png")), "empty-ID blocked decal preserves form/model/no PNG");
            Check(Hash(externalJson) == jsonHash, "outside manifest unchanged after all blocked mutations");
            File.Delete(manifest); File.WriteAllText(manifest, originalJson);
            // Leaf links, including dangling leaves, must be refused before writes.
            foreach (bool dangling in new[] { false, true })
            {
                string leaf = Path.Combine(root, "overlays", "linked-leaf.png");
                string target = Path.Combine(externalDir, dangling ? "missing-target.png" : "outside.png");
                File.CreateSymbolicLink(leaf, target);
                Form("linked-leaf", "Blocked leaf", false, "overlays/outside.png"); before = Snapshot();
                Check(((string)Call(_dock, "SaveDecalToLayer")).StartsWith("Could not save") && Snapshot() == before,
                    "decal leaf link rejected dangling=" + dangling);
                Check(Reader("LoadTexture", root, NewLayer("overlays/linked-leaf.png")) == null, "texture leaf link rejected dangling=" + dangling);
                File.Delete(leaf);
                Check(!dangling || !File.Exists(target), "dangling target remains absent");
            }
            Form("../../escape", "Traversal decal", false, "overlays/outside.png"); before = Snapshot();
            Check(((string)Call(_dock, "SaveDecalToLayer")).StartsWith("Could not save") && Snapshot() == before
                && !File.Exists(Path.Combine(_out, "escape.png")), "constructed decal traversal rejected before PNG/form/model");
            Form("nested 雪/good", "Nested decal", true, "underlays/outside.png");
            Check(((string)Call(_dock, "SaveDecalToLayer")).StartsWith("Saved nested 雪/good"), "valid nested decal saves");
            using (var saved = Image.LoadFromFile(Path.Combine(root, "underlays", "nested 雪", "good.png")))
                Check(Pixels(saved) == Pixels(image), "saved manual decal pixels exact");
            Put(_dock, "_decal", null);

            // Exercise the actual native FileSelected callback and deferred free.
            Form("pending", "Callback form", false, "overlays/outside.png"); before = Snapshot();
            Call(_dock, "BrowseImage");
            var picker = _dock.GetChildren().OfType<FileDialog>().Single();
            picker.EmitSignal(FileDialog.SignalName.FileSelected, Path.Combine(_out, "missing-source.png"));
            Check(Snapshot() == before && ((string)_dock.GetType().GetProperty("Status").GetValue(_dock)).StartsWith("Could not import"),
                "native missing-source callback status/form preserved");
            Check(picker.IsQueuedForDeletion(), "native failure callback queues dialog free");
            await Frame();
            Check(!_dock.GetChildren().OfType<FileDialog>().Any(), "native callback dialog actually freed");
            Call(_dock, "BrowseImage"); picker = _dock.GetChildren().OfType<FileDialog>().Single();
            picker.EmitSignal(FileDialog.SignalName.FileSelected, outside);
            Check(((LineEdit)Field(_dock, "_image")).Text == "overlays/outside.png" && picker.IsQueuedForDeletion(), "native success callback assigns normalized result and cleans up");
            await Frame();
            // Linked browse directory is refused before dialog creation.
            string browseRoot = Path.Combine(_out, "browse-blocked"); Directory.CreateDirectory(browseRoot);
            Directory.CreateSymbolicLink(Path.Combine(browseRoot, "overlays"), externalDir);
            System.Environment.SetEnvironmentVariable("GUO_LAYER_DIR", browseRoot); before = Snapshot();
            Call(_dock, "BrowseImage");
            Check(Snapshot() == before && !_dock.GetChildren().OfType<FileDialog>().Any()
                && ((string)_dock.GetType().GetProperty("Status").GetValue(_dock)).StartsWith("Could not browse"), "linked initial browse directory rejected with status/form preserved");
            // Open on a safe root, then select after destination is replaced by a link.
            System.Environment.SetEnvironmentVariable("GUO_LAYER_DIR", root);
            Call(_dock, "BrowseImage"); picker = _dock.GetChildren().OfType<FileDialog>().Single();
            System.Environment.SetEnvironmentVariable("GUO_LAYER_DIR", browseRoot); before = Snapshot();
            picker.EmitSignal(FileDialog.SignalName.FileSelected, outside);
            Check(Snapshot() == before && picker.IsQueuedForDeletion(), "linked-destination native callback preserves form and frees");
            await Frame();
            Check(Hash(outside) == outsideHash && Hash(externalJson) == jsonHash, "all outside originals unchanged");
            string trusted = Path.Combine(_out, "trusted-redirected-root");
            Directory.CreateSymbolicLink(trusted, root);
            System.Environment.SetEnvironmentVariable("GUO_LAYER_DIR", trusted);
            Check((string)Stage(Path.Combine(trusted, "overlays", "space 雪", "image.png"), false) == "overlays/space 雪/image.png",
                "production StageImage selected redirected root works");
            Check(((IList)Reader("Load", new object[] { null })).Count == 2, "production manifest through selected redirected root works");
            using (var texture = Reader("LoadTexture", Path.Combine(trusted, "overlays"), NewLayer("space 雪/image.png")) as Texture2D)
                Check(texture != null, "production texture trusted redirected ancestor works");
            Check(Reader("LoadTexture", trusted, NewLayer("linked/outside.png")) == null, "production descendant link below trusted root rejected");
            _unsupported.Add("Access-denied ACL controls not exercised; non-Windows filesystem behavior not exercised.");
        }
        catch (Exception ex) { failure = ex.ToString(); GD.PrintErr(failure); }
        finally
        {
            _dock?.QueueFree();
            System.Environment.SetEnvironmentVariable("GUO_LAYER_DIR", inherited);
            bool restored = System.Environment.GetEnvironmentVariable("GUO_LAYER_DIR") == inherited;
            if (!restored) failure = (failure ?? "") + " environment restore failed";
            if (_out != null)
                File.WriteAllText(Path.Combine(_out, "result.json"), JsonSerializer.Serialize(new
                {
                    ok = failure == null, control = _control, assembly = _game?.Location,
                    assembly_sha256 = _game != null ? Hash(Path.Combine(ProjectSettings.GlobalizePath("res://"), ".godot", "mono", "temp", "bin", "Debug", _control ? "TerrainProof.dll" : "GUO.dll")) : null,
                    passed = _passed, unsupported = _unsupported, restored_inherited_layer_dir = restored, failure,
                }, new JsonSerializerOptions { WriteIndented = true }));
            GetTree().Quit(failure == null ? 0 : 1);
        }
    }
    private async Task Frame() => await ToSignal(GetTree(), SceneTree.SignalName.ProcessFrame);
    private async Task ColdConsume(string root)
    {
        System.Environment.SetEnvironmentVariable("GUO_LAYER_DIR", root);
        string manifestHash = Hash(Path.Combine(root, "layers.json"));
        var layers = (IList)Reader("Load", new object[] { null });
        Check(layers.Count == 2, "cold process reads two prepared layers");
        foreach (object layer in layers)
        {
            using var texture = Reader("LoadTexture", root, layer) as Texture2D;
            Check(texture != null && texture.GetWidth() == 4 && texture.GetHeight() == 4, "cold process prepared texture " + Field(layer, "Id"));
        }
        if (OS.GetCmdlineUserArgs().Contains("--reader-only"))
        {
            Check(_game.GetType("GUO.Renderer.TerrainLayerPaths") != null, "Release shared path helper present");
            Check(!_game.GetTypes().Any(t => t.FullName?.StartsWith("GUO.Editor.", StringComparison.Ordinal) == true), "Release editor types absent");
            Check(Hash(Path.Combine(root, "layers.json")) == manifestHash, "Release prepared manifest unchanged");
            return;
        }
        Type ai = _game.GetType("GUO.Editor.AiFeatures", true);
        ai.GetMethod("Apply", Flags).Invoke(null, new object[] { false });
        _dock = (Node)Activator.CreateInstance(_game.GetType("GUO.Editor.LayersDock", true));
        AddDock((EditorDock)_dock);
        ((EditorDock)_dock).Open();
        await Frame();
        foreach (string id in new[] { "kept", "nested 雪/good" })
        {
            Call(_dock, "Select", id);
            Check(((LineEdit)Field(_dock, "_id")).Text == id && (Texture2D)_dock.GetType().GetProperty("Preview").GetValue(_dock) != null,
                "cold process dock reopens prepared layer " + id);
            if (DisplayServer.GetName() != "headless")
            {
                for (int i = 0; i < 45; i++) await Frame();
                _dock.GetType().GetProperty("CustomMinimumSize").SetValue(_dock, new Vector2(900, 700));
                ((EditorDock)_dock).MakeVisible();
                for (int i = 0; i < 5; i++) await Frame();
                Check(((Control)_dock).IsVisibleInTree() && ((Control)_dock).Size.Y >= 200,
                    "native dock visibly expanded " + id);
                RenderingServer.ForceDraw();
                using var screenshot = EditorInterface.Singleton.GetBaseControl().GetViewport().GetTexture().GetImage();
                Check(screenshot.SavePng(Path.Combine(_out, id == "kept" ? "cold-overlay.png" : "cold-underlay.png")) == Error.Ok,
                    "native dock viewport capture " + id);
            }
        }
        RemoveDock((EditorDock)_dock);
        Check(Hash(Path.Combine(root, "layers.json")) == manifestHash, "cold process prepared manifest unchanged");
    }
    private object Stage(string path, bool underlay) => _dock.GetType().GetMethod("StageImage", Flags).Invoke(null, new object[] { path, underlay });
    private void Form(string id, string name, bool underlay, string image) => Call(_dock, "SetForm", id, name, underlay, 0, 10, 20, 15, 25, image, "synthetic", 0.5f);
    private string Snapshot()
    {
        string[] text = new[] { "_id", "_name", "_image", "_prompt" }.Select(n => ((LineEdit)Field(_dock, n)).Text).ToArray();
        double[] numbers = new[] { "_facet", "_x0", "_y0", "_x1", "_y1", "_opacity" }.Select(n => ((SpinBox)Field(_dock, n)).Value).ToArray();
        var model = ((IList)Field(_dock, "_layers")).Cast<object>().Select(l => new[] { "Id", "Name", "Image", "Prompt", "X0", "Y0", "X1", "Y1", "Opacity", "Kind", "Facet" }
            .Select(n => Field(l, n).ToString()).ToArray()).ToArray();
        return JsonSerializer.Serialize(new { text, numbers, model, kind = ((OptionButton)Field(_dock, "_kind")).Selected,
            selection = ((ItemList)Field(_dock, "_list")).GetSelectedItems() });
    }
}
#endif
