// SPDX-License-Identifier: BSD-2-Clause
// Labs-only offline proof (task 1149): GuoEffectPolicy.ClassicShadowsSuppressed changes the REAL rendered
// frame. Britain 1417,1734, scratch profile, no shard/account. Classic static (tree/rock) shadow pixels are
// measured as the presented-frame difference between ShadowsEnabled=true and ShadowsEnabled=false in the
// otherwise identical, settled state. Thresholds are fixed in code before the first run.
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
using GUO.Renderer.SpeedMode;
using GUO.Renderer.SpeedMode.Shadows;

namespace GUO.Labs.LightingClock;

public partial class NativeShadowOwnershipProbe : Node
{
    private const int X = 1417, Y = 1734, MinShadow = 100, MaxAbsent = 10;
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
    private sealed record Diff(int Count, int MinX, int MaxX, int MinY, int MaxY);
    private sealed record Info(bool Suppressed, bool Lit, bool Produced, bool Active, string Fallback, string Source);

    public override void _EnterTree()
    {
        _root = System.Environment.GetEnvironmentVariable("GUO_SHADOW_OUT");
        Require(!string.IsNullOrWhiteSpace(_root) && Path.IsPathRooted(_root), "external GUO_SHADOW_OUT required");
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
    private void Save() => File.WriteAllText(Path.Combine(_root, "native-shadow-ownership-results.json"),
        JsonSerializer.Serialize(new { disconnected = !NetClient.Socket.IsConnected, rows = _rows, failures = _failures },
            new JsonSerializerOptions { WriteIndented = true }));

    private void Mutated() { _scene.UpdateDrawPosition = true; _scene.UpdateMaxDrawZ(true); _scene.Update(); }
    private Info Snap() => new(GuoEffectPolicy.ClassicShadowsSuppressed(_profile), OrderedWorldCapture.LastFrameEnhancedLit,
        GuoEffectPolicy.EnhancedShadowsProduced, SpeedModeDebug.Active, OrderedWorldCapture.LastFallback, _scene.SpeedSunSource);

    // One draw. Seen = the policy state the game thread saw when it queued this frame.
    private async Task<(Img Img, Info Seen)> Draw(string name)
    {
        Mutated(); var seen = Snap();
        _scene.Camera.Update(true, 0, GUO.Input.Mouse.Position); _draw.Invoke(_game, null); await Painted();
        using var shown = GetViewport().GetTexture().GetImage();
        if (shown.GetFormat() != Image.Format.Rgba8) shown.Convert(Image.Format.Rgba8);
        if (name != null) shown.SavePng(Path.Combine(_stills, $"{++_n:00}-{name}.png"));
        return (new Img(shown.GetData(), shown.GetWidth(), shown.GetHeight()), seen);
    }
    private async Task<Img> Settle(string name, int frames = 4) { Img last = null; for (int i = 0; i < frames; i++) last = (await Draw(i == frames - 1 ? name : null)).Img; return last; }

    private static Diff Compare(Img a, Img b)
    {
        int n = 0, x0 = int.MaxValue, x1 = -1, y0 = int.MaxValue, y1 = -1;
        for (int i = 0; i < a.W * a.H; i++) {
            int o = i * 4;
            if (Math.Max(Math.Abs(a.Rgba[o] - b.Rgba[o]), Math.Max(Math.Abs(a.Rgba[o + 1] - b.Rgba[o + 1]), Math.Abs(a.Rgba[o + 2] - b.Rgba[o + 2]))) <= 8) continue;
            n++; int x = i % a.W, y = i / a.W; x0 = Math.Min(x0, x); x1 = Math.Max(x1, x); y0 = Math.Min(y0, y); y1 = Math.Max(y1, y);
        }
        return new Diff(n, x0, x1, y0, y1);
    }

    // Classic shadow pixels in the current settled state: ShadowsEnabled off (reference), then on.
    private async Task<(Diff D, Info On)> ClassicPixels(string name)
    {
        _profile.ShadowsEnabled = false; await Settle(null);
        var refImg = await Settle(name + "-ref");
        _profile.ShadowsEnabled = true; await Settle(null, 3);
        var (img, seen) = await Draw(name + "-shadows-on"); var d = Compare(img, refImg);
        Check(_profile.ShadowsEnabled && _profile.ShadowsStatics, name + ": stored preferences untouched");
        return (d, seen);
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
            ProfileManager.Load("Offline", "native-shadow-ownership", "native-shadow-ownership");
            _profile = ProfileManager.CurrentProfile;
            Require(Path.GetFullPath(ProfileManager.ProfilePath).StartsWith(_root, StringComparison.OrdinalIgnoreCase), "isolated profile");
            _profile.DefaultScale = 1; _profile.GameWindowFullSize = true; _profile.TopbarGumpIsDisabled = true;
            _profile.UseCircleOfTransparency = false; _profile.NoColorObjectsOutOfRange = false; _profile.EnableDeathScreen = false;
            _profile.LightingSpeedMode = false; _profile.SpeedModeSunShadows = true; _profile.SpeedModeLampShadows = 0;
            _profile.SpeedModeSoften = 0; _profile.SpeedModeWindowLight = false; _profile.SpeedModeWeather = false;
            _profile.SpeedModeFog = false; _profile.SpeedModeMorningHaze = 0; _profile.SpeedModeReflections = false;
            _profile.WaterStyle = "classic"; _profile.WindStrength = 0; _profile.DrawRoofs = false;
            _profile.ShadowsEnabled = true; _profile.ShadowsStatics = true;
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
            var trees = StaticFilters.TreeTiles.Where(id => SunShadowProjection.Eligible(td[id].Flags, td[id].Height) && _game.UO.Arts.GetArt(id).Texture != null).Take(1).ToList();
            Require(trees.Count == 1, "native tree witness");
            var spots = new[] { (-1, 1, trees[0]), (2, -1, trees[0]) }; int idx = 2000000;
            foreach (var (dx, dy, g) in spots) { var s = Static.Create(world, g, 0, idx++); s.SetInWorldTile((ushort)(X + dx), (ushort)(Y + dy), (sbyte)_z); _objs.Add(s); }
            ushort rock = 4945; bool rockArt = _game.UO.Arts.GetArt(rock).Texture != null;
            if (rockArt) { var s = Static.Create(world, rock, 0, idx++); s.SetInWorldTile((ushort)(X + 1), (ushort)(Y + 3), (sbyte)_z); _objs.Add(s); }
            _draw = _game.GetType().GetMethod("DrawFrame", Private);
            _scene.UpdateDrawPosition = true;
            for (int i = 0; i < 40; i++) await ToSignal(GetTree(), SceneTree.SignalName.ProcessFrame);
            _game.SetProcess(false);
            _rows.Add(new { test = "fixture", x = X, y = Y, z = _z, floor = 0x0495, tree = (int)trees[0], rock = rockArt, statics = _objs.OfType<Static>().Count(), resolution = "1280x720" });

            // a) enhanced off -> classic shadows drawn (baseline)
            var a = await ClassicPixels("a-enhanced-off");
            _rows.Add(new { test = "a-enhanced-off", a.D, seenOn = a.On });
            Check(!a.On.Active && !a.On.Suppressed, "a: enhanced path inactive, not suppressed");
            Check(a.D.Count >= MinShadow, $"a: classic shadows drawn with enhanced off ({a.D.Count} px, need >= {MinShadow})");

            // b) enhanced on, daytime sun shadows on, settled -> classic absent, enhanced present
            _profile.LightingSpeedMode = true; _profile.SpeedModeSunShadows = true; _profile.SpeedModeLampShadows = 0;
            _profile.LocalLightingTestTime = true; LightingTestClock.Seek(_profile, 12 * 60);
            var b = await ClassicPixels("b-enhanced-day");
            _profile.ShadowsEnabled = false; _profile.SpeedModeSunShadows = true; var sunOn = await Settle("b-sun-on-ref");
            _profile.SpeedModeSunShadows = false; var sunOff = await Settle("b-sun-off-ref"); _profile.SpeedModeSunShadows = true;
            var bSun = Compare(sunOn, sunOff);
            _rows.Add(new { test = "b-enhanced-day", classic = b.D, seenOn = b.On, enhancedSunShadow = bSun });
            Check(b.On.Active && b.On.Lit && b.On.Produced && b.On.Suppressed, $"b: gates true on the shadows-on frame {b.On}");
            Check(b.D.Count <= MaxAbsent, $"b: classic tree shadow absent while enhanced ({b.D.Count} px, allow <= {MaxAbsent})");
            Check(bSun.Count >= MinShadow, $"b: enhanced sun shadow present ({bSun.Count} px, need >= {MinShadow})");

            // c) enhanced on, nothing produced (night, sun off, lamp shadows on, no lamp in reach) -> classic drawn
            _profile.SpeedModeSunShadows = false; _profile.SpeedModeLampShadows = 3; LightingTestClock.Seek(_profile, 0);
            var c = await ClassicPixels("c-night-nolamp");
            _rows.Add(new { test = "c-night-nolamp", classic = c.D, seenOn = c.On });
            Check(c.On.Active && c.On.Lit && !c.On.Produced && !c.On.Suppressed, $"c: lit, nothing produced, not suppressed {c.On}");
            Check(c.D.Count >= MinShadow, $"c: classic shadows drawn when no enhanced shadow produced ({c.D.Count} px, need >= {MinShadow})");

            // d) forced failure -> classic returns; count frames showing neither
            _profile.SpeedModeSunShadows = true; _profile.SpeedModeLampShadows = 0; LightingTestClock.Seek(_profile, 12 * 60);
            _profile.ShadowsEnabled = true; await Settle(null, 5);
            var (_, d0) = await Draw("d0-before-failure");
            Check(d0.Suppressed && d0.Lit && d0.Produced, $"d: precondition suppressed {d0}");
            OrderedWorldCapture.InjectFailure = 2;
            var failFrames = new List<(Img Img, Info Seen)>();
            for (int i = 1; i <= 4; i++) failFrames.Add(await Draw($"d{i}-failing"));
            _profile.ShadowsEnabled = false; await Settle(null, 3);
            var failRef = await Settle("d-fallback-ref", 2);
            OrderedWorldCapture.InjectFailure = 0;
            int neither = 0; bool returned = false; var perFrame = new List<object>();
            for (int i = 0; i < failFrames.Count; i++) {
                var dd = Compare(failFrames[i].Img, failRef);
                bool classic = dd.Count >= MinShadow; if (!classic && !returned) neither++; else returned = true;
                perFrame.Add(new { frame = i + 1, classicPixels = dd.Count, seen = failFrames[i].Seen });
            }
            _rows.Add(new { test = "d-failure-fallback", framesWithNeither = neither, perFrame, lastFallback = OrderedWorldCapture.LastFallback });
            Check(returned, "d: classic shadows returned after forced failure");
            Check(neither <= 1, $"d: at most the one-frame boundary shows neither shadow (measured {neither})");

            Check(!NetClient.Socket.IsConnected, "never connected"); Save();
            if (_failures.Count != 0) throw new InvalidOperationException(string.Join("; ", _failures));
            GD.Print("[native-shadow-ownership] PASS");
            GetTree().Quit(0);
        } catch (Exception error) {
            _failures.Add(error.ToString()); Save(); GD.PrintErr("[native-shadow-ownership] FAIL " + error); GetTree().Quit(1);
        } finally { OrderedWorldCapture.InjectFailure = 0; foreach (var o in _objs) if (!o.IsDestroyed) o.Destroy(); }
    }
}
