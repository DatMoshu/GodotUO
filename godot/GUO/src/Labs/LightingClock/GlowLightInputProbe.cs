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

            // 4) enhanced: the input stays the classic target (presentation decides late); failure frames included.
            _profile.RealtimeLights = false; _profile.LightingSpeedMode = true;
            _profile.LocalLightingTestTime = true; LightingTestClock.Seek(_profile, 0);
            var e = await Pair("4-enhanced-night", frames: 8);
            var eInfo = Snap();
            OrderedWorldCapture.InjectFailure = 2;
            UseGlow(true); var inputs = new List<string>();
            for (int i = 0; i < 4; i++) { await Draw(i == 3 ? "4-enhanced-failing" : null); inputs.Add(PostFxStack.Instance.LastLightInput); }
            OrderedWorldCapture.InjectFailure = 0;
            _rows.Add(new { test = "4-enhanced", input = e.Input, seen = eInfo, failureFrameInputs = inputs, lastFallback = OrderedWorldCapture.LastFallback });
            Check(eInfo.Active, "4: enhanced path active");
            Check(e.Input == "classic" && inputs.All(i => i == "classic"), "4: enhanced and its failure frames feed the classic target");

            Check(!NetClient.Socket.IsConnected, "never connected"); Save();
            if (_failures.Count != 0) throw new InvalidOperationException(string.Join("; ", _failures));
            GD.Print("[glow-light-input] PASS");
            GetTree().Quit(0);
        } catch (Exception error) {
            _failures.Add(error.ToString()); Save(); GD.PrintErr("[glow-light-input] FAIL " + error); GetTree().Quit(1);
        } finally { OrderedWorldCapture.InjectFailure = 0; PostFxStack.LabsLightInput = 0; foreach (var o in _objs) if (!o.IsDestroyed) o.Destroy(); }
    }
}
