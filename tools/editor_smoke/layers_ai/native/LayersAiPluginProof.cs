// SPDX-License-Identifier: BSD-2-Clause
#if TOOLS
using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Reflection;
using System.Security.Cryptography;
using System.Text.Json;
using System.Threading;
using System.Threading.Tasks;
using Godot;
using GUO.Editor;
using Environment = System.Environment;

[Tool]
public partial class LayersAiPluginProof : EditorPlugin
{
    private const BindingFlags Flags = BindingFlags.Public | BindingFlags.NonPublic | BindingFlags.Static | BindingFlags.Instance;
    private readonly List<string> _passed = new();
    private GuoEditorPlugin _plugin;
    private EditorSettings _settings;
    private Variant _original;
    private bool _hadSetting, _wasVisible;
    private string _out, _inheritedLayers;
    private int _discoveries, _factories;
    private bool _requireSavedFalse;
    private static object Get(object o, string n) => o.GetType().GetField(n, Flags).GetValue(o);
    private static object Call(object o, string n, params object[] a) => o.GetType().GetMethod(n, Flags).Invoke(o, a);
    private static void Property(object o, string n, object v) => o.GetType().GetProperty(n, Flags).SetValue(o, v);
    private static readonly Type Gate = typeof(LayersDock).Assembly.GetType("GUO.Editor.AiFeatures", true);
    private static bool Enabled => (bool)Gate.GetProperty("Enabled", Flags).GetValue(null);
    private void Check(bool ok, string name)
    {
        if (!ok) throw new Exception("FAIL " + name);
        _passed.Add(name); GD.Print("PASS " + name);
    }
    private async Task Frame(int n = 1) { for (int i = 0; i < n; i++) await ToSignal(GetTree(), SceneTree.SignalName.ProcessFrame); }
    public override void _EnterTree()
    {
        string[] args = OS.GetCmdlineUserArgs(); _out = args[Array.IndexOf(args, "--proof-out") + 1];
        _settings = EditorInterface.Singleton.GetEditorSettings();
        _hadSetting = _settings.HasSetting("guo/ai/enabled");
        if (_hadSetting) _original = _settings.GetSetting("guo/ai/enabled");
        if (args.Contains("--seed-false"))
        {
            _settings.SetSetting("guo/ai/enabled", false);
            Callable.From(() => GetTree().Quit()).CallDeferred();
            return;
        }
        _requireSavedFalse = args.Contains("--require-saved-false");
        _inheritedLayers = Environment.GetEnvironmentVariable("GUO_LAYER_DIR");
        Environment.SetEnvironmentVariable("GUO_LAYER_DIR", Path.Combine(_out, "layers"));
        if (!_requireSavedFalse) _settings.SetSetting("guo/ai/enabled", false);
        // All unrelated AI tabs stay outside visibility-driven discovery. This is a
        // headless wiring proof, not a visual or whole-editor readiness test.
        var baseControl = EditorInterface.Singleton.GetBaseControl();
        _wasVisible = baseControl.Visible; baseControl.Hide();
        Callable.From(Run).CallDeferred();
    }
    private async Task Route(bool enabled)
    {
        _settings.SetSetting("guo/ai/enabled", enabled);
        // Emit the actual EditorSettings signal; production subscribes during Build.
        _settings.EmitSignal(EditorSettings.SignalName.SettingsChanged);
        await Frame(2);
        Check(Enabled == enabled, "actual settings callback applies shared gate " + enabled);
    }
    private static Image Capture()
    {
        var image = Image.CreateEmpty(8, 8, false, Image.Format.Rgba8); image.Fill(Colors.Black); return image;
    }
    private void Bind(LayersDock dock, Fake fake, string workflow)
    {
        Property(dock, "DiscoverWorkflows", (Func<List<string>>)(() => { _discoveries++; return new List<string> { workflow }; }));
        Property(dock, "CreateProvider", (Func<IImageProvider>)(() => { _factories++; return fake; }));
    }
    private async void Run()
    {
        string failure = null;
        try
        {
            Directory.CreateDirectory(_out);
            if (_requireSavedFalse)
                Check(EditorInterface.Singleton.GetEditorPaths().IsSelfContained() && _hadSetting && !_original.AsBool(),
                    "cold editor reads persisted false preference from isolated self-contained settings");
            _plugin = new GuoEditorPlugin(); AddChild(_plugin); await Frame(3);
            var dock = (LayersDock)Get(_plugin, "_layers");
            Check(dock != null && !Enabled && !((Control)Get(dock, "_generationRow")).Visible && ((OptionButton)Get(dock, "_wfPick")).ItemCount == 0,
                "actual plugin Build reads false EditorSettings before Layers construction");
            Check(((Label)Get(dock, "_promptLabel")).Text == "Layer note", "actual plugin false retains manual metadata control");
            string wf = Path.Combine(_out, "workflow.json"); File.WriteAllText(wf, "{}");
            var fake = new Fake(); Bind(dock, fake, wf);
            await Route(true);
            Check(((Control)Get(dock, "_generationRow")).Visible && _discoveries > 0 && _factories == 0, "actual on callback reaches Layers and discovers without provider");
            Call(dock, "ReplaceCapture", Capture());
            Task pending = (Task)Call(dock, "RunGenerationAsync"); await fake.Entered.Task.WaitAsync(TimeSpan.FromSeconds(10));
            await Route(false);
            Check(fake.Token.IsCancellationRequested && !((Control)Get(dock, "_generationRow")).Visible && !(bool)Get(dock, "_busy"), "actual off callback cancels pending Layers job and hides groups");
            await Route(true);
            string status = dock.Status;
            fake.Result.TrySetException(new IOException("late plugin fake error")); await pending;
            Check(dock.Status == status && !(bool)Get(dock, "_busy"), "actual plugin off/on suppresses late fake error/finalizer");
            fake = new Fake(); Bind(dock, fake, wf);
            pending = (Task)Call(dock, "RunGenerationAsync"); await fake.Entered.Task.WaitAsync(TimeSpan.FromSeconds(10));
            // Exercise the real plugin teardown path without requesting an assembly reload.
            _plugin.OnBeforeSerialize(); await Frame(2);
            Check(Get(_plugin, "_layers") == null && fake.Token.IsCancellationRequested && (bool)Get(dock, "_stopped"), "real plugin teardown shuts Layers before freeing dock");
            fake.Result.TrySetResult(new ImageResult()); await pending;
            Check(fake.Disposed, "real plugin teardown pending runner completes and provider disposes");
            _settings.SetSetting("guo/ai/enabled", false);
            var retired = dock;
            _plugin.OnAfterDeserialize(); await Frame(4);
            dock = (LayersDock)Get(_plugin, "_layers");
            Check(dock != null && !ReferenceEquals(dock, retired) && !Enabled && !((Control)Get(dock, "_generationRow")).Visible,
                "same-assembly plugin rebuild rereads false preference and creates fresh off Layers");
            Check(new[] { "_capture", "_generated", "_decalRaw", "_decal", "_mask" }.All(n => Get(dock, n) == null), "reopened Layers owns no retired staging images");
            _plugin.QueueFree(); await Frame(3);
            Check((bool)Get(dock, "_stopped"), "actual plugin ExitTree also shuts Layers");
            _plugin = new GuoEditorPlugin(); AddChild(_plugin); await Frame(3);
            dock = (LayersDock)Get(_plugin, "_layers");
            Check(dock != null && !Enabled && ((Label)Get(dock, "_promptLabel")).Text == "Layer note", "fresh plugin instance opens with false preference and manual note");
            if (OS.GetCmdlineUserArgs().Contains("--capture"))
            {
                var data = (EditorData)Get(_plugin, "_data");
                data.LoadAsync();
                DateTime until = DateTime.UtcNow.AddSeconds(45);
                while (!data.IsLoaded && DateTime.UtcNow < until && string.IsNullOrEmpty(data.Error)) await Frame();
                Check(data.IsLoaded, "actual plugin manual-capture prerequisite data loads read-only");
                var world = (WorldView)Get(_plugin, "_world");
                EditorInterface.Singleton.GetBaseControl().Show();
                _plugin._MakeVisible(true);
                Check(world.EnsureBooted() && world.IsVisibleInTree(), "actual WorldView boots visibly in headless fixture with AI off");
                // No unrelated AI dock exists. Toggle only the shared gate and Layers
                // around the actual three-frame capture await; no provider is bound.
                Gate.GetMethod("Apply", Flags).Invoke(null, new object[] { true }); Call(dock, "ApplyAiFeatures");
                int before = (int)Get(dock, "_captureVersion");
                Call(dock, "CaptureFromUi");
                Check((int)Get(dock, "_captureVersion") == before + 1, "actual capture enters frame-await lifetime");
                Gate.GetMethod("Apply", Flags).Invoke(null, new object[] { false }); Call(dock, "ApplyAiFeatures");
                Check(!((CancellationTokenSource)Get(dock, "_dockLife")).IsCancellationRequested, "AI disable does not cancel manual capture lifetime");
                await Frame(6);
                Check(Get(dock, "_capture") != null || dock.Status.Contains("no image yet") || dock.Status.Contains("no camera yet"), "capture resumes after AI disable through actionable headless completion");
                before = (int)Get(dock, "_captureVersion");
                Call(dock, "CaptureFromUi");
                Check((int)Get(dock, "_captureVersion") == before + 1, "manual off capture starts another frame wait");
                dock.Shutdown(); dock.Shutdown(); _plugin.OnBeforeSerialize(); await Frame(6);
                Check(Get(dock, "_capture") == null && Get(_plugin, "_layers") == null,
                    "double Shutdown and plugin QueueFree during pending capture leave no late image or dock");
            }
        }
        catch (Exception ex) { failure = ex.ToString(); GD.PushError(failure); }
        finally
        {
            if (GodotObject.IsInstanceValid(_plugin)) { _plugin.OnBeforeSerialize(); _plugin.QueueFree(); }
            if (_hadSetting) _settings.SetSetting("guo/ai/enabled", _original); else _settings.Erase("guo/ai/enabled");
            Gate.GetMethod("Apply", Flags).Invoke(null, new object[] { _hadSetting ? _original.AsBool() : false });
            Environment.SetEnvironmentVariable("GUO_LAYER_DIR", _inheritedLayers);
            EditorInterface.Singleton.GetBaseControl().Visible = _wasVisible;
            Directory.CreateDirectory(_out);
            string assemblyFile = Path.Combine(ProjectSettings.GlobalizePath("res://"), ".godot/mono/temp/bin/Debug/GUO.dll");
            File.WriteAllText(Path.Combine(_out, "result.json"), JsonSerializer.Serialize(new { ok = failure == null, failure, passed = _passed,
                assembly_sha256 = Convert.ToHexString(SHA256.HashData(File.ReadAllBytes(assemblyFile))),
                loaded_mvid = typeof(LayersDock).Assembly.ManifestModule.ModuleVersionId, discoveries = _discoveries, factories = _factories,
                persisted_false_checked = _requireSavedFalse,
                capture_lifetime_checked = OS.GetCmdlineUserArgs().Contains("--capture"),
                limits = new[] { "headless actual plugin wiring and teardown only; unrelated diagnostics retained", "no real provider or service called; unrelated AI tabs remain hidden", "capture checks exercise actual frame awaits and headless no-image recovery, not captured pixel/render parity", "same-assembly rebuild; assembly hot-reload remains separately red" } }, new JsonSerializerOptions { WriteIndented = true }));
            GetTree().Quit(failure == null ? 0 : 1);
        }
    }
    private sealed class Fake : IImageProvider, IDisposable
    {
        public string Id => "synthetic"; public string Name => "Synthetic"; public bool NeedsKey => false;
        public CancellationToken Token; public bool Disposed;
        public TaskCompletionSource<bool> Entered = new(TaskCreationOptions.RunContinuationsAsynchronously);
        public TaskCompletionSource<ImageResult> Result = new(TaskCreationOptions.RunContinuationsAsynchronously);
        public Task<ImageResult> RunAsync(ImageRequest request, Action<double> progress, CancellationToken ct)
        { Token = ct; Entered.TrySetResult(true); return Result.Task; }
        public void Dispose() => Disposed = true;
    }
}
#endif
