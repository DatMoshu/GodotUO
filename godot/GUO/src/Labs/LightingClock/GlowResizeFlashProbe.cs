// SPDX-License-Identifier: BSD-2-Clause
// Labs-only offline proof (task 1152): no full-screen bloom flash on the first frames after a world resize, a PostFx
// rebuild, a Glow re-enable, an enhanced-lighting re-enable or a capture-failure recovery. Compiles unchanged on the
// pre-1150 base (optional 1150 read-outs go through reflection). Britain 1417,1734, scratch profile, no shard/account.
// Thresholds are frozen here before the first run and are never changed afterwards.
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
using GUO.Renderer.DepthWorld;
using GUO.Renderer.PostFx;
using GUO.Renderer.SpeedMode;

namespace GUO.Labs.LightingClock;

public partial class GlowResizeFlashProbe : Node
{
    private const int X = 1417, Y = 1734;
    private const BindingFlags Any = BindingFlags.Instance | BindingFlags.Static | BindingFlags.Public | BindingFlags.NonPublic;
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
    private bool _glow, _enh;

    private sealed record Img(byte[] Rgba, int W, int H);
    private sealed record Info(bool Lit, string Fallback, string Input, int Elected, int W);
    private sealed record Row(int frame, int px, double meanPositive, double meanSigned, int w, bool lit, string lightInput, int glowElected, string fallback, bool flash);

    // FROZEN THRESHOLDS (task 1152), fixed before any repair run. Pixels counted = luma brighter by >= LumaStep than the
    // settled frame of the same state (Glow-on settled frame for Glow-on cases, Glow-off settled frame for Glow-off).
    private const int LumaStep = 12;
    private const int FlashPx = 100000;        // a full-screen flash: more than this many brighter pixels ...
    private const double FlashMean = 5.0;      // ... or a mean positive luma gain over the frame above this
    private const int AllowedPx = 30000;       // allowed on every frame of every case
    private const double AllowedMean = 1.0;
    private const int Frames = 6;

    public override void _EnterTree()
    {
        _root = System.Environment.GetEnvironmentVariable("GUO_FLASH_OUT");
        Require(!string.IsNullOrWhiteSpace(_root) && Path.IsPathRooted(_root), "external GUO_FLASH_OUT required");
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
    private async Task Painted() => await ToSignal(RenderingServer.Singleton, RenderingServer.SignalName.FramePostDraw);
    private void Save() => File.WriteAllText(Path.Combine(_root, "glow-resize-flash-results.json"),
        JsonSerializer.Serialize(new { thresholds = new { LumaStep, FlashPx, FlashMean, AllowedPx, AllowedMean, Frames }, rows = _rows, failures = _failures },
            new JsonSerializerOptions { WriteIndented = true }));

    private void Mutated() { _scene.UpdateDrawPosition = true; _scene.UpdateMaxDrawZ(true); _scene.Update(); }

    // Optional (1150-only) read-outs; absent on the base.
    private static string LightInput() => PostFxStack.Instance.GetType().GetProperty("LastLightInput", Any)?.GetValue(PostFxStack.Instance) as string ?? "n/a";
    private static int Elected() => typeof(OrderedWorldCapture).GetProperty("LastGlowElected", Any)?.GetValue(null) is int i ? i : -1;

    private async Task<(Img Img, Info Info)> Draw(string stillName)
    {
        Mutated();
        _scene.Camera.Update(true, 0, GUO.Input.Mouse.Position); _draw.Invoke(_game, null); await Painted();
        using var shown = GetViewport().GetTexture().GetImage();
        if (shown.GetFormat() != Image.Format.Rgba8) shown.Convert(Image.Format.Rgba8);
        if (stillName != null) shown.SavePng(Path.Combine(_stills, $"{++_n:000}-{stillName}.png"));
        return (new Img(shown.GetData(), shown.GetWidth(), shown.GetHeight()),
            new Info(OrderedWorldCapture.LastFrameEnhancedLit, OrderedWorldCapture.LastFallback, LightInput(), Elected(), shown.GetWidth()));
    }
    private async Task<Img> Settle(string still, int frames = 6) { Img last = null; for (int i = 0; i < frames; i++) last = (await Draw(i == frames - 1 ? still : null)).Img; return last; }

    private static double Luma(byte[] d, int o) => 0.299 * d[o] + 0.587 * d[o + 1] + 0.114 * d[o + 2];
    private static (int Px, double Mean, double SignedMean) Gain(Img on, Img reference)
    {
        if (on.W != reference.W || on.H != reference.H) return (-1, -1, -1);
        int px = 0; double pos = 0, sgn = 0; int n = on.W * on.H;
        for (int i = 0; i < n; i++) {
            double d = Luma(on.Rgba, i * 4) - Luma(reference.Rgba, i * 4); sgn += d; if (d > 0) pos += d;
            if (d >= LumaStep) px++;
        }
        return (px, pos / n, sgn / n);
    }

    private async Task Ensure(bool glow, bool enhanced, int w, int h)
    {
        _glow = glow; _enh = enhanced;
        UseGlow(glow); SetLighting(enhanced);
        _game.SetWindowSize(w, h);
        await Settle(null, 8);
    }
    private static void UseGlow(bool on) { PostFxStack.Instance.EnsureLoaded(); PostFxStack.Instance.Use(on ? PostFxLibrary.Find("Glow") : PostFxPreset.Classic(), false); }
    private void SetLighting(bool enhanced)
    {
        _profile.RealtimeLights = false;
        if (enhanced) { _profile.LightingSpeedMode = true; _profile.LocalLightingTestTime = true; LightingTestClock.Seek(_profile, 0); }
        else { _profile.LightingSpeedMode = false; _profile.LocalLightingTestTime = false; var l = _game.UO.World.Light; l.Overall = l.RealOverall = 30; l.Personal = l.RealPersonal = 0; }
    }

    // prep() puts the state in place and settles; act() is the single change under test, applied just before frame 0.
    private async Task Case(string name, Func<Task> prep, Action act, int frames = Frames)
    {
        await prep();
        string label = $"{(_enh ? "enhanced" : "classic")}-{(_glow ? "glow" : "noglow")}-{name}";
        act();
        var got = new List<(Img Img, Info Info)>();
        for (int i = 0; i < frames; i++) got.Add(await Draw(i == 0 ? label + "-f0" : null));
        var settled = await Settle(label + "-settled", 6);
        var rows = new List<Row>(); bool bad = false, flash = false;
        for (int i = 0; i < got.Count; i++) {
            var g = Gain(got[i].Img, settled); var inf = got[i].Info;
            bool isFlash = g.Px > FlashPx || g.Mean > FlashMean; bool over = g.Px < 0 || g.Px > AllowedPx || g.Mean > AllowedMean;
            flash |= isFlash; bad |= over;
            rows.Add(new Row(i, g.Px, Math.Round(g.Mean, 3), Math.Round(g.SignedMean, 3), inf.W, inf.Lit, inf.Input, inf.Elected, inf.Fallback, isFlash));
        }
        _rows.Add(new { @case = label, flashDetected = flash, pass = !bad, frames = rows });
        string px = string.Join(",", rows.Select(r => r.px));
        if (bad) _failures.Add($"{label}: frame over the allowed gain ({px})");
        GD.Print($"[glow-resize-flash] {label} {(bad ? "FAIL" : "ok")} flash={flash} px=[{px}] mean=[{string.Join(",", rows.Select(r => r.meanPositive))}]");
    }

    public override async void _Ready()
    {
        try {
            string[] args = OS.GetCmdlineUserArgs();
            Require(!args.Any(a => a is "--account" or "--password" or "--auto-login" or "--autologin" or "--shard-session" or "--window-size"), "no account/shard overrides");
            for (int i = 0; Client.Game?.Scene is not LoginScene; i++) { if (i > 1200) throw new Exception("login timeout"); await ToSignal(GetTree(), SceneTree.SignalName.ProcessFrame); }
            _game = Client.Game; var world = _game.UO.World;
            Require(!NetClient.Socket.IsConnected, "disconnected bootstrap");
            var setup = typeof(NetClient).GetMethod("SetupSocket", BindingFlags.Instance | BindingFlags.NonPublic);
            setup.Invoke(NetClient.Socket, new[] { Enum.Parse(setup.GetParameters()[0].ParameterType, "TcpSocket") });
            Settings.GlobalSettings.ProfilesPath = Path.Combine(_root, "fixture", "profiles");
            typeof(ProfileManager).GetField("_rootPath", BindingFlags.Static | BindingFlags.NonPublic)?.SetValue(null, null);
            ProfileManager.Load("Offline", "glow-resize-flash", "glow-resize-flash");
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
            int lamp = new int[] { 0x0A15, 0x0B1D, 0x0A12, 0x0A0F, 0x0A26 }.Concat(Enumerable.Range(0, td.Length))
                .First(id => id < td.Length && td[id].IsLight && _game.UO.Arts.GetArt((uint)id).Texture != null);
            var ls = Static.Create(world, (ushort)lamp, 0, 2000000); ls.SetInWorldTile((ushort)(X + 1), (ushort)(Y + 1), (sbyte)_z); _objs.Add(ls);
            _draw = _game.GetType().GetMethod("DrawFrame", BindingFlags.Instance | BindingFlags.NonPublic);
            _scene.UpdateDrawPosition = true;
            for (int i = 0; i < 40; i++) await ToSignal(GetTree(), SceneTree.SignalName.ProcessFrame);
            _game.SetProcess(false);
            _rows.Add(new { test = "fixture", x = X, y = Y, z = _z, lamp, lightInputReadout = LightInput(), electedReadout = Elected() });

            foreach (bool enh in new[] { true, false })
            foreach (bool glow in new[] { true, false }) {
                bool e = enh, g = glow;
                Func<Task> small = () => Ensure(g, e, 1280, 720);
                // (a) world / window resize, up then down
                await Case("resize-up", small, () => _game.SetWindowSize(1920, 1080));
                await Case("resize-down", () => Ensure(g, e, 1920, 1080), () => _game.SetWindowSize(1280, 720));
                if (g) {
                    // (b) PostFx rebuild, and re-selecting the same preset
                    await Case("rebuild", small, () => PostFxStack.Instance.Rebuild());
                    await Case("reselect", small, () => UseGlow(true));
                    // (c) Glow re-enable after off
                    await Case("glow-reenable", async () => { await Ensure(false, e, 1280, 720); _glow = true; }, () => UseGlow(true));
                }
                if (e) {
                    // (d) enhanced lighting off -> on
                    await Case("light-reenable", async () => { await Ensure(g, true, 1280, 720); _profile.LightingSpeedMode = false; await Settle(null, 6); }, () => _profile.LightingSpeedMode = true);
                    // (e) recovery after an injected capture failure
                    await Case("failure-recovery", async () => { await Ensure(g, true, 1280, 720); OrderedWorldCapture.InjectFailure = 2; await Settle(null, 12); }, () => OrderedWorldCapture.InjectFailure = 0, 8);
                }
            }
            Save();
            if (_failures.Count != 0) throw new InvalidOperationException(string.Join("; ", _failures));
            GD.Print("[glow-resize-flash] PASS");
            GetTree().Quit(0);
        } catch (Exception error) {
            _failures.Add(error.ToString()); Save(); GD.PrintErr("[glow-resize-flash] FAIL " + error); GetTree().Quit(1);
        } finally { OrderedWorldCapture.InjectFailure = 0; foreach (var o in _objs) if (!o.IsDestroyed) o.Destroy(); }
    }
}
