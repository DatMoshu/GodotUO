// SPDX-License-Identifier: BSD-2-Clause
#if GUO_LABS
// Labs-only offline proof (task 1149 W1): Labs ground puddles yield to enhanced speed weather only while it
// actually presented on the previous frame, and return the very next frame during a SUSTAINED enhanced
// failure. Britain 1417,1734, scratch profile, never connected, rain forced on the world weather state.
// Thresholds fixed before the first run: MinPuddles in "present" phases, 0 in "yielded" phases; during
// failure frame 1 may be either (one-frame boundary), frames 2..N must all be >= MinPuddles; after the
// injection stops, enhanced must bind within MaxRebind frames and ground must then stay yielded.
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
using GUO.Game.GameObjects;
using GUO.Game.Scenes;
using GUO.Network;
using GUO.Renderer.DepthWorld;
using GUO.Renderer.SpeedMode;
using GUO.Renderer.SpeedMode.Weather;

namespace GUO.Labs.Weather;

public partial class NativeWeatherOwnershipProbe : Node
{
    private const int X = 1417, Y = 1734, MinPuddles = 10, FailFrames = 12, MaxRebind = 10, Settled = 8;
    private const BindingFlags Private = BindingFlags.Instance | BindingFlags.NonPublic;
    private readonly List<object> _rows = new();
    private readonly List<string> _failures = new();
    private string _root;
    private GUO.GameController _game;
    private GameScene _scene;
    private Profile _profile;
    private MethodInfo _draw;
    private World _world;
    private readonly HashSet<ushort> _land = new();
    private int _n;

    private sealed record Fr(int Frame, string Diag, int Puddles, bool Owns, bool Bound, bool Lit, bool Active, string Fallback);

    public override void _EnterTree()
    {
        _root = System.Environment.GetEnvironmentVariable("GUO_WEATHER_OWN_OUT");
        Require(!string.IsNullOrWhiteSpace(_root) && Path.IsPathRooted(_root), "external GUO_WEATHER_OWN_OUT required");
        _root = Path.GetFullPath(_root);
        Require(!_root.StartsWith(Path.GetFullPath(ProjectSettings.GlobalizePath("res://")), StringComparison.OrdinalIgnoreCase), "evidence outside project");
        string scratch = Path.Combine(_root, "fixture");
        Directory.CreateDirectory(scratch);
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
    private void Save() => File.WriteAllText(Path.Combine(_root, "native-weather-ownership-results.json"),
        JsonSerializer.Serialize(new { disconnected = !NetClient.Socket.IsConnected, rows = _rows, failures = _failures },
            new JsonSerializerOptions { WriteIndented = true }));

    // One frame. Owns = what the game thread decided when it queued this frame's Labs surfaces.
    private async Task<Fr> Frame()
    {
        Time.Ticks += 100; // game processing is off, so the probe advances the game clock 100 ms per frame
        _world.Weather.Generate(WeatherType.WT_RAIN, 60, 20); // keeps the rain timer alive
        var st = WeatherLab.For(_world); st.Settings.PuddleTiles.UnionWith(_land);
        bool owns = WeatherLab.EnhancedOwnsGround(_profile);
        _scene.UpdateDrawPosition = true; _scene.UpdateMaxDrawZ(true); _scene.Update();
        _scene.Camera.Update(true, 0, GUO.Input.Mouse.Position); _draw.Invoke(_game, null); await Painted();
        int puddles = 0;
        foreach (var s in st.Surfaces)
            if (s != null && s.QueuedAt == st.LastTicks && !s.Water && (s.Fill > 0 || s.RingAge < 1)) puddles++;
        bool owns2 = WeatherLab.EnhancedOwnsGround(_profile); int queued = st.Surfaces.Count(q => q != null && q.QueuedAt == st.LastTicks); int queuedWater = st.Surfaces.Count(q => q != null && q.QueuedAt == st.LastTicks && q.Water);
        string diag = $"owns2={owns2} queued={queued} water={queuedWater} last={st.LastTicks} now={Time.Ticks} en={st.Settings.Enabled} pud={st.Settings.Puddles} rain={st.Raining} cur={_world.Weather.CurrentWeather} cnt={_world.Weather.Count} tiles={st.Settings.PuddleTiles.Count} surf={st.Surfaces.Count(q => q != null)} maxFill={st.Surfaces.Max(q => q?.Fill ?? 0)} map={st.Map}/{_world.MapIndex}";
        return new Fr(++_n, diag, puddles, owns, SpeedModeWeatherRuntime.LastFrameBound, OrderedWorldCapture.LastFrameEnhancedLit,
            SpeedModeDebug.Active, OrderedWorldCapture.LastFallback);
    }
    private async Task<List<Fr>> Frames(int n) { var l = new List<Fr>(); for (int i = 0; i < n; i++) l.Add(await Frame()); return l; }

    public override async void _Ready()
    {
        try {
            string[] args = OS.GetCmdlineUserArgs();
            Require(!args.Any(a => a is "--account" or "--password" or "--auto-login" or "--autologin" or "--shard-session" or "--window-size"), "no account/shard overrides");
            for (int i = 0; Client.Game?.Scene is not LoginScene; i++) { if (i > 1200) throw new Exception("login timeout"); await ToSignal(GetTree(), SceneTree.SignalName.ProcessFrame); }
            _game = Client.Game; _world = _game.UO.World; var world = _world;
            Require(!NetClient.Socket.IsConnected, "disconnected bootstrap");
            var setup = typeof(NetClient).GetMethod("SetupSocket", Private);
            setup.Invoke(NetClient.Socket, new[] { Enum.Parse(setup.GetParameters()[0].ParameterType, "TcpSocket") });
            Settings.GlobalSettings.ProfilesPath = Path.Combine(_root, "fixture", "profiles");
            typeof(ProfileManager).GetField("_rootPath", BindingFlags.Static | BindingFlags.NonPublic)?.SetValue(null, null);
            ProfileManager.Load("Offline", "native-weather-ownership", "native-weather-ownership");
            _profile = ProfileManager.CurrentProfile;
            Require(Path.GetFullPath(ProfileManager.ProfilePath).StartsWith(_root, StringComparison.OrdinalIgnoreCase), "isolated profile");
            _profile.DefaultScale = 1; _profile.GameWindowFullSize = true; _profile.TopbarGumpIsDisabled = true;
            _profile.UseCircleOfTransparency = false; _profile.NoColorObjectsOutOfRange = false; _profile.EnableDeathScreen = false;
            _profile.LightingSpeedMode = false; _profile.SpeedModeSunShadows = false; _profile.SpeedModeLampShadows = 0;
            _profile.SpeedModeSoften = 0; _profile.SpeedModeWindowLight = false; _profile.SpeedModeWeather = false;
            _profile.SpeedModeFog = false; _profile.SpeedModeMorningHaze = 0; _profile.SpeedModeReflections = false;
            _profile.WaterStyle = "classic"; _profile.WindStrength = 0; _profile.DrawRoofs = false;
            _profile.LocalLightingTestTime = false; _profile.LocalLightingTestAuto = false;
            UoPlusOptions.Set(_profile, UoPlusFeature.Puddles, true, 100);
            world.CreatePlayer(1); world.MapIndex = 0; world.Player.Graphic = 0x190;
            world.Player.SetInWorldTile(X, Y, 20); world.ShardClock.Set(12, 0, 0, Time.Ticks);
            _scene = new GameScene(world); _game.SetScene(_scene); _game.SetWindowSize(1280, 720); _scene.Camera.Zoom = 1;
            for (int i = 0; i < 12; i++) await ToSignal(GetTree(), SceneTree.SignalName.ProcessFrame);
            world.Map.GetMapZ(X, Y, out sbyte ground, out _);
            world.Player.SetInWorldTile(X, Y, (sbyte)(ground + 1));
            for (int dx = -30; dx <= 30; dx++) for (int dy = -30; dy <= 30; dy++)
                if (world.Map.GetTile(X + dx, Y + dy) is { } t) for (var o = t; o != null; o = o.TNext) if (o is Land l) _land.Add(l.Graphic);
            Require(_land.Count > 0, "land graphics around the fixture");
            _draw = _game.GetType().GetMethod("DrawFrame", Private);
            _scene.UpdateDrawPosition = true;
            for (int i = 0; i < 40; i++) await ToSignal(GetTree(), SceneTree.SignalName.ProcessFrame);
            _game.SetProcess(false);
            _rows.Add(new { test = "fixture", x = X, y = Y, landGraphics = _land.Count, resolution = "1280x720" });

            // a) enhanced weather off -> Labs ground puddles present
            await Frames(4); var a = await Frames(Settled);
            _rows.Add(new { test = "a-enhanced-off", frames = a });
            Check(a.All(f => !f.Owns && f.Puddles >= MinPuddles), $"a: puddles >= {MinPuddles} and no ownership on every frame ({string.Join(",", a.Select(f => f.Puddles))})");

            // b) enhanced on and bound -> Labs ground puddles 0
            _profile.LightingSpeedMode = true; _profile.SpeedModeWeather = true;
            var bw = await Frames(Settled * 2); var b = await Frames(Settled);
            _rows.Add(new { test = "b-enhanced-bound", warmup = bw, frames = b });
            Check(b.All(f => f.Owns && f.Bound && f.Lit && f.Puddles == 0), $"b: owns, bound, lit, 0 puddles on every frame ({string.Join(",", b.Select(f => f.Puddles))})");

            // c) sustained failure, injection held on every frame
            OrderedWorldCapture.InjectFailure = 2;
            var c = await Frames(FailFrames);
            _rows.Add(new { test = "c-sustained-failure", frames = c, lastFallback = OrderedWorldCapture.LastFallback });
            Check(c.Skip(1).All(f => f.Puddles >= MinPuddles && !f.Owns), $"c: puddles back from failure frame 2 onward ({string.Join(",", c.Select(f => f.Puddles))})");
            Check(c.Skip(1).All(f => !f.Lit), "c: injection really failed the enhanced frame");

            // d) recovery
            OrderedWorldCapture.InjectFailure = 0;
            var d = await Frames(MaxRebind + 8);
            int bind = d.FindIndex(f => f.Owns);
            _rows.Add(new { test = "d-recovery", frames = d, firstOwnsFrameIndex = bind });
            Check(bind >= 0 && bind < MaxRebind, $"d: ground yields again within {MaxRebind} frames (index {bind})");
            if (bind >= 0)
            {
                Check(d.Skip(bind).All(f => f.Owns && f.Puddles == 0), "d: once yielded, stays yielded with 0 puddles");
                Check(d.Take(bind).All(f => f.Puddles >= MinPuddles), "d: puddles stay present until the handover");
            }
            Check(_profile.LightingSpeedMode && _profile.SpeedModeWeather && UoPlusOptions.Requested(_profile, UoPlusFeature.Puddles), "stored preferences untouched");

            Check(!NetClient.Socket.IsConnected, "never connected"); Save();
            if (_failures.Count != 0) throw new InvalidOperationException(string.Join("; ", _failures));
            GD.Print("[native-weather-ownership] PASS");
            GetTree().Quit(0);
        } catch (Exception error) {
            _failures.Add(error.ToString()); Save(); GD.PrintErr("[native-weather-ownership] FAIL " + error); GetTree().Quit(1);
        } finally { OrderedWorldCapture.InjectFailure = 0; }
    }
}
#endif
