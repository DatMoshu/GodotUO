// SPDX-License-Identifier: BSD-2-Clause
// Task 1155: frozen, offline, common GPU benchmark harness for the render competition.
// Labs (L4) fixture: compiled only with -p:GuoLabs=true Debug; never in an export.
// Run it with tools/competition_bench/run.py (see README.md in this folder).
#if GUO_LABS && DEBUG
using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.Linq;
using System.Reflection;
using System.Security.Cryptography;
using System.Text.Json;
using System.Threading.Tasks;
using Godot;
using GUO.Configuration;
using GUO.Game;
using GUO.Game.Scenes;
using GUO.Network;
using GUO.Renderer.DepthWorld;
using GUO.Renderer.SpeedMode.Shadows;
using Environment = System.Environment;

namespace GUO.Labs.CompetitionBench;

/// <summary>
/// Offline bootstrap (isolated workspace, settings, profile; no shard, no account),
/// a fixed scene, fixed clock and camera, then either a timed run or one still.
/// </summary>
public partial class CompetitionBenchProbe : Node
{
    private const BindingFlags Private = BindingFlags.Instance | BindingFlags.NonPublic;
    private const int Width = 1920, Height = 1080;
    private enum Phase { Boot, Warmup, Sample, Rewarm, Done }

    private string _root, _label, _scene, _time, _variant, _mode, _combo, _dataDir, _stillsDir, _resultPath;
    private int _warmupFrames = 1200, _sampleFrames = 3600, _rewarmFrames = 300;
    private double _timeoutS = 1800;
    private int _phaseFrames;
    private Stopwatch _repeatWall;
    private string _gpuStateBefore;
    private Dictionary<string, string> _profileBefore;
    private List<object> _profileDiff = new();
    private string _fixtureBefore;
    private Task<string> _gpuTask;
    private int _repeats = 3;
    private bool _raw;
    private BenchScene _benchScene;
    private BenchTime _benchTime;
    private bool _enhanced;
    private GUO.GameController _game;
    private GameScene _gameScene;
    private Profile _profile;
    private Phase _phase = Phase.Boot;
    private double _phaseStart;
    private readonly Stopwatch _clock = Stopwatch.StartNew();
    private long _lastTimestamp;
    private int _repeat;
    private Rid[] _viewports = Array.Empty<Rid>();
    private List<double> _gpu = new(), _cpu = new(), _frame = new();
    private readonly List<object> _repeatRows = new();
    private readonly List<string> _rawLines = new();
    private object _memoryBefore;
    private long _fallbackBefore, _drawnBefore;
    private int _skip;
    private bool _capturing;
    private long _captureStartFrame = -1;
    private ulong _lastUsedGpuFrame = ulong.MaxValue;
    private int _invalidGpu, _staleGpu;
    private readonly Dictionary<string, int> _invalidWhy = new();
    private List<double> _viewportGpu = new(), _preGpu = new(), _whole = new();
    private long _inside, _passMarkers;
    private bool _finished;
    private double _startupS;

    private static string Env(string name, string fallback = null) =>
        Environment.GetEnvironmentVariable(name) is { Length: > 0 } v ? v : fallback;

    private static T Field<T>(object value, string name) => (T)value.GetType().GetField(name, Private).GetValue(value);

    private static void Require(bool ok, string reason)
    {
        if (!ok) throw new InvalidOperationException(reason);
    }

    public override void _EnterTree()
    {
        // Runs before the child Main: nothing of the owner's is opened for writing.
        try
        {
            _root = Env("GUO_BENCH_OUT");
            Require(!string.IsNullOrWhiteSpace(_root) && Path.IsPathRooted(_root), "external rooted GUO_BENCH_OUT required");
            _root = Path.GetFullPath(_root);
            string project = Path.GetFullPath(ProjectSettings.GlobalizePath("res://"));
            string repo = Path.GetFullPath(Path.Combine(project, "..", ".."));
            Require(!_root.StartsWith(repo, StringComparison.OrdinalIgnoreCase), "GUO_BENCH_OUT must stay outside the repository");
            _label = Env("GUO_BENCH_LABEL");
            Require(_label != null && _label.All(c => char.IsLetterOrDigit(c) || c is '-' or '_' or '.'), "GUO_BENCH_LABEL required (letters, digits, - _ .)");
            _scene = Env("GUO_BENCH_SCENE", "town-roofs");
            _time = Env("GUO_BENCH_TIME", "noon");
            _variant = Env("GUO_BENCH_VARIANT", "enhanced-off");
            _mode = Env("GUO_BENCH_MODE", "timed");
            Require(_variant is "classic" or "enhanced-off" or "terrain" or "terrain-cloud-wind", "unknown GUO_BENCH_VARIANT " + _variant);
            Require(_mode is "timed" or "capture", "unknown GUO_BENCH_MODE " + _mode);
            _benchScene = CompetitionBenchScenes.Scene(_scene);
            _benchTime = CompetitionBenchScenes.Time(_time);
            var inv = System.Globalization.CultureInfo.InvariantCulture;
            _warmupFrames = int.Parse(Env("GUO_BENCH_WARMUP_FRAMES", "1200"));
            _sampleFrames = int.Parse(Env("GUO_BENCH_SAMPLE_FRAMES", "3600"));
            _rewarmFrames = int.Parse(Env("GUO_BENCH_REWARM_FRAMES", "300"));
            _timeoutS = double.Parse(Env("GUO_BENCH_TIMEOUT_S", "1800"), inv);
            _repeats = int.Parse(Env("GUO_BENCH_REPEATS", "3"));
            Require(_warmupFrames >= 0 && _sampleFrames > 0 && _rewarmFrames >= 0 && _repeats >= 1, "frame counts: warmup >= 0, sample > 0, repeats >= 1");
            _raw = Env("GUO_BENCH_RAW") == "1";
            CompetitionBenchClock.Active = true; // from process start: identical sim sequence every run
            _combo = _scene + "-" + _time + "-" + _variant;
            _dataDir = Path.Combine(_root, "data", _label, _combo);
            _stillsDir = Path.Combine(_root, "stills");
            _resultPath = Path.Combine(_dataDir, _mode == "capture" ? "capture.json" : "bench.json");
            Require(!File.Exists(_resultPath), "result already exists for this label; refusing to overwrite " + _resultPath);
            string still = Path.Combine(_stillsDir, _label + "-" + _combo + ".png");
            Require(_mode != "capture" || !File.Exists(still), "still already exists; refusing to overwrite " + still);
            string scratch = Path.Combine(_dataDir, "fixture");
            Directory.CreateDirectory(scratch); Directory.CreateDirectory(_stillsDir);
            GUO.Workspace.Workspace.RootOverride = Path.Combine(scratch, "workspace");
            Environment.SetEnvironmentVariable("UO_WORKSPACE_DIR", GUO.Workspace.Workspace.RootOverride);
            Environment.SetEnvironmentVariable("UO_CACHE_DIR", Path.Combine(_root, "cache"));
            Settings.CustomSettingsFilepath = Path.Combine(scratch, "settings.json");
            File.WriteAllText(Settings.CustomSettingsFilepath, JsonSerializer.Serialize(new {
                autologin = false, saveaccount = false, login_music = false, is_win_maximized = false,
                profilespath = Path.Combine(scratch, "profiles"), plugins = Array.Empty<string>() }));
        }
        catch (Exception error) { Fail(error.Message); }
    }

    private void Fail(string reason, int code = 1)
    {
        if (_finished) return;
        _finished = true; _phase = Phase.Done;
        GD.PrintErr("[competition-bench] FAIL " + reason);
        Console.Error.WriteLine("[competition-bench] FAIL " + reason);
        GetTree()?.Quit(code);
    }

    /// <summary>Enhanced lighting with every competition addition off (the frozen "enhanced" set).</summary>
    private void ApplyProfile(bool enhanced)
    {
        var p = _profile;
        p.DefaultScale = 1; p.GameWindowFullSize = true; p.TopbarGumpIsDisabled = true;
        p.UseCircleOfTransparency = false; p.NoColorObjectsOutOfRange = false; p.EnableDeathScreen = false;
        p.ReduceFPSWhenInactive = false; p.UseCustomLightLevel = false; p.RealtimeLights = false;
        p.LightingSpeedMode = enhanced;
        p.SpeedModeSunShadows = true; p.SpeedModeLampShadows = 0; p.SpeedModeSoften = 0;
        p.SpeedModeWindowLight = false; p.SpeedModeWeather = false; p.SpeedModeFog = false;
        p.SpeedModeMorningHaze = 0; p.SpeedModeReflections = false; p.SpeedModeNightFloor = 3;
        p.WaterStyle = "classic"; p.WindStrength = 0;
        p.DrawRoofs = true; // town roofs must be visible
        p.ShadowsEnabled = false; p.ShadowsStatics = false;
    }

    public override async void _Ready()
    {
        if (_finished) return;
        try
        {
            string[] args = OS.GetCmdlineUserArgs();
            Require(!args.Any(a => a is "--account" or "--password" or "--auto-login" or "--autologin" or "--shard-session" or "--window-size"),
                "isolated bench has no account/shard/window overrides");
            int cacheAt = Array.IndexOf(args, "--cache-dir");
            Require(cacheAt >= 0 && cacheAt + 1 < args.Length && Path.GetFullPath(args[cacheAt + 1]).Equals(
                Path.Combine(_root, "cache"), StringComparison.OrdinalIgnoreCase), "explicit --cache-dir <GUO_BENCH_OUT>/cache required");
            for (int i = 0; Client.Game?.Scene is not LoginScene; i++)
            {
                if (i > 3600) throw new Exception("login timeout");
                await ToSignal(GetTree(), SceneTree.SignalName.ProcessFrame);
            }
            _game = Client.Game; var world = _game.UO.World;
            Require(!NetClient.Socket.IsConnected, "disconnected bootstrap");
            var setup = typeof(NetClient).GetMethod("SetupSocket", Private);
            setup.Invoke(NetClient.Socket, new[] { Enum.Parse(setup.GetParameters()[0].ParameterType, "TcpSocket") });
            string scratch = Path.Combine(_dataDir, "fixture");
            Settings.GlobalSettings.ProfilesPath = Path.Combine(scratch, "profiles");
            typeof(ProfileManager).GetField("_rootPath", BindingFlags.Static | BindingFlags.NonPublic)?.SetValue(null, null);
            ProfileManager.Load("Offline", "competition-bench", "competition-bench");
            _profile = ProfileManager.CurrentProfile;
            Require(Path.GetFullPath(ProfileManager.ProfilePath).StartsWith(_root, StringComparison.OrdinalIgnoreCase), "isolated profile");

            if (Env("GUO_BENCH_DUMP_PROFILE_MEMBERS") is { } dump)
            {
                File.WriteAllLines(dump, ProfileMemberNames()); // regenerates CompetitionBenchFrozen.ProfileMembers
                _finished = true; GetTree().Quit(0); return;
            }
            CheckAllowedFields();
            _fixtureBefore = FixtureSignature();
            _enhanced = _variant != "classic";
            Require(RenderingServer.GetCurrentRenderingMethod() == "forward_plus" && RenderingServer.GetCurrentRenderingDriverName() == "vulkan", "requires forward_plus on vulkan");
            ApplyProfile(_enhanced);
            if (_variant is "terrain" or "terrain-cloud-wind")
            {
                _profileBefore = SnapProfile(_profile);
                if (!CompetitionVariants.Apply(_profile, _variant))
                {
                    WriteUnsupported();
                    return;
                }
            }

            world.CreatePlayer(1); world.MapIndex = 0; world.Player.Graphic = 0x190;
            world.Map.GetMapZ((ushort)_benchScene.X, (ushort)_benchScene.Y, out sbyte ground, out _);
            sbyte z = (sbyte)(_benchScene.Z ?? ground);
            world.Player.SetInWorldTile((ushort)_benchScene.X, (ushort)_benchScene.Y, z);
            PinClock();
            _gameScene = new GameScene(world); _game.SetScene(_gameScene);
            _game.SetWindowSize(Width, Height); _gameScene.Camera.Zoom = 1;

            // Uncapped real frame loop: vsync off, no cap, no throttle in GameController.Update.
            _game.SetRefreshRate(Constants.MAX_FPS);
            DisplayServer.WindowSetVsyncMode(DisplayServer.VSyncMode.Disabled);
            Engine.MaxFps = 0;
            var interval = Field<float[]>(_game, "_intervalFixedUpdate");
            interval[0] = interval[1] = 0.001f;

            for (int i = 0; i < 12; i++) await ToSignal(GetTree(), SceneTree.SignalName.ProcessFrame);
            _startupS = _clock.Elapsed.TotalSeconds; _phase = Phase.Warmup; _phaseStart = _clock.Elapsed.TotalSeconds; _lastTimestamp = Stopwatch.GetTimestamp();
        }
        catch (Exception error) { Fail(error.ToString()); }
    }

    /// <summary>Clock and light level are pinned every frame: no drift between runs.</summary>
    private void PinClock()
    {
        var world = _game.UO.World;
        world.ShardClock.Set((byte)_benchTime.Hour, (byte)_benchTime.Minute, 0, Time.Ticks);
        world.Light.Overall = (byte)_benchTime.ClassicOverall; world.Light.Personal = 0;
    }

    private void WriteUnsupported()
    {
        File.WriteAllText(_resultPath, JsonSerializer.Serialize(new {
            status = "unsupported", variant = "unsupported", requested_variant = _variant, scene = _scene, time = _time,
            label = _label, mode = _mode, build = "Debug GuoLabs" }, Indented));
        GD.Print("[competition-bench] UNSUPPORTED variant " + _variant + " has no implementation in CompetitionVariants");
        _finished = true; _phase = Phase.Done;
        GetTree().Quit(0);
    }

    // ---- Profile containment (the hook may only change what it declares, and only NEW members) ----
    private static string Norm(string name) =>
        name.StartsWith("<", StringComparison.Ordinal) && name.Contains(">k__BackingField") ? name.Substring(1, name.IndexOf('>') - 1) : name;

    private static bool IsSimple(Type t) => t.IsPrimitive || t.IsEnum || t == typeof(string) || t == typeof(decimal);

    private static string Text(object value)
    {
        if (value == null) return "null";
        if (value is string text) return text;
        if (value is IFormattable f) return f.ToString(null, System.Globalization.CultureInfo.InvariantCulture);
        return value.ToString();
    }

    /// <summary>Every instance field (public and private, base types included), recursively for nested
    /// value types and GUO classes (depth 3); collections and Godot or system objects by their text.</summary>
    internal static Dictionary<string, string> SnapProfile(object root)
    {
        var result = new Dictionary<string, string>();
        var seen = new HashSet<object>(ReferenceEqualityComparer.Instance);
        void Record(string key, object value, int depth)
        {
            if (value == null || IsSimple(value.GetType())) { result[key] = Text(value); return; }
            var type = value.GetType();
            if (value is System.Collections.IEnumerable list)
            {
                result[key] = "[" + string.Join(",", list.Cast<object>().Select(x => x != null && IsSimple(x.GetType()) ? Text(x) : x?.GetType().Name ?? "null")) + "]";
                return;
            }
            bool own = type.IsValueType || (type.Namespace?.StartsWith("GUO", StringComparison.Ordinal) ?? false);
            if (own && depth < 3 && (type.IsValueType || seen.Add(value))) { Walk(value, key, depth + 1); return; }
            result[key] = Text(value);
        }
        void Walk(object obj, string path, int depth)
        {
            for (var t = obj.GetType(); t != null && t != typeof(object); t = t.BaseType)
                foreach (var f in t.GetFields(BindingFlags.Instance | BindingFlags.Public | BindingFlags.NonPublic | BindingFlags.DeclaredOnly))
                {
                    string name = Norm(f.Name);
                    string key = path.Length == 0 ? name : path + "." + name;
                    try { Record(key, f.GetValue(obj), depth); } catch (Exception e) { result[key] = "<" + e.GetType().Name + ">"; }
                }
        }
        Walk(root, "", 0);
        return result;
    }

    /// <summary>Names of every instance member of Profile (fields, including backing fields normalised, and properties).</summary>
    internal static string[] ProfileMemberNames()
    {
        var names = new SortedSet<string>(StringComparer.Ordinal);
        for (var t = typeof(Profile); t != null && t != typeof(object); t = t.BaseType)
        {
            foreach (var f in t.GetFields(BindingFlags.Instance | BindingFlags.Public | BindingFlags.NonPublic | BindingFlags.DeclaredOnly)) names.Add(Norm(f.Name));
            foreach (var p in t.GetProperties(BindingFlags.Instance | BindingFlags.Public | BindingFlags.NonPublic | BindingFlags.DeclaredOnly)) names.Add(p.Name);
        }
        return names.ToArray();
    }

    private static List<object> DiffProfile(Dictionary<string, string> before, Dictionary<string, string> after, out List<string> names)
    {
        names = new List<string>(); var rows = new List<object>();
        foreach (var key in before.Keys.Union(after.Keys).OrderBy(k => k, StringComparer.Ordinal))
        {
            before.TryGetValue(key, out var a); after.TryGetValue(key, out var b);
            if (a != b) { names.Add(key.Split('.')[0]); rows.Add(new { field = key, before = a, after = b }); }
        }
        names = names.Distinct().ToList();
        return rows;
    }

    /// <summary>AllowedFields may only name NEW members: anything that already existed at the common commit is refused.</summary>
    private static void CheckAllowedFields()
    {
        var existing = CompetitionBenchFrozen.ProfileMembers.ToHashSet(StringComparer.Ordinal);
        var bad = CompetitionVariants.AllowedFields.Where(existing.Contains).OrderBy(x => x).ToList();
        Require(bad.Count == 0, "AllowedFields names members that already exist in Profile at the common commit (only new members may be switched): " + string.Join(",", bad));
    }

    /// <summary>The Profile at sample time may differ from the enhanced-off snapshot only in declared NEW fields.</summary>
    private void CheckContainment(string where)
    {
        if (_profileBefore == null) return; // classic / enhanced-off: no hook ran
        _profileDiff = DiffProfile(_profileBefore, SnapProfile(_profile), out var names);
        var illegal = names.Where(n => !CompetitionVariants.AllowedFields.Contains(n)).ToList();
        Require(illegal.Count == 0, "variant hook changed Profile state it did not declare in AllowedFields (" + where + "): " + string.Join(",", illegal));
    }

    private string FixtureSignature()
    {
        var vp = GetViewport();
        return string.Join(";", "scaling3d=" + vp.Scaling3DMode, "scale=" + vp.Scaling3DScale.ToString(System.Globalization.CultureInfo.InvariantCulture),
            "msaa3d=" + vp.Msaa3D, "msaa2d=" + vp.Msaa2D, "ssaa=" + vp.ScreenSpaceAA, "taa=" + vp.UseTaa,
            "fsr=" + vp.FsrSharpness.ToString(System.Globalization.CultureInfo.InvariantCulture));
    }

    /// <summary>Everything the frozen contract fixes, re-asserted at sample start and end.</summary>
    private void CheckFixture(string where)
    {
        var win = DisplayServer.WindowGetSize();
        Require(RenderingServer.GetCurrentRenderingMethod() == "forward_plus", "renderer must be forward_plus, got " + RenderingServer.GetCurrentRenderingMethod());
        Require(RenderingServer.GetCurrentRenderingDriverName() == "vulkan", "driver must be vulkan, got " + RenderingServer.GetCurrentRenderingDriverName());
        Require(win.X == Width && win.Y == Height, "window must be 1920x1080, got " + win.X + "x" + win.Y + " (" + where + ")");
        Require(DisplayServer.WindowGetVsyncMode() == DisplayServer.VSyncMode.Disabled, "vsync must be off (" + where + ")");
        Require(Engine.MaxFps == 0, "Engine.MaxFps must be 0 (" + where + ")");
        Require(Math.Abs(_gameScene.Camera.Zoom - 1f) < 1e-6f, "camera zoom must be 1 (" + where + ")");
        Require(Math.Abs(Engine.TimeScale - 1.0) < 1e-9, "Engine.TimeScale must be 1 (" + where + ")");
        int physics = ProjectSettings.GetSetting("physics/common/physics_ticks_per_second", 60).AsInt32();
        Require(Engine.PhysicsTicksPerSecond == physics, "Engine.PhysicsTicksPerSecond must equal the project value " + physics + " (" + where + ")");
        Require(_fixtureBefore == null || FixtureSignature() == _fixtureBefore,
            "root viewport scaling/MSAA/AA changed since before the variant hook (" + where + "): " + FixtureSignature() + " vs " + _fixtureBefore);
    }

    /// <summary>nvidia-smi sample with a hard timeout (the process is killed on timeout); "unavailable" otherwise.</summary>
    private static string GpuState()
    {
        try
        {
            var info = new ProcessStartInfo("nvidia-smi", "--query-gpu=clocks.gr,clocks.mem,temperature.gpu,power.draw,pstate --format=csv,noheader,nounits")
            { RedirectStandardOutput = true, UseShellExecute = false, CreateNoWindow = true };
            using var p = Process.Start(info);
            var read = p.StandardOutput.ReadToEndAsync();
            if (!p.WaitForExit(3000)) { try { p.Kill(true); } catch { } return "unavailable"; }
            string text = read.Result.Trim();
            return p.ExitCode == 0 && text.Length > 0 ? text : "unavailable";
        }
        catch { return "unavailable"; }
    }

    private static double? GraphicsClock(string state) =>
        state != null && state != "unavailable" && double.TryParse(state.Split(',')[0].Trim(), System.Globalization.NumberStyles.Float,
            System.Globalization.CultureInfo.InvariantCulture, out var mhz) ? mhz : null;

    public override void _Process(double delta)
    {
        if (_phase == Phase.Boot || _phase == Phase.Done) return;
        try
        {
            PinClock();
            long now = Stopwatch.GetTimestamp();
            double frameMs = (now - _lastTimestamp) * 1000d / Stopwatch.Frequency; _lastTimestamp = now;
            if (_clock.Elapsed.TotalSeconds > _timeoutS) { Fail("TIMEOUT wall time exceeded " + _timeoutS + " s", 98); return; }
            _phaseFrames++;
            switch (_phase)
            {
                case Phase.Warmup:
                case Phase.Rewarm:
                    int target = _phase == Phase.Warmup ? _warmupFrames : _rewarmFrames;
                    if (_phaseFrames == Math.Max(1, target - 60)) _gpuTask = Task.Run(GpuState); // asynchronous, inside warmup
                    if (_phaseFrames < target) return;
                    if (_mode == "capture")
                    {
                        if (_captureStartFrame < 0) { CompetitionBenchClock.RebaseTo(CompetitionBenchClock.SampleBaseSeconds); _captureStartFrame = CompetitionBenchClock.Frames; }
                        else if (!_capturing && CompetitionBenchClock.Frames - _captureStartFrame >= CompetitionBenchClock.CaptureFrame) { _capturing = true; Capture(); }
                        return;
                    }
                    BeginSample();
                    break;
                case Phase.Sample:
                    if (_skip > 0) { _skip--; return; }
                    _frame.Add(frameMs);
                    double cpu = 0;
                    foreach (var rid in _viewports) cpu += RenderingServer.ViewportGetMeasuredRenderTimeCpu(rid);
                    _cpu.Add(cpu);
                    if (_frame.Count % 300 == 0) CheckViewports();
                    if (_frame.Count >= _sampleFrames) EndSample();
                    break;
            }
        }
        catch (Exception error) { Fail(error.ToString()); }
    }

    private Rid[] ListViewports()
    {
        var list = new List<Rid> { GetTree().Root.GetViewportRid() };
        foreach (var node in GetTree().Root.FindChildren("*", "SubViewport", true, false))
            list.Add(((SubViewport)node).GetViewportRid());
        return list.ToArray();
    }

    private void CheckViewports()
    {
        var now = ListViewports();
        Require(now.SequenceEqual(_viewports), "the set of rendering viewports changed during the sample (" + _viewports.Length + " -> " + now.Length + "); GPU timing would be incomplete");
    }

    private void CheckAdmission(string where)
    {
        Require(!_gameScene.UseRealtimeLights, "realtime lights active (" + where + ")");
        Require(_gameScene.UseSpeedLights == _enhanced,
            (_enhanced ? "enhanced lighting requested but not admitted" : "classic lighting requested but enhanced active") + " (" + where + ")");
        Require(ProfileManager.CurrentProfile.DrawRoofs, "roofs not drawn (" + where + ")");
        Require(!NetClient.Socket.IsConnected, "bench connected to a network");
        if (_enhanced)
        {
            var shadows = Field<SpeedShadowInputs>(_gameScene, "_speedShadows");
            Require(shadows?.StructuralSun?.Ready == true, "sun shadows not admitted (" + where + ")");
            Require(GUO.Renderer.SpeedMode.SpeedModeBindings.Current.LightTarget != null, "no enhanced light target published (" + where + ")");
        }
    }

    private object MemorySnapshot()
    {
        using var process = Process.GetCurrentProcess(); process.Refresh();
        return new {
            processWorkingSetBytes = process.WorkingSet64, processPrivateBytes = process.PrivateMemorySize64,
            godotStaticMemoryBytes = OS.GetStaticMemoryUsage(), gcTotalMemoryBytes = GC.GetTotalMemory(false),
            gcCollections = new[] { GC.CollectionCount(0), GC.CollectionCount(1), GC.CollectionCount(2) },
            videoMemUsedBytes = RenderingServer.GetRenderingInfo(RenderingServer.RenderingInfo.VideoMemUsed),
            textureMemUsedBytes = RenderingServer.GetRenderingInfo(RenderingServer.RenderingInfo.TextureMemUsed),
            bufferMemUsedBytes = RenderingServer.GetRenderingInfo(RenderingServer.RenderingInfo.BufferMemUsed),
            monitorRenderVideoMemUsed = Performance.GetMonitor(Performance.Monitor.RenderVideoMemUsed),
        };
    }

    private void BeginSample()
    {
        CheckAdmission("sample start"); CheckFixture("sample start"); CheckContainment("sample start");
        _viewports = ListViewports();
        foreach (var rid in _viewports) RenderingServer.ViewportSetMeasureRenderTime(rid, true);
        _gpuStateBefore = _gpuTask != null && _gpuTask.Wait(4000) ? _gpuTask.Result : "unavailable"; _gpuTask = null;
        _cpu = new List<double>(1 << 16); _frame = new List<double>(1 << 16);
        _memoryBefore = MemorySnapshot();
        _fallbackBefore = OrderedWorldCapture.FramesFallback; _drawnBefore = OrderedWorldCapture.FramesDrawn;
        CompetitionBenchClock.RebaseTo(CompetitionBenchClock.SampleBaseSeconds); // identical sim phase each repeat
        BenchGpuMarkers.Viewports = _viewports; BenchGpuMarkers.Reset(); BenchGpuMarkers.Enabled = true;
        _repeatWall = Stopwatch.StartNew();
        _skip = 10; _phase = Phase.Sample; _phaseFrames = 0;
    }

    private void EndSample()
    {
        BenchGpuMarkers.Enabled = false;
        double wall = _repeatWall.Elapsed.TotalSeconds;
        CheckAdmission("sample end"); CheckFixture("sample end"); CheckViewports(); CheckContainment("sample end");
        long fallback = OrderedWorldCapture.FramesFallback - _fallbackBefore, drawn = OrderedWorldCapture.FramesDrawn - _drawnBefore;
        Require(fallback == 0, "Depth World fallback frames during the sample: " + fallback);
        Require(drawn > 0, "no Depth World frames drawn during the sample");
        string after = GpuState(); // after the timed loop, hard timeout
        double? c0 = GraphicsClock(_gpuStateBefore), c1 = GraphicsClock(after);
        bool drift = c0 is > 0 && c1 != null && Math.Abs(c1.Value - c0.Value) / c0.Value > 0.15;
        var resolved = BenchGpuMarkers.Snapshot();
        var valid = resolved.Where(r => r.Valid).ToArray();
        var why = resolved.Where(r => !r.Valid).GroupBy(r => r.Why ?? "?").ToDictionary(g => g.Key, g => g.Count());
        long passMarkers = valid.Sum(r => (long)r.PassMarkers), inside = valid.Sum(r => (long)r.PassMarkersInside);
        var pre = valid.Select(r => r.PreDrawGpuMs).ToList(); var vp = valid.Select(r => r.ViewportGpuMs).ToList();
        var total = valid.Select(r => r.PreDrawGpuMs + r.ViewportGpuMs).ToList(); var whole = valid.Select(r => r.WholeFrameGpuMs).ToList();
        var ratios = valid.Where(r => r.WholeFrameGpuMs > 0).Select(r => (r.PreDrawGpuMs + r.ViewportGpuMs) / r.WholeFrameGpuMs).OrderBy(x => x).ToArray();
        double ratioMedian = ratios.Length == 0 ? 0 : ratios[ratios.Length / 2];
        var gaps = valid.Select(r => r.WholeFrameGpuMs - (r.PreDrawGpuMs + r.ViewportGpuMs)).OrderBy(x => x).ToArray();
        double gapMedian = gaps.Length == 0 ? double.MaxValue : gaps[gaps.Length / 2];
        double validFraction = resolved.Length == 0 ? 0 : (double)valid.Length / resolved.Length;
        double resolvedFraction = (double)resolved.Length / _frame.Count;
        bool validOk = validFraction >= 0.99, insideOk = inside == passMarkers && passMarkers > 0,
             coverageOk = ratioMedian >= 0.93 && ratioMedian <= 1.07 && gapMedian <= 0.05, resolvedOk = resolvedFraction >= 0.95;
        _repeatRows.Add(new {
            repeat = _repeat, frames = _frame.Count, wall_s = wall, depthWorldFramesDrawn = drawn, depthWorldFallbackFrames = fallback,
            gpu_ms = BenchStats.Of(total), gpu_predraw_ms = BenchStats.Of(pre), gpu_viewport_ms = BenchStats.Of(vp),
            gpu_whole_frame_ms = BenchStats.Of(whole),
            gpu_coverage = new { resolvedFrames = resolved.Length, validFrames = valid.Length, invalidFrames = resolved.Length - valid.Length,
                invalidReasons = why, stampedFrames = BenchGpuMarkers.Stamped, unavailable = BenchGpuMarkers.Unavailable,
                simFrameIndex = CompetitionBenchClock.Frames,
                gates = new { validFraction, validOk, passMarkers, passMarkersInside = inside, insideOk,
                    totalToWholeMedian = ratioMedian, wholeMinusTotalMedianMs = gapMedian, coverageOk, resolvedFraction, resolvedOk } },
            gpu_state_before = _gpuStateBefore, gpu_state_after = after, gpu_clock_drift_flag = drift,
            cpu_ms = BenchStats.Of(_cpu), frame_ms = BenchStats.Of(_frame),
            memoryBefore = _memoryBefore, memoryAfter = MemorySnapshot() });
        if (_raw)
            foreach (var r in valid) _rawLines.Add(string.Join(",", _repeat, r.Frame,
                r.PreDrawGpuMs.ToString("F4", System.Globalization.CultureInfo.InvariantCulture),
                r.ViewportGpuMs.ToString("F4", System.Globalization.CultureInfo.InvariantCulture),
                r.WholeFrameGpuMs.ToString("F4", System.Globalization.CultureInfo.InvariantCulture)));
        Require(validOk, "GPU validity gate: only " + validFraction.ToString("P1") + " of resolved frames valid (" + string.Join("; ", why.Select(p => p.Key + " x" + p.Value)) + ")");
        Require(insideOk, "GPU coverage gate: pass markers inside predraw span " + inside + "/" + passMarkers);
        Require(coverageOk, "GPU coverage gate: predraw+viewport vs whole-frame span ratio " + ratioMedian.ToString("F3") + ", whole minus total median " + gapMedian.ToString("F3") + " ms (limit 0.05)");
        Require(resolvedOk, "GPU timestamps resolved for only " + resolvedFraction.ToString("P1") + " of frames");
        _repeat++;
        if (_repeat >= _repeats) { WriteBench(); return; }
        _phase = Phase.Rewarm; _phaseFrames = 0;
    }

    private object EnvironmentInfo()
    {
        var win = DisplayServer.WindowGetSize();
        var vp = GetViewport().GetVisibleRect().Size;
        return new {
            adapter = RenderingServer.GetVideoAdapterName(), vendor = RenderingServer.GetVideoAdapterVendor(),
            adapterApiVersion = RenderingServer.GetVideoAdapterApiVersion(),
            driver = RenderingServer.GetCurrentRenderingDriverName(), method = RenderingServer.GetCurrentRenderingMethod(),
            engine = Engine.GetVersionInfo()["string"].AsString(),
            dotnet = System.Runtime.InteropServices.RuntimeInformation.FrameworkDescription,
            requestedWindow = new[] { Width, Height }, windowSize = new[] { win.X, win.Y },
            viewportSize = new[] { (int)vp.X, (int)vp.Y },
            vsync = DisplayServer.WindowGetVsyncMode().ToString(), maxFps = Engine.MaxFps, build = "Debug GuoLabs" };
    }

    private object Fixture() => new {
        scene = _scene, x = _benchScene.X, y = _benchScene.Y, z = (int)_game.UO.World.Player.Z, time = _time,
        clock = _benchTime.Hour.ToString("D2") + ":" + _benchTime.Minute.ToString("D2"),
        classicOverall = _benchTime.ClassicOverall, classicOverallNote = "used only for classic-variant still parity, never for scoring",
        variant = _variant, enhanced = _enhanced, zoom = _gameScene.Camera.Zoom,
        settings = new { _profile.LightingSpeedMode, _profile.SpeedModeSunShadows, _profile.SpeedModeLampShadows,
            _profile.SpeedModeSoften, _profile.SpeedModeWindowLight, _profile.SpeedModeWeather, _profile.SpeedModeFog,
            _profile.SpeedModeMorningHaze, _profile.SpeedModeReflections, _profile.SpeedModeNightFloor,
            _profile.WindStrength, _profile.WaterStyle, _profile.DrawRoofs, _profile.RealtimeLights } };

    private static readonly JsonSerializerOptions Indented = new() { WriteIndented = true };

    private void WriteBench()
    {
        File.WriteAllText(_resultPath, JsonSerializer.Serialize(new {
            status = "pass", label = _label, mode = "timed", variant = _variant, scene = _scene, time = _time,
            startup_s = _startupS, process_wall_s = _clock.Elapsed.TotalSeconds,
            warmup_frames = _warmupFrames, sample_frames = _sampleFrames, rewarm_frames = _rewarmFrames, repeats = _repeats,
            profile_diff = _profileDiff, allowed_fields = CompetitionVariants.AllowedFields.OrderBy(x => x).ToArray(),
            gpuMeasuredViewports = _viewports.Length, fixture = Fixture(), environment = EnvironmentInfo(),
            repeatResults = _repeatRows,
            note = "gpu_ms = predraw RD span (OrderedWorldCapture.RenderThread begin/end raw Vulkan ns) + viewport GPU, both read on the render thread at one resolution; invalid frames (missing pair, viewport <= 0) are counted, never zero. Warmup and sample are fixed counts of simulation frames (1/60 s each); wall time is only an output and a timeout.",
            simulation = new { fixedDeltaS = CompetitionBenchClock.FixedDelta, baseSeconds = CompetitionBenchClock.SampleBaseSeconds },
            build = "Debug GuoLabs",
        }, Indented));
        if (_raw) File.WriteAllLines(Path.Combine(_dataDir, "raw_gpu.csv"), _rawLines.Prepend("repeat,resolved_frame,predraw_gpu_ms,viewport_gpu_ms,whole_gpu_ms"));
        GD.Print("[competition-bench] PASS timed " + _combo + " repeats=" + _repeats + " result=" + _resultPath);
        _finished = true; _phase = Phase.Done;
        GetTree().Quit(0);
    }

    private void Capture()
    {
        try
        {
            CheckAdmission("capture"); CheckFixture("capture"); CheckContainment("capture");
            using var image = GetViewport().GetTexture().GetImage();
            string still = Path.Combine(_stillsDir, _label + "-" + _combo + ".png");
            Require(image.SavePng(still) == Error.Ok, "could not save " + still);
            if (image.GetFormat() != Image.Format.Rgba8) image.Convert(Image.Format.Rgba8);
            string hash = Convert.ToHexString(SHA256.HashData(image.GetData())).ToLowerInvariant();
            File.WriteAllText(_resultPath, JsonSerializer.Serialize(new {
                status = "pass", label = _label, mode = "capture", variant = _variant, scene = _scene, time = _time,
                still, image_sha256 = hash, image_size = new[] { image.GetWidth(), image.GetHeight() },
                sim_frame = CompetitionBenchClock.CaptureFrame, build = "Debug GuoLabs", profile_diff = _profileDiff,
                fixture = Fixture(), environment = EnvironmentInfo() }, Indented));
            GD.Print("[competition-bench] PASS capture " + _combo + " sha256=" + hash[..12] + " still=" + still);
            _finished = true; _phase = Phase.Done;
            GetTree().Quit(0);
        }
        catch (Exception error) { Fail(error.ToString()); }
    }
}

/// <summary>Distribution of one metric: count, mean, median, p95, p99, min, max (nearest rank).</summary>
internal static class BenchStats
{
    internal static object Of(List<double> samples)
    {
        if (samples.Count == 0) return new { count = 0 };
        var s = samples.ToArray(); Array.Sort(s);
        double Rank(double p) => s[Math.Clamp((int)Math.Ceiling(p * s.Length) - 1, 0, s.Length - 1)];
        double median = s.Length % 2 == 1 ? s[s.Length / 2] : (s[s.Length / 2 - 1] + s[s.Length / 2]) / 2;
        return new { count = s.Length, mean = s.Average(), median, p95 = Rank(.95), p99 = Rank(.99), min = s[0], max = s[^1] };
    }
}
#endif
