// SPDX-License-Identifier: BSD-2-Clause
// Labs-only offline proof (task 1150): the Glow PostFx preset reads the light that owns the frame.
// Britain 1417,1734, scratch profile, no shard/account. Glow-on vs Classic-preset frames of the same
// settled state; thresholds below are fixed in code before the first run and are never weakened.
using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Reflection;
using System.Text.Json;
using System.Threading.Tasks;
using Godot;
using GUO.Configuration;
using GUO.Game;
using GUO.Game.Data;
using GUO.Game.GameObjects;
using GUO.Game.Scenes;
using GUO.Network;
using GUO.Renderer;
using GUO.Renderer.DepthWorld;
using GUO.Renderer.PostFx;
using GUO.Renderer.SpeedMode;

namespace GUO.Labs.LightingClock;

public partial class GlowLightInputProbe : Node
{
    private const int X = 1417, Y = 1734;
    private const BindingFlags Private = BindingFlags.Instance | BindingFlags.NonPublic;
    private readonly List<object> _rows = new();
    private readonly List<string> _failures = new();
    private readonly List<GameObject> _objs = new();
    private string _root, _stills;
    private GUO.GameController _game;
    private GameScene _scene;
    private Profile _profile;
    private MethodInfo _draw;
    private uint _serial = 0x40010000;
    private int _z, _n;

    private sealed record Img(byte[] Rgba, int W, int H);
    private sealed record Info(bool Suppressed, bool Lit, bool Produced, bool Active, string Fallback);

    // Thresholds (fixed before the first run).
    private const int BoxX0 = 440, BoxX1 = 840, BoxY0 = 200, BoxY1 = 520;
    private const int NightLampMinAdded = 400;     // realtime night: bloom-added px (luma +12) near the lamp, with the fix
    private const int LegacyMaxAdded = 40;         // ... and with the pre-1150 classic input (was ~0)
    private const int ClassicMinAdded = 100;       // classic night: Glow still blooms from the classic target
    private const int NoonMaxAddedPx = 200;        // realtime noon: added px anywhere in the frame
    private const double NoonMaxMeanAdd = 0.5;     // realtime noon: mean added luma (0..255) over the frame

    private const int FrameMaxPx = 30000;          // enhanced: added px anywhere on a frame (a full-screen wash is ~900k)
    private const double FrameMaxMean = 1.0;       // ... and mean added luma
    private const double CentroidMaxPx = 3.0;      // first frame after the lamp moves vs settled
    private const double HalfResMaxMean = 3.0;    // half-res bloom is coarser (measured 1.23 on the first run; a wash is ~58)
    private const int HalfResMinAdded = 150;       // mobile half-resolution tier, lamp box

    public override void _EnterTree()
    {
        _root = System.Environment.GetEnvironmentVariable("GUO_BLOOM_OUT");
        Require(!string.IsNullOrWhiteSpace(_root) && Path.IsPathRooted(_root), "external GUO_BLOOM_OUT required");
        _root = Path.GetFullPath(_root);
        Require(!_root.StartsWith(Path.GetFullPath(ProjectSettings.GlobalizePath("res://")), StringComparison.OrdinalIgnoreCase), "evidence outside project");
        string scratch = Path.Combine(_root, "fixture"); _stills = Path.Combine(_root, "stills");
        Directory.CreateDirectory(scratch); Directory.CreateDirectory(_stills);
        GUO.Workspace.Workspace.RootOverride = Path.Combine(scratch, "workspace");
        System.Environment.SetEnvironmentVariable("UO_WORKSPACE_DIR", GUO.Workspace.Workspace.RootOverride);
        Settings.CustomSettingsFilepath = Path.Combine(scratch, "settings.json");
        File.WriteAllText(Settings.CustomSettingsFilepath, JsonSerializer.Serialize(new {
            autologin = false, saveaccount = false, login_music = false, is_win_maximized = false,
            profilespath = Path.Combine(scratch, "profiles"), plugins = Array.Empty<string>() }));
    }

    private static void Require(bool ok, string reason) { if (!ok) throw new InvalidOperationException(reason); }
    private void Check(bool ok, string reason) { if (!ok) _failures.Add(reason); }
    private async Task Painted() => await ToSignal(RenderingServer.Singleton, RenderingServer.SignalName.FramePostDraw);
    private void Save() => File.WriteAllText(Path.Combine(_root, "glow-light-input-results.json"),
        JsonSerializer.Serialize(new { disconnected = !NetClient.Socket.IsConnected, rows = _rows, failures = _failures },
            new JsonSerializerOptions { WriteIndented = true }));

    private void Mutated() { _scene.UpdateDrawPosition = true; _scene.UpdateMaxDrawZ(true); _scene.Update(); }
    private Info Snap() => new(GuoEffectPolicy.ClassicShadowsSuppressed(_profile), OrderedWorldCapture.LastFrameEnhancedLit,
        GuoEffectPolicy.EnhancedShadowsProduced, SpeedModeDebug.Active, OrderedWorldCapture.LastFallback);

    private async Task<Img> Draw(string name)
    {
        Mutated();
        _scene.Camera.Update(true, 0, GUO.Input.Mouse.Position); _draw.Invoke(_game, null); await Painted();
        using var shown = GetViewport().GetTexture().GetImage();
        if (shown.GetFormat() != Image.Format.Rgba8) shown.Convert(Image.Format.Rgba8);
        if (name != null) shown.SavePng(Path.Combine(_stills, $"{++_n:00}-{name}.png"));
        return new Img(shown.GetData(), shown.GetWidth(), shown.GetHeight());
    }
    private async Task<Img> Settle(string name, int frames = 4) { Img last = null; for (int i = 0; i < frames; i++) last = await Draw(i == frames - 1 ? name : null); return last; }

    private static double Luma(byte[] d, int o) => 0.299 * d[o] + 0.587 * d[o + 1] + 0.114 * d[o + 2];
    private sealed record Added(int Box, int Frame, double MeanFrame);
    // Bloom only adds light: count pixels where "on" is brighter than "off".
    private static Added Gain(Img on, Img off)
    {
        int box = 0, all = 0; double sum = 0;
        for (int i = 0; i < on.W * on.H; i++) {
            double d = Luma(on.Rgba, i * 4) - Luma(off.Rgba, i * 4); if (d > 0) sum += d;
            if (d < 12) continue; all++;
            int px = i % on.W, py = i / on.W; if (px >= BoxX0 && px <= BoxX1 && py >= BoxY0 && py <= BoxY1) box++;
        }
        return new Added(box, all, sum / (on.W * on.H));
    }
    private sealed record Cen(double X, double Y, int N);
    private static Cen Centroid(Img on, Img off)
    {
        double sx = 0, sy = 0; int n = 0;
        for (int i = 0; i < on.W * on.H; i++) {
            if (Luma(on.Rgba, i * 4) - Luma(off.Rgba, i * 4) < 12) continue;
            sx += i % on.W; sy += i / on.W; n++;
        }
        return n == 0 ? new Cen(0, 0, 0) : new Cen(sx / n, sy / n, n);
    }
    private async Task<object> Perf(int w, int h, int mode, int n)
    {
        PostFxStack.LabsLightInput = mode; var wall = new List<double>(); var gpu = new List<double>(); var rootGpu = new List<double>();
        var rid = GetViewport().GetViewportRid(); int lit = 0;
        for (int i = 0; i < 20; i++) { Mutated(); _scene.Camera.Update(true, 0, GUO.Input.Mouse.Position); _draw.Invoke(_game, null); await Painted(); }
        for (int i = 0; i < n; i++) {
            long t = System.Diagnostics.Stopwatch.GetTimestamp();
            Mutated(); _scene.Camera.Update(true, 0, GUO.Input.Mouse.Position); _draw.Invoke(_game, null); await Painted();
            wall.Add((System.Diagnostics.Stopwatch.GetTimestamp() - t) * 1000.0 / System.Diagnostics.Stopwatch.Frequency);
            gpu.Add(PostFxStack.Instance.LastGpuMs); rootGpu.Add(RenderingServer.ViewportGetMeasuredRenderTimeGpu(rid));
            if (OrderedWorldCapture.LastGlowElected > 0) lit++;
        }
        PostFxStack.LabsLightInput = 0;
        static double Med(List<double> v) { var s = v.OrderBy(x => x).ToList(); return s[s.Count / 2]; }
        return new { w, h, input = mode == 3 ? "classic(pre-change)" : "elected", frames = n, electedFrames = lit, cpuFrameMsMedian = Med(wall), postfxGpuMsMedian = Med(gpu), rootGpuMsMedian = Med(rootGpu) };
    }
    private static int Differing(Img a, Img b) { int n = 0; for (int i = 0; i < a.Rgba.Length; i++) if (a.Rgba[i] != b.Rgba[i]) n++; return n; }

    private static void UseGlow(bool on) { PostFxStack.Instance.EnsureLoaded(); PostFxStack.Instance.Use(on ? PostFxLibrary.Find("Glow") : PostFxPreset.Classic(), false); }
    private void Light(int overall) { var l = _game.UO.World.Light; l.Overall = l.RealOverall = overall; l.Personal = l.RealPersonal = 0; }

    // Glow-off then Glow-on of the current state; mode is PostFxStack.LabsLightInput for the Glow-on frames.
    private async Task<(Img Off, Img On, string Input)> Pair(string name, int mode = 0, int frames = 6)
    {
        UseGlow(false); PostFxStack.LabsLightInput = 0; var off = await Settle(name + "-glow-off", frames);
        UseGlow(true); PostFxStack.LabsLightInput = mode; var on = await Settle(name + "-glow-on", frames);
        string input = PostFxStack.Instance.LastLightInput; PostFxStack.LabsLightInput = 0;
        return (off, on, input);
    }

    public override async void _Ready()
    {
        try {
            string[] args = OS.GetCmdlineUserArgs();
            Require(!args.Any(a => a is "--account" or "--password" or "--auto-login" or "--autologin" or "--shard-session" or "--window-size"), "no account/shard overrides");
            for (int i = 0; Client.Game?.Scene is not LoginScene; i++) { if (i > 1200) throw new Exception("login timeout"); await ToSignal(GetTree(), SceneTree.SignalName.ProcessFrame); }
            _game = Client.Game; var world = _game.UO.World;
            Require(!NetClient.Socket.IsConnected, "disconnected bootstrap");
            var setup = typeof(NetClient).GetMethod("SetupSocket", Private);
            setup.Invoke(NetClient.Socket, new[] { Enum.Parse(setup.GetParameters()[0].ParameterType, "TcpSocket") });
            Settings.GlobalSettings.ProfilesPath = Path.Combine(_root, "fixture", "profiles");
            typeof(ProfileManager).GetField("_rootPath", BindingFlags.Static | BindingFlags.NonPublic)?.SetValue(null, null);
            ProfileManager.Load("Offline", "glow-light-input", "glow-light-input");
            _profile = ProfileManager.CurrentProfile;
            Require(Path.GetFullPath(ProfileManager.ProfilePath).StartsWith(_root, StringComparison.OrdinalIgnoreCase), "isolated profile");
            _profile.DefaultScale = 1; _profile.GameWindowFullSize = true; _profile.TopbarGumpIsDisabled = true;
            _profile.UseCircleOfTransparency = false; _profile.NoColorObjectsOutOfRange = false; _profile.EnableDeathScreen = false;
            _profile.LightingSpeedMode = false; _profile.RealtimeLights = false; _profile.SpeedModeSunShadows = false; _profile.SpeedModeLampShadows = 0;
            _profile.SpeedModeSoften = 0; _profile.SpeedModeWindowLight = false; _profile.SpeedModeWeather = false;
            _profile.SpeedModeFog = false; _profile.SpeedModeMorningHaze = 0; _profile.SpeedModeReflections = false;
            _profile.WaterStyle = "classic"; _profile.WindStrength = 0; _profile.DrawRoofs = false;
            _profile.ShadowsEnabled = false; _profile.ShadowsStatics = false;
            _profile.LocalLightingTestTime = false; _profile.LocalLightingTestAuto = false;
            world.CreatePlayer(1); world.MapIndex = 0; world.Player.Graphic = 0x190;
            world.Player.SetInWorldTile(X, Y, 20); world.ShardClock.Set(12, 0, 0, Time.Ticks);
            _scene = new GameScene(world); _game.SetScene(_scene); _game.SetWindowSize(1280, 720); _scene.Camera.Zoom = 1;
            for (int i = 0; i < 12; i++) await ToSignal(GetTree(), SceneTree.SignalName.ProcessFrame);
            world.Map.GetMapZ(X, Y, out sbyte ground, out _); _z = ground + 1;
            world.Player.SetInWorldTile(X, Y, (sbyte)_z);
            var td = _game.UO.FileManager.TileData.StaticData;
            for (int y = -6; y <= 6; y++) for (int x = -6; x <= 6; x++) {
                var f = world.GetOrCreateItem(_serial++); f.Graphic = 0x0495; f.Amount = 1; f.SetInWorldTile((ushort)(X + x), (ushort)(Y + y), (sbyte)_z);
                f.CheckGraphicChange(); f.AlphaHue = 255; _objs.Add(f);
            }
            // The lamp: the first light-source static with art, preferring known lamps and torches.
            int lamp = new int[] { 0x0A15, 0x0B1D, 0x0A12, 0x0A0F, 0x0A26 }.Concat(Enumerable.Range(0, td.Length))
                .First(id => id < td.Length && td[id].IsLight && _game.UO.Arts.GetArt((uint)id).Texture != null);
            var ls = Static.Create(world, (ushort)lamp, 0, 2000000); ls.SetInWorldTile((ushort)(X + 1), (ushort)(Y + 1), (sbyte)_z); _objs.Add(ls);
            _draw = _game.GetType().GetMethod("DrawFrame", Private);
            _scene.UpdateDrawPosition = true;
            for (int i = 0; i < 40; i++) await ToSignal(GetTree(), SceneTree.SignalName.ProcessFrame);
            _game.SetProcess(false);
            _rows.Add(new { test = "fixture", x = X, y = Y, z = _z, floor = 0x0495, lamp, resolution = "1280x720" });

            // 1) classic lighting, night: Glow follows the classic target, unchanged by the fix.
            Light(30);
            var c = await Pair("1-classic-night");
            var cForced = await Pair("1-classic-night-forced", mode: 1);
            var cGain = Gain(c.On, c.Off); int cSame = Differing(c.On, cForced.On);
            _rows.Add(new { test = "1-classic-night", input = c.Input, gain = cGain, differingBytesVsForcedClassicInput = cSame });
            var cNo = await Pair("1-classic-night-prechange", mode: 3); int cNoDiff = Differing(c.On, cNo.On);
            Check(cNoDiff == 0 && OrderedWorldCapture.LastGlowElected == 0, $"1: classic byte-exact vs never-elect, nothing elected ({cNoDiff})");
            Check(c.Input == "classic", "1: classic lighting feeds the classic target");
            Check(cGain.Box >= ClassicMinAdded, $"1: Glow still blooms from the classic target ({cGain.Box} px, need >= {ClassicMinAdded})");
            Check(cSame == 0, $"1: classic Glow output identical to the pre-1150 input ({cSame} bytes differ)");

            // 2) realtime night: lamp glow now present.
            _profile.RealtimeLights = true; Light(30);
            await Settle(null, 4);
            var n = await Pair("2-realtime-night");
            var nOld = await Pair("2-realtime-night-legacy", mode: 1);
            var nRaw = await Pair("2-realtime-night-raw", mode: 2);
            var nGain = Gain(n.On, n.Off); var nOldGain = Gain(nOld.On, nOld.Off); var nRawGain = Gain(nRaw.On, nRaw.Off);
            _rows.Add(new { test = "2-realtime-night", input = n.Input, legacyInput = nOld.Input, gain = nGain, legacyGain = nOldGain, rawGain = nRawGain });
            var nNo = await Pair("2-realtime-night-prechange", mode: 3); int nNoDiff = Differing(n.On, nNo.On);
            Check(nNoDiff == 0 && OrderedWorldCapture.LastGlowElected == 0, $"2: realtime byte-exact vs never-elect, nothing elected ({nNoDiff})");
            Check(n.Input == "realtime", "2: realtime feeds the realtime field");
            Check(nGain.Box >= NightLampMinAdded, $"2: lamp glow present ({nGain.Box} px, need >= {NightLampMinAdded})");
            Check(nOldGain.Box <= LegacyMaxAdded, $"2: the classic input gave ~no glow ({nOldGain.Box} px, allow <= {LegacyMaxAdded})");

            // 3) realtime noon: no full-screen bloom.
            Light(0); await Settle(null, 4);
            var d = await Pair("3-realtime-noon");
            var dRaw = await Pair("3-realtime-noon-raw", mode: 2);
            var dGain = Gain(d.On, d.Off); var dRawGain = Gain(dRaw.On, dRaw.Off);
            _rows.Add(new { test = "3-realtime-noon", input = d.Input, gain = dGain, rawFieldGain = dRawGain });
            Check(d.Input == "realtime", "3: realtime feeds the realtime field by day");
            Check(dGain.Frame <= NoonMaxAddedPx && dGain.MeanFrame <= NoonMaxMeanAdd, $"3: no full-screen bloom by day ({dGain.Frame} px, mean {dGain.MeanFrame:0.000})");

            // 4) enhanced: Glow is elected, inside the render-thread success transaction, to the light that lit the frame.
            _profile.RealtimeLights = false; _profile.LightingSpeedMode = true;
            _profile.LocalLightingTestTime = true; LightingTestClock.Seek(_profile, 0);
            var e = await Pair("4-enhanced-night", frames: 8);
            var eInfo = Snap(); int eElected = OrderedWorldCapture.LastGlowElected;
            var eNo = await Pair("4-enhanced-night-noelect", mode: 3, frames: 8);
            var eGain = Gain(e.On, e.Off); var eNoGain = Gain(eNo.On, eNo.Off);
            _rows.Add(new { test = "4-enhanced-night", seen = eInfo, elected = eElected, gain = eGain, preChangeInputGain = eNoGain });
            Check(eInfo.Active && eInfo.Lit, "4: enhanced path active and lit");
            Check(eElected == 1, $"4: one Glow material elected on a lit frame ({eElected})");
            Check(eGain.Box >= NightLampMinAdded, $"4: enhanced night lamp glow present ({eGain.Box} px, need >= {NightLampMinAdded})");
            Check(eNoGain.Box < eGain.Box, $"4: the elected input is what blooms (pre-change input {eNoGain.Box} px < {eGain.Box})");

            // 5) enhanced noon: no wash.
            LightingTestClock.Seek(_profile, 720);
            var en = await Pair("5-enhanced-noon", frames: 8); var enGain = Gain(en.On, en.Off);
            _rows.Add(new { test = "5-enhanced-noon", gain = enGain, elected = OrderedWorldCapture.LastGlowElected });
            Check(enGain.Frame <= NoonMaxAddedPx && enGain.MeanFrame <= NoonMaxMeanAdd, $"5: no wash by day ({enGain.Frame} px, mean {enGain.MeanFrame:0.000})");
            LightingTestClock.Seek(_profile, 0);

            // 6) per-frame election == lit; moving lamp, bloom centroid tracks the lamp (no lag).
            UseGlow(true); await Settle(null, 6);
            var lampItem = _objs.OfType<Static>().Last(); int lag = 0, mismatch = 0; var lagRows = new List<object>();
            for (int step = 1; step <= 6; step++) {
                lampItem.SetInWorldTile((ushort)(X + 1 + (step % 3)), (ushort)(Y + 1 + (step % 2)), (sbyte)_z);
                PostFxStack.LabsLightInput = 0; var first = await Draw(null);
                bool lit1 = OrderedWorldCapture.LastFrameEnhancedLit; int el1 = OrderedWorldCapture.LastGlowElected;
                if ((el1 > 0) != lit1) mismatch++;
                PostFxStack.LabsLightInput = 3; var offImg = await Draw(null);
                PostFxStack.LabsLightInput = 0; var settled = await Draw(null); settled = await Draw(null);
                var cf = Centroid(first, offImg); var cs = Centroid(settled, offImg);
                double dist = Math.Sqrt((cf.X - cs.X) * (cf.X - cs.X) + (cf.Y - cs.Y) * (cf.Y - cs.Y));
                if (cs.N < NightLampMinAdded / 4 || dist > CentroidMaxPx) lag++;
                lagRows.Add(new { step, lit = lit1, elected = el1, firstN = cf.N, settledN = cs.N, cf.X, cf.Y, sx = cs.X, sy = cs.Y, dist });
            }
            _rows.Add(new { test = "6-moving-lamp", steps = lagRows, lagFrames = lag, electionMismatches = mismatch });
            Check(mismatch == 0, $"6: elected input == LastFrameEnhancedLit on every frame ({mismatch} mismatches)");
            Check(lag == 0, $"6: bloom centroid tracks the moving lamp within {CentroidMaxPx}px every frame ({lag} bad steps)");
            lampItem.SetInWorldTile((ushort)(X + 1), (ushort)(Y + 1), (sbyte)_z);
            await Settle(null, 4);

            // 7) failure, sustained, then recovery; no full-screen gain on any frame.
            UseGlow(false); OrderedWorldCapture.InjectFailure = 2; var failOff = await Settle("7-fail-glow-off", 4);
            OrderedWorldCapture.InjectFailure = 0; await Settle(null, 4);
            UseGlow(true); await Settle(null, 4);
            OrderedWorldCapture.InjectFailure = 2; var fRows = new List<object>(); int fBad = 0;
            for (int i = 0; i < 12; i++) {
                var f = await Draw(i == 11 ? "7-failing-frame12" : null);
                bool lit = OrderedWorldCapture.LastFrameEnhancedLit; int el = OrderedWorldCapture.LastGlowElected; var g = Gain(f, failOff);
                if (lit || el != 0 || PostFxStack.Instance.LastLightInput != "classic" || g.Frame > FrameMaxPx || g.MeanFrame > FrameMaxMean) fBad++;
                fRows.Add(new { i, lit, el, frame = g.Frame, mean = g.MeanFrame });
            }
            OrderedWorldCapture.InjectFailure = 0;
            // 7a) recovery with Glow running (no PostFx build frame): strict, election == lit on every frame.
            int liveFirst = -1, liveBad = 0; var liveRows = new List<object>();
            for (int i = 0; i < 8; i++) {
                var f = await Draw(i == 0 ? "7-live-recovery-first" : null);
                bool lit = OrderedWorldCapture.LastFrameEnhancedLit; int el = OrderedWorldCapture.LastGlowElected; var g = Gain(f, e.Off);
                if (lit && liveFirst < 0) liveFirst = i;
                if ((el > 0) != lit || g.Frame > FrameMaxPx || g.MeanFrame > FrameMaxMean) liveBad++;
                liveRows.Add(new { i, lit, el, frame = g.Frame, mean = g.MeanFrame });
            }
            _rows.Add(new { test = "7a-live-recovery", frames = liveRows, firstLitFrame = liveFirst, bad = liveBad });
            Check(liveBad == 0 && liveFirst >= 0 && liveFirst <= 2, $"7a: live recovery elected == lit on every frame, no wash (first lit {liveFirst}, {liveBad} bad)");
            int firstLit = -1; var rRows = new List<object>(); int rBad = 0;
            UseGlow(false); var recOff = await Settle(null, 3); UseGlow(true);
            for (int i = 0; i < 8; i++) {
                var f = await Draw(i == 0 ? "7-recovery-first" : null);
                bool lit = OrderedWorldCapture.LastFrameEnhancedLit; int el = OrderedWorldCapture.LastGlowElected; var g = Gain(f, recOff);
                if (lit && firstLit < 0) firstLit = i;
                if ((el > 0 && !lit) || (el == 0 && lit && i != 0) || g.Frame > FrameMaxPx || g.MeanFrame > FrameMaxMean) rBad++;
                rRows.Add(new { i, lit, el, frame = g.Frame, mean = g.MeanFrame });
            }
            _rows.Add(new { test = "7-failure-recovery", failureFrames = fRows, badFailureFrames = fBad, recoveryFrames = rRows, firstLitFrame = firstLit, badRecoveryFrames = rBad });
            Check(fBad == 0, $"7: classic input on all 12 injected-failure frames, no wash ({fBad} bad)");
            Check(firstLit >= 0 && firstLit <= 2, $"7: lit again within 3 frames of recovery (first lit frame {firstLit})");
            Check(rBad == 0, $"7: every recovery frame elected == lit with no full-screen gain ({rBad} bad)");
            // light-only failure: world enhanced, Glow classic.
            OrderedWorldCapture.InjectSpeedLightFailure = true; UseGlow(false); var slOff = await Settle("7b-light-fail-glow-off", 4); UseGlow(true); await Settle(null, 3);
            var sl = new List<object>(); int slBad = 0;
            for (int i = 0; i < 10; i++) {
                var f = await Draw(null); bool lit = OrderedWorldCapture.LastFrameEnhancedLit; int el = OrderedWorldCapture.LastGlowElected; var g = Gain(f, slOff);
                if (lit || el != 0 || g.Frame > FrameMaxPx || g.MeanFrame > FrameMaxMean) slBad++;
                sl.Add(new { i, lit, el, frame = g.Frame, mean = g.MeanFrame });
            }
            OrderedWorldCapture.InjectSpeedLightFailure = false;
            await Settle(null, 3);
            _rows.Add(new { test = "7b-light-only-failure", frames = sl, bad = slBad });
            Check(slBad == 0, $"7b: speed-light-only failure feeds Glow the classic input on all 10 frames ({slBad} bad)");

            // 8) exact first frames after enabling Glow, and after a resize.
            UseGlow(false); var enOff = await Settle(null, 4); UseGlow(true); int enBad = 0; var enRows = new List<object>();
            for (int i = 0; i < 6; i++) {
                var f = await Draw(null); var g = Gain(f, enOff); bool lit = OrderedWorldCapture.LastFrameEnhancedLit; int el = OrderedWorldCapture.LastGlowElected;
                if (g.Frame > FrameMaxPx || g.MeanFrame > FrameMaxMean || (el > 0 && !lit) || (el == 0 && lit && i != 0)) enBad++;
                enRows.Add(new { i, lit, el, frame = g.Frame, mean = g.MeanFrame });
            }
            _rows.Add(new { test = "8-enable", frames = enRows, bad = enBad });
            Check(enBad == 0, $"8: first frames after enabling Glow have no full-screen gain ({enBad} bad)");
            int rzBad = 0; var rzRows = new List<object>();
            foreach (var (w, h) in new[] { (1920, 1080), (1280, 720) }) {
                UseGlow(false); _game.SetWindowSize(w, h); var rzOff = await Settle(null, 5); UseGlow(true);
                for (int i = 0; i < 6; i++) {
                    var f = await Draw(i == 5 ? $"8-resize-{w}" : null); var g = Gain(f, rzOff); bool lit = OrderedWorldCapture.LastFrameEnhancedLit; int el = OrderedWorldCapture.LastGlowElected;
                    if (f.W != rzOff.W || g.Frame > FrameMaxPx || g.MeanFrame > FrameMaxMean || (el > 0 && !lit) || (el == 0 && lit && i != 0)) rzBad++;
                    rzRows.Add(new { w, i, lit, el, frame = g.Frame, mean = g.MeanFrame });
                }
            }
            _rows.Add(new { test = "8-resize", frames = rzRows, bad = rzBad });
            Check(rzBad == 0, $"8: frames around a resize have no full-screen gain ({rzBad} bad)");
            // resize while Glow is already running (the world target is replaced under an active stack); mode 3 = control
            var rz2 = new List<object>(); int rz2Bad = 0; var rzFrames = new Dictionary<int, List<Added>>();
            foreach (int mode in new[] { 3, 0 }) {
                UseGlow(false); PostFxStack.LabsLightInput = 0; var ref1280 = await Settle(null, 4); UseGlow(true); PostFxStack.LabsLightInput = mode; await Settle(null, 4);
                _game.SetWindowSize(1920, 1080); await Settle(null, 4); _game.SetWindowSize(1280, 720);
                rzFrames[mode] = new();
                for (int i = 0; i < 6; i++) {
                    var f = await Draw(null);
                    if (f.W != ref1280.W) { rz2.Add(new { mode, i, w = f.W }); rz2Bad++; rzFrames[mode].Add(new Added(0, 0, 0)); continue; }
                    var g = Gain(f, ref1280); rzFrames[mode].Add(g); bool lit = OrderedWorldCapture.LastFrameEnhancedLit; int el = OrderedWorldCapture.LastGlowElected;
                    if ((el > 0 && !lit) || (el == 0 && lit && mode == 0 && i > 0)) rz2Bad++;
                    rz2.Add(new { mode, i, lit, el, frame = g.Frame, mean = g.MeanFrame, fallback = OrderedWorldCapture.LastFallback });
                }
                PostFxStack.LabsLightInput = 0;
            }
            for (int i = 0; i < 6; i++) if (rzFrames[0][i].Frame > rzFrames[3][i].Frame + FrameMaxPx) rz2Bad++;
            _rows.Add(new { test = "8-resize-running", frames = rz2, bad = rz2Bad });
            Check(rz2Bad == 0, $"8: resize with Glow running: the election adds no wash over the pre-change input, never elects unlit ({rz2Bad} bad)");

            // 9) two light-consuming passes (both elected) and a light_tex-only custom shader (not elected).
            var twin = PostFxPreset.Parse("{\"name\":\"LabsTwoBloom\",\"passes\":[{\"shader\":\"bloom\",\"params\":{\"threshold\":0.55,\"radius\":6,\"intensity\":0.7,\"from_world\":0.2}},{\"shader\":\"bloom\",\"params\":{\"threshold\":0.55,\"radius\":3,\"intensity\":0.4,\"from_world\":0.2}}],\"bindings\":[]}", "labs");
            UseGlow(false); var tOff = await Settle("9-two-pass-ref", 5);
            PostFxStack.Instance.Use(twin, false); var twinImg = await Settle("9-two-bloom", 6); int twinEl = OrderedWorldCapture.LastGlowElected;
            var twinGain = Gain(twinImg, tOff);
            var custom = new Shader { Code = "shader_type canvas_item;\nrender_mode blend_disabled;\nuniform sampler2D source : hint_screen_texture, filter_nearest;\nuniform sampler2D light_tex : filter_nearest;\nvoid fragment() { COLOR = vec4(texture(source, SCREEN_UV).rgb + texture(light_tex, SCREEN_UV).rgb * 0.5, 1.0); }\n" };
            PostFxLibrary.Register("labs_light_only", custom);
            var customPreset = PostFxPreset.Parse("{\"name\":\"LabsLightOnly\",\"passes\":[{\"shader\":\"labs_light_only\",\"params\":{}}],\"bindings\":[]}", "labs");
            PostFxStack.Instance.Use(customPreset, false); PostFxStack.LabsLightInput = 0; var cu = await Settle("9-custom-light-only", 6); int cuEl = OrderedWorldCapture.LastGlowElected;
            PostFxStack.LabsLightInput = 3; var cuNo = await Settle(null, 6); PostFxStack.LabsLightInput = 0;
            int cuDiff = Differing(cu, cuNo);
            _rows.Add(new { test = "9-two-light-passes", elected = twinEl, gain = twinGain, customLightOnlyElected = cuEl, customDifferingBytesVsPreChange = cuDiff });
            Check(twinEl == 2, $"9: both light_tex+light_ambient passes elected ({twinEl})");
            Check(twinGain.Box >= NightLampMinAdded, $"9: two-pass glow present ({twinGain.Box} px)");
            Check(cuEl == 0 && cuDiff == 0, $"9: a light_tex-only custom shader keeps the classic contract (elected {cuEl}, {cuDiff} bytes differ from pre-change)");

            // 10) soften 0 and 10; mobile half-resolution tier; compare split.
            var soften = new List<object>();
            foreach (int sf in new[] { 0, 10 }) {
                _profile.SpeedModeSoften = sf; UseGlow(false); var so = await Settle(null, 5); UseGlow(true); var sn = await Settle($"10-soften-{sf}", 6);
                var g = Gain(sn, so); soften.Add(new { soften = sf, elected = OrderedWorldCapture.LastGlowElected, gain = g });
                Check(OrderedWorldCapture.LastGlowElected == 1 && g.Box >= NightLampMinAdded, $"10: soften {sf} glow present and elected ({g.Box} px)");
            }
            _profile.SpeedModeSoften = 0;
            PostFxStack.Instance.FullQuality = false; UseGlow(false); var hOff = await Settle(null, 5); UseGlow(true); var hOn = await Settle("10-half-res", 6);
            var hg = Gain(hOn, hOff); int hEl = OrderedWorldCapture.LastGlowElected;
            Check(hEl == 1 && hg.Box >= HalfResMinAdded && hg.Frame <= FrameMaxPx && hg.MeanFrame <= HalfResMaxMean, $"10: mobile half-res tier elected and blooms ({hg.Box} px, frame {hg.Frame}, elected {hEl})");
            PostFxStack.Instance.Split = 0.5f; PostFxStack.Instance.Rebuild(); var sp = await Settle("10-compare-split", 6); int spEl = OrderedWorldCapture.LastGlowElected; var spg = Gain(sp, hOff);
            PostFxStack.Instance.Split = 0f; PostFxStack.Instance.Rebuild(); PostFxStack.Instance.FullQuality = true;
            Check(Differing(sp, hOn) > 0 && spEl == 1 && spg.Frame <= FrameMaxPx && spg.MeanFrame <= HalfResMaxMean, $"10: compare split under enhanced (elected {spEl}, frame {spg.Frame})");
            _rows.Add(new { test = "10-variants", soften, halfRes = new { elected = hEl, gain = hg }, split = new { elected = spEl, gain = spg } });

            // 11) Glow off under enhanced: nothing elected, ever.
            UseGlow(false); int offElected = 0;
            for (int i = 0; i < 6; i++) { await Draw(null); offElected += OrderedWorldCapture.LastGlowElected + (PostFxStack.Instance.TryGetLightElection(out _) ? 1 : 0); }
            Check(offElected == 0 && OrderedWorldCapture.LastFrameEnhancedLit, $"11: Glow off under enhanced publishes nothing ({offElected})");
            _rows.Add(new { test = "11-glow-off", elected = offElected });

            // 12) performance: enhanced night, Glow on; elected vs the pre-change (classic) input, ABAB per resolution.
            if (System.Environment.GetEnvironmentVariable("GUO_BLOOM_PERF") == "1") {
                RenderingServer.ViewportSetMeasureRenderTime(GetViewport().GetViewportRid(), true);
                UseGlow(true); var perf = new List<object>();
                foreach (var (w, h) in new[] { (1280, 720), (1920, 1080) }) {
                    _game.SetWindowSize(w, h); await Settle(null, 20);
                    foreach (int mode in new[] { 3, 0, 3, 0 }) perf.Add(await Perf(w, h, mode, 150));
                }
                _rows.Add(new { test = "12-perf", runs = perf });
            }

            Check(!NetClient.Socket.IsConnected, "never connected"); Save();
            if (_failures.Count != 0) throw new InvalidOperationException(string.Join("; ", _failures));
            GD.Print("[glow-light-input] PASS");
            GetTree().Quit(0);
        } catch (Exception error) {
            _failures.Add(error.ToString()); Save(); GD.PrintErr("[glow-light-input] FAIL " + error); GetTree().Quit(1);
        } finally { OrderedWorldCapture.InjectFailure = 0; PostFxStack.LabsLightInput = 0; foreach (var o in _objs) if (!o.IsDestroyed) o.Destroy(); }
    }
}
