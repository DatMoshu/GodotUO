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
public partial class LayersAiProof : EditorPlugin
{
    private const BindingFlags Flags = BindingFlags.Public | BindingFlags.NonPublic | BindingFlags.Instance | BindingFlags.Static;
    private readonly List<string> _passed = new();
    private LayersDock _dock;
    private string _out;
    private int _discoveries, _factories, _mainThread;
    private List<string> _catalog = new();
    private Fake _next;
    private Exception _discoveryFailure;
    private static readonly Type Gate = typeof(LayersDock).Assembly.GetType("GUO.Editor.AiFeatures", true);
    private static object Get(object o, string n) => o.GetType().GetField(n, Flags).GetValue(o);
    private static object Call(object o, string n, params object[] a) => o.GetType().GetMethod(n, Flags).Invoke(o, a);
    private static void Property(object o, string n, object v) => o.GetType().GetProperty(n, Flags).SetValue(o, v);
    private static void Apply(bool on) => Gate.GetMethod("Apply", Flags).Invoke(null, new object[] { on });
    private static string Pixels(Image i) => Convert.ToHexString(SHA256.HashData(i.GetData()));
    private bool Busy => (bool)Get(_dock, "_busy");
    private OptionButton Picker => (OptionButton)Get(_dock, "_wfPick");
    private Image Image(string n) => (Image)Get(_dock, n);
    private void Check(bool yes, string name)
    {
        if (!yes) throw new Exception("FAIL " + name);
        _passed.Add(name); GD.Print("PASS " + name);
    }
    private async Task Frame(int n = 1)
    {
        for (int i = 0; i < n; i++) await ToSignal(GetTree(), SceneTree.SignalName.ProcessFrame);
    }
    private Task RunJob() => (Task)Call(_dock, "RunGenerationAsync");
    private void Toggle(bool on) { Apply(on); Call(_dock, "ApplyAiFeatures"); }
    private static Image Solid(Color color, int size = 8)
    {
        var i = Godot.Image.CreateEmpty(size, size, false, Godot.Image.Format.Rgba8); i.Fill(color); return i;
    }
    private LayersDock NewDock()
    {
        var dock = new LayersDock();
        Property(dock, "DiscoverWorkflows", (Func<List<string>>)(() => { _discoveries++; if (_discoveryFailure != null) throw _discoveryFailure; return _catalog.ToList(); }));
        Property(dock, "CreateProvider", (Func<IImageProvider>)(() => { _factories++; return _next ?? throw new Exception("No fake bound"); }));
        AddChild(dock);
        return dock;
    }
    public override void _EnterTree() => Callable.From(Run).CallDeferred();

    private async void Run()
    {
        string[] args = OS.GetCmdlineUserArgs();
        _out = args[Array.IndexOf(args, "--proof-out") + 1];
        string inherited = Environment.GetEnvironmentVariable("GUO_LAYER_DIR");
        bool original = (bool)Gate.GetProperty("Enabled", Flags).GetValue(null);
        string failure = null;
        _mainThread = Environment.CurrentManagedThreadId;
        try
        {
            Directory.CreateDirectory(_out);
            Environment.SetEnvironmentVariable("GUO_LAYER_DIR", Path.Combine(_out, "layers"));
            Apply(false);
            _dock = NewDock(); await Frame();
            Check(_discoveries == 0 && _factories == 0, "initial off does not evaluate folder/catalog or construct provider");
            Check(new[] { "_workflowRow", "_generationRow", "_generatedGroup" }.All(n => !((Control)Get(_dock, n)).Visible), "complete AI groups hidden initially off");
            Check(((Label)Get(_dock, "_promptLabel")).Text == "Layer note" && Picker.ItemCount == 0, "off metadata label and empty workflow picker");
            Check(((Control)Get(_dock, "_extractView")).Visible && ((Control)Get(_dock, "_maskView")).Visible, "manual work previews remain visible");
            var buttons = Walk(_dock).OfType<Button>().ToArray();
            Check(new[] { "From area", "Capture area", "Re-extract", "Save to layer", "Fill", "Clear", "Browse", "Add", "Update", "Remove", "Reload world" }.All(t => buttons.Any(b => b.Text == t && b.Visible)), "all mixed manual buttons remain visible off");
            Call(_dock, "ReplaceCapture", Solid(Colors.Black));
            Call(_dock, "RefreshAuthorWorkflows");
            Picker.AddItem("valid.json"); Picker.SetItemMetadata(0, "valid.json"); Picker.Disabled = false;
            await RunJob();
            Check(_discoveries == 0 && _factories == 0 && _dock.Status.Contains("disabled") && !Busy, "direct off refresh and generation return before discovery/request/provider prep");
            Check(ComfyUiProvider.Workflows("\\\\must-not-contact.invalid\\no-share").Count == 0, "direct global workflow discovery returns empty off");
            Toggle(true);
            Check(_discoveries == 1 && _factories == 0 && Picker.Disabled && ((Button)Get(_dock, "_generate")).Disabled, "on empty catalog normal disabled state without provider");
            Check(((Label)Get(_dock, "_promptLabel")).Text == "Prompt" && ((Control)Get(_dock, "_generatedGroup")).Visible, "on AI groups and Prompt restored");
            string wf = Path.Combine(_out, "catalog", "flux_klein_edit.json");
            Directory.CreateDirectory(Path.GetDirectoryName(wf)); File.WriteAllText(wf, "{}");
            _catalog.Add(wf); Call(_dock, "RefreshAuthorWorkflows");
            Check(!Picker.Disabled && _factories == 0 && Picker.ItemCount == 1, "local synthetic catalog refresh has no provider construction");
            ((LineEdit)Get(_dock, "_prompt")).Text = "discovery note";
            foreach (Exception e in new Exception[] { new IOException("raw discovery error"), new UnauthorizedAccessException("raw discovery error") })
            {
                _discoveryFailure = e; Call(_dock, "RefreshAuthorWorkflows");
                Check(Picker.Disabled && Picker.ItemCount == 0 && ((Button)Get(_dock, "_generate")).Disabled && _factories == 0, e.GetType().Name + " empty disabled state with no provider");
                Check(_dock.Status.Contains("launchers/comfy/doctor.bat") && !_dock.Status.Contains("raw discovery") && ((LineEdit)Get(_dock, "_prompt")).Text == "discovery note", e.GetType().Name + " actionable sanitized status and metadata preserved");
                _discoveryFailure = null; Call(_dock, "RefreshAuthorWorkflows");
                Check(!Picker.Disabled && Picker.ItemCount == 1 && !((Button)Get(_dock, "_generate")).Disabled && _factories == 0, e.GetType().Name + " refresh retry recovers");
            }
            ((SpinBox)Get(_dock, "_tol")).Value = 0; ((SpinBox)Get(_dock, "_feather")).Value = 0;
            Call(_dock, "FillMask", true);
            _dock.SetForm("saved", "Saved", false, 0, 1, 2, 3, 4, "", "note retained", 1);
            using var red = Solid(Colors.Red);
            byte[] png = red.SavePngToBuffer();
            ImageResult Success() => new() { Pngs = new List<byte[]> { png }, Seed = 42 };
            _next = new Fake(); Task success = RunJob(); await _next.Entered.Task.WaitAsync(TimeSpan.FromSeconds(10));
            Check(Busy && _next.Request.InputPng.Length > 0 && _next.Request.Prompt == "note retained" && _next.Request.Width == 8, "explicit job snapshots inputs before worker");
            _next.Finish(Success()); await success;
            Check(_next.Thread != _mainThread && Environment.CurrentManagedThreadId == _mainThread && _next.Disposed, "provider runs off editor thread and result resumes on editor thread with disposal");
            Check(Image("_generated") != null && Image("_decal") != null && !Busy && !((Button)Get(_dock, "_generate")).Disabled, "explicit fake success extracts and recovers button");
            string staged = Pixels(Image("_decal"));
            Toggle(false);
            Check(Pixels(Image("_decal")) == staged && Pixels(Image("_generated")) == Pixels(red), "AI off retains generated and extracted staging pixels");
            Check(((LineEdit)Get(_dock, "_prompt")).Text == "note retained", "AI off preserves Prompt metadata text");
            Check(_dock.SaveDecalToLayer().StartsWith("Saved saved"), "manual off staged Save succeeds");
            string savedFile = Path.Combine(_out, "layers", "overlays", "saved.png");
            using (var saved = Godot.Image.LoadFromFile(savedFile)) Check(Pixels(saved) == staged, "manual off staged save pixel/alpha readback exact");
            string json = File.ReadAllText(Path.Combine(_out, "layers", "layers.json"));
            Check(json.Contains("note retained"), "Prompt metadata persists in unchanged manifest schema");
            Call(_dock, "FillMask", false); Check(Image("_decal").GetPixel(4, 4).A == 0, "manual off Clear recombines transparent alpha");
            Call(_dock, "FillMask", true); Call(_dock, "Reextract");
            Check(Pixels(Image("_decal")) == staged, "manual off Fill and re-extract preserve expected pixels");
            ((Control)Get(_dock, "_maskView")).Size = new Vector2(80, 80);
            ((CheckBox)Get(_dock, "_erase")).ButtonPressed = true;
            Call(_dock, "PaintMaskAt", new Vector2(40, 40));
            Check(Image("_decal").GetPixel(4, 4).A == 0, "manual off brush erase recombines locally");
            Call(_dock, "FillMask", true);
            Call(_dock, "CaptureFromUi"); Check(_dock.Status.Contains("World tab has not booted"), "manual off capture prerequisite remains actionable");
            string outside = Path.Combine(_out, "explicit.png"); red.SavePng(outside);
            string relative = (string)Call(_dock, "StageImage", outside, false);
            _dock.SetForm("imported", "Imported", false, 0, 1, 2, 3, 4, relative, "manual note", 0.5f);
            Check(_dock.AddFromUi().StartsWith("Added imported"), "manual off explicit PNG registry Add");
            Call(_dock, "Select", "imported"); ((LineEdit)Get(_dock, "_prompt")).Text = "updated note";
            Check(_dock.UpdateSelected().StartsWith("Updated imported"), "manual off registry Update");
            Call(_dock, "Refresh"); Call(_dock, "Select", "imported");
            Check(((LineEdit)Get(_dock, "_prompt")).Text == "updated note", "manual off registry reopen metadata readback");
            Check(_dock.RemoveSelected().StartsWith("Removed imported"), "manual off registry Remove");

            Toggle(true);
            var late = _next = new Fake(); Task old = RunJob(); await late.Entered.Task.WaitAsync(TimeSpan.FromSeconds(10));
            Toggle(false); Check(late.Token.IsCancellationRequested && !Busy, "disable pending cancels linked token and releases busy");
            string offStatus = _dock.Status; Toggle(true);
            var fresh = _next = new Fake(); Task newer = RunJob(); await fresh.Entered.Task.WaitAsync(TimeSpan.FromSeconds(10));
            Check(!fresh.Token.IsCancellationRequested && late.Token.IsCancellationRequested, "off/on new token fresh old token permanently canceled");
            string newStatus = _dock.Status;
            late.Finish(Success()); await old;
            Check(Busy && _dock.Status == newStatus && Pixels(Image("_decal")) == staged, "late old success and finally cannot overwrite staging/status/new busy");
            fresh.Finish(Success()); await newer;
            Check(!Busy, "new job completes after old finalizer suppression");
            late = _next = new Fake(); old = RunJob(); await late.Entered.Task.WaitAsync(TimeSpan.FromSeconds(10));
            Toggle(false); Toggle(true); newStatus = _dock.Status;
            late.Fail(new IOException("raw-secret-like-provider-error")); await old;
            Check(_dock.Status == newStatus && !Busy, "late old exception suppressed after off/on");
            var coop = _next = new Fake(cooperative: true); old = RunJob(); await coop.Entered.Task.WaitAsync(TimeSpan.FromSeconds(10));
            Toggle(false); await old;
            Check(coop.Token.IsCancellationRequested && !Busy, "cooperative cancellation finishes cleanly");
            Toggle(true);
            late = _next = new Fake(); old = RunJob(); await late.Entered.Task.WaitAsync(TimeSpan.FromSeconds(10));
            Call(_dock, "ReplaceCapture", Solid(Colors.Blue, 12));
            Check(late.Token.IsCancellationRequested && Image("_capture").GetWidth() == 12 && Image("_generated") == null, "capture replacement cancels old job and resets staging");
            newStatus = _dock.Status; late.Finish(Success()); await old;
            Check(Image("_capture").GetWidth() == 12 && Image("_generated") == null && _dock.Status == newStatus, "late result cannot combine with replacement capture/mask");

            foreach (string kind in new[] { "empty", "malformed", "error", "exception", "cancel", "null" })
            {
                _next = new Fake(); Task bad = RunJob(); await _next.Entered.Task.WaitAsync(TimeSpan.FromSeconds(10));
                if (kind == "exception") _next.Fail(new IOException("raw provider message must not reach screen"));
                else if (kind == "cancel") _next.Cancel();
                else _next.Finish(kind switch
                {
                    "malformed" => new ImageResult { Pngs = new List<byte[]> { new byte[] { 1, 2, 3 } } },
                    "error" => new ImageResult { Error = "raw provider message must not reach screen" },
                    "null" => null,
                    _ => new ImageResult(),
                });
                await bad;
                Check(!Busy && !((Button)Get(_dock, "_generate")).Disabled && !_dock.Status.Contains("raw provider"), kind + " releases busy/button and sanitizes error");
                Check(_dock.Status.Contains(kind == "cancel" ? "cancelled" : "launchers/comfy/doctor.bat"), kind + " actionable feedback");
                _next = new Fake(); Task retry = RunJob(); await _next.Entered.Task.WaitAsync(TimeSpan.FromSeconds(10)); _next.Finish(Success()); await retry;
                Check(Image("_generated")?.GetWidth() == 12 && !Busy, kind + " successful retry uses current capture dimensions");
            }
            Toggle(false); _dock.Shutdown(); _dock.Shutdown(); _dock.QueueFree(); await Frame(2);
            int beforeDiscovery = _discoveries, beforeFactory = _factories;
            _dock = NewDock(); await Frame(); Call(_dock, "Select", "saved");
            Check(_discoveries == beforeDiscovery && _factories == beforeFactory && ((LineEdit)Get(_dock, "_prompt")).Text == "note retained", "cold off component reopens saved manual metadata without discovery/provider");
            _dock.Shutdown(); _dock.QueueFree(); await Frame(2);
            Apply(true); _dock = NewDock(); await Frame();
            Check(_discoveries == beforeDiscovery + 1 && Image("_capture") == null, "initial on component discovers once with clean owned state");
            Call(_dock, "ReplaceCapture", Solid(Colors.Black));
            late = _next = new Fake(); old = RunJob(); await late.Entered.Task.WaitAsync(TimeSpan.FromSeconds(10));
            string shutdownStatus = _dock.Status;
            _dock.Shutdown(); _dock.Shutdown();
            Check(late.Token.IsCancellationRequested && new[] { "_capture", "_generated", "_decalRaw", "_decal", "_mask" }.All(n => Get(_dock, n) == null), "double Shutdown cancels and clears all five owned Images");
            Check(new[] { "_preview", "_extractView", "_genView", "_maskView" }.All(n => ((TextureRect)Get(_dock, n)).Texture == null), "Shutdown clears all preview texture references");
            _dock.QueueFree(); await Frame(2); late.Finish(Success()); await old;
            Check(late.Disposed, "pending late success after QueueFree completes without Godot access and disposes provider");
            _dock = NewDock(); await Frame(); Call(_dock, "ReplaceCapture", Solid(Colors.Black));
            late = _next = new Fake(); old = RunJob(); await late.Entered.Task.WaitAsync(TimeSpan.FromSeconds(10));
            _dock.QueueFree(); await Frame(2);
            Check(late.Token.IsCancellationRequested && new[] { "_capture", "_generated", "_decalRaw", "_decal", "_mask" }.All(n => Get(_dock, n) == null), "defensive ExitTree cancels job and clears images without explicit Shutdown");
            late.Fail(new IOException("late teardown failure")); await old;
            Check(late.Disposed, "late error after defensive QueueFree consumed and provider disposed");
            _dock = null;
        }
        catch (Exception ex) { failure = ex.ToString(); GD.PushError(failure); }
        finally
        {
            if (GodotObject.IsInstanceValid(_dock)) { _dock.Shutdown(); _dock.QueueFree(); }
            Environment.SetEnvironmentVariable("GUO_LAYER_DIR", inherited); Apply(original);
            string assemblyFile = Path.Combine(ProjectSettings.GlobalizePath("res://"), ".godot/mono/temp/bin/Debug/GUO.dll");
            var report = new { ok = failure == null, passed = _passed, failure,
                assembly = assemblyFile, loaded_mvid = typeof(LayersDock).Assembly.ManifestModule.ModuleVersionId,
                assembly_sha256 = Convert.ToHexString(SHA256.HashData(File.ReadAllBytes(assemblyFile))),
                discoveries = _discoveries, factories = _factories, real_provider_calls = 0,
                limits = new[] { "component proof only; no plugin preference routing or actual WorldView capture claim", "no actual AI provider/service invoked" } };
            File.WriteAllText(Path.Combine(_out, "result.json"), JsonSerializer.Serialize(report, new JsonSerializerOptions { WriteIndented = true }));
            GetTree().Quit(failure == null ? 0 : 1);
        }
    }
    private static IEnumerable<Node> Walk(Node n)
    {
        yield return n;
        foreach (Node child in n.GetChildren()) foreach (Node next in Walk(child)) yield return next;
    }
    private sealed class Fake : IImageProvider, IDisposable
    {
        public string Id => "synthetic"; public string Name => "Synthetic"; public bool NeedsKey => false;
        public TaskCompletionSource<bool> Entered = new(TaskCreationOptions.RunContinuationsAsynchronously);
        private readonly TaskCompletionSource<ImageResult> _finish = new(TaskCreationOptions.RunContinuationsAsynchronously);
        private readonly bool _cooperative;
        public CancellationToken Token; public ImageRequest Request; public int Thread; public bool Disposed;
        public Fake(bool cooperative = false) => _cooperative = cooperative;
        public async Task<ImageResult> RunAsync(ImageRequest req, Action<double> progress, CancellationToken ct)
        {
            Request = req; Token = ct; Thread = Environment.CurrentManagedThreadId;
            using var reg = _cooperative ? ct.Register(() => _finish.TrySetCanceled(ct)) : default;
            Entered.TrySetResult(true); return await _finish.Task;
        }
        public void Finish(ImageResult r) => _finish.TrySetResult(r);
        public void Fail(Exception e) => _finish.TrySetException(e);
        public void Cancel() => _finish.TrySetCanceled();
        public void Dispose() => Disposed = true;
    }
}
#endif
