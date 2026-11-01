// SPDX-License-Identifier: BSD-2-Clause
// Labs-only offline proof: the local lighting clock changes the REAL rendered frame
// (sun-shadow mask + ambient), not just the resolver. Britain 1417,1734, no shard/account.
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
using GUO.Renderer;
using GUO.Renderer.DepthWorld;
using GUO.Renderer.SpeedMode.PhysicalWorld;
using GUO.Renderer.SpeedMode.Shadows;
using N2 = System.Numerics.Vector2;

namespace GUO.Labs.LightingClock;

public partial class NativeLightingClockProbe : Node
{
    private const int X = 1417, Y = 1734;
    private const BindingFlags Private = BindingFlags.Instance | BindingFlags.NonPublic;
    private readonly List<object> _rows = new();
    private readonly List<string> _failures = new();
    private readonly List<Item> _items = new();
    private string _root, _stills;
    private GUO.GameController _game;
    private GameScene _scene;
    private Profile _profile;
    private MethodInfo _draw;
    private uint _serial = 0x40010000;
    private int _z;

    private sealed record Frame(byte[] Rgba, byte[] World, int W, int H, string Source);
    private sealed record Stat(int Count, double Cx, double Cy, int MinX, int MaxX, int MinY, int MaxY, double MeanDelta, double Mean);

    public override void _EnterTree()
    {
        _root = System.Environment.GetEnvironmentVariable("GUO_CLOCK_OUT");
        Require(!string.IsNullOrWhiteSpace(_root) && Path.IsPathRooted(_root), "external GUO_CLOCK_OUT required");
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

    private static T Field<T>(object value, string name) => (T)value.GetType().GetField(name, Private).GetValue(value);
    private static void Require(bool ok, string reason) { if (!ok) throw new InvalidOperationException(reason); }
    private void Check(bool ok, string reason) { if (!ok) _failures.Add(reason); }
    private async Task Painted() => await ToSignal(RenderingServer.Singleton, RenderingServer.SignalName.FramePostDraw);
    private void Save() => File.WriteAllText(Path.Combine(_root, "native-lighting-clock-results.json"),
        JsonSerializer.Serialize(new { disconnected = !NetClient.Socket.IsConnected, rows = _rows, failures = _failures },
            new JsonSerializerOptions { WriteIndented = true }));

    private Item Place(ushort graphic, int x, int y, int z)
    {
        Require(_game.UO.Arts.GetArt(graphic).Texture != null, "native art " + graphic.ToString("X4"));
        var item = _game.UO.World.GetOrCreateItem(_serial++);
        item.Graphic = graphic; item.Amount = 1; item.SetInWorldTile((ushort)x, (ushort)y, (sbyte)z);
        item.CheckGraphicChange(); item.AlphaHue = 255; _items.Add(item); return item;
    }

    private void Mutated() { _scene.UpdateDrawPosition = true; _scene.UpdateMaxDrawZ(true); _scene.Update(); }

    private ushort FindWall()
    {
        ref var tiles = ref _game.UO.FileManager.TileData.StaticData;
        for (ushort id = 1, n = 0; id < Math.Min(tiles.Length, 0x4000) && n < 256; id++) {
            var t = tiles[id];
            if (!t.IsWall || t.IsWindow || t.IsDoor || t.IsTransparent || t.IsTranslucent || t.Height < 16 || t.Height > 30) continue;
            n++; if (_game.UO.Arts.GetArt(id).Texture == null) continue;
            var item = Place(id, X - 2, Y, _z);
            var s = NativePhysicalWorldProducer.Build(_game.UO.World, new(1, 1, 0), new(X - 2, Y, 1, 1));
            bool ok = false;
            foreach (var tri in s.Triangles.Span) if (tri.Owner == new PhysicalOwnerKey(PhysicalSourceKind.Item, item.Serial)) ok = true;
            item.Destroy(); if (ok) return id;
        }
        throw new InvalidOperationException("no native wall admitted by the physical producer");
    }

    // Two draws: the first settles any one-frame cache publication, the second is measured.
    private async Task<Frame> Shot(string name, bool sunShadows)
    {
        _profile.SpeedModeSunShadows = sunShadows; Mutated();
        for (int i = 0; i < 2; i++) { _scene.Camera.Update(true, 0, GUO.Input.Mouse.Position); _draw.Invoke(_game, null); await Painted(); }
        using var world = Field<RenderTargets>(_game, "_renderTargets").WorldRenderTarget.Texture.GetImage();
        using var shown = GetViewport().GetTexture().GetImage();
        if (shown.GetFormat() != Image.Format.Rgba8) shown.Convert(Image.Format.Rgba8);
        shown.SavePng(Path.Combine(_stills, name + ".png"));
        return new Frame(shown.GetData(), world.GetData(), shown.GetWidth(), shown.GetHeight(), _scene.SpeedSunSource);
    }

    private static double Lum(byte[] d, int i) => d[i] + d[i + 1] + d[i + 2];
    // Fixture floor (neutral grey 0x0495 tiles) from the shadow-free noon frame; excludes Britain's own buildings.
    private static bool[] FloorMask(Frame noSun)
    {
        var m = new bool[noSun.W * noSun.H];
        for (int i = 0; i < m.Length; i++) {
            int px = i % noSun.W, py = i / noSun.W; if (px < 350 || px > 800 || py < 60 || py > 540) continue; // fixture diamond only
            byte r = noSun.Rgba[i * 4], g = noSun.Rgba[i * 4 + 1], b = noSun.Rgba[i * 4 + 2];
            m[i] = Math.Min(r, Math.Min(g, b)) >= 120 && Math.Max(r, Math.Max(g, b)) - Math.Min(r, Math.Min(g, b)) <= 6;
        }
        return m;
    }
    private static Stat Measure(Frame on, Frame off, bool[] floor, bool[] exclude = null, bool[] mask = null)
    {
        int n = 0, f = 0, x0 = int.MaxValue, x1 = -1, y0 = int.MaxValue, y1 = -1; double sx = 0, sy = 0, sd = 0, mean = 0;
        for (int y = 0; y < on.H; y++) for (int x = 0; x < on.W; x++) {
            if (!floor[y * on.W + x] || exclude != null && exclude[y * on.W + x]) continue;
            int i = (y * on.W + x) * 4; double lo = Lum(off.Rgba, i); mean += lo; f++;
            double d = lo - Lum(on.Rgba, i);
            if (d < Math.Max(3, .1 * lo)) continue; // >=10% darker with sun shadows than without
            if (mask != null) mask[y * on.W + x] = true;
            n++; sx += x; sy += y; sd += d / Math.Max(1, lo); x0 = Math.Min(x0, x); x1 = Math.Max(x1, x); y0 = Math.Min(y0, y); y1 = Math.Max(y1, y);
        }
        return new Stat(n, n > 0 ? sx / n : 0, n > 0 ? sy / n : 0, x0, x1, y0, y1, n > 0 ? sd / n : 0, mean / Math.Max(1, f * 3.0));
    }

    private static N2 Screen(N2 ray) { var s = new N2(ray.X - ray.Y, ray.X + ray.Y); return s / s.Length(); }
    private static double Cos(double ax, double ay, double bx, double by) => (ax * bx + ay * by) / (Math.Sqrt(ax * ax + ay * ay) * Math.Sqrt(bx * bx + by * by) + 1e-12);
    private static (N2 dir, double len) Proj(int h) { var r = SunShadowProjection.FromShardMinute(h * 60); return (Screen(r.Direction), r.Length); }

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
            ProfileManager.Load("Offline", "native-lighting-clock", "native-lighting-clock");
            _profile = ProfileManager.CurrentProfile;
            Require(Path.GetFullPath(ProfileManager.ProfilePath).StartsWith(_root, StringComparison.OrdinalIgnoreCase), "isolated profile");
            _profile.DefaultScale = 1; _profile.GameWindowFullSize = true; _profile.TopbarGumpIsDisabled = true;
            _profile.UseCircleOfTransparency = false; _profile.NoColorObjectsOutOfRange = false; _profile.EnableDeathScreen = false;
            _profile.LightingSpeedMode = true; _profile.SpeedModeSunShadows = true; _profile.SpeedModeLampShadows = 0;
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
            for (int y = -6; y <= 6; y++) for (int x = -6; x <= 6; x++) Place(0x0495, X + x, Y + y, _z);
            ushort wall = FindWall(); int wallHeight = _game.UO.FileManager.TileData.StaticData[wall].Height;
            for (int y = -1; y <= 1; y++) for (int h = 0; h < 60; h += wallHeight) Place(wall, X, Y + y, _z + h);
            _draw = _game.GetType().GetMethod("DrawFrame", Private);
            _scene.UpdateDrawPosition = true;
            for (int i = 0; i < 12; i++) await ToSignal(GetTree(), SceneTree.SignalName.ProcessFrame);
            _game.SetProcess(false);
            _rows.Add(new { test = "fixture", x = X, y = Y, z = _z, floor = 0x0495, wall, wallHeight, resolution = "1280x720" });

            // Resolver cross-check on the same scratch profile the renderer reads.
            _profile.LocalLightingTestTime = true;
            var resolved = new List<object>();
            for (int m = 0; m < 1440; m += 30) {
                LightingTestClock.Seek(_profile, m);
                var r = LightingTestClock.Resolve(_profile, world.ShardClock, .4f, Time.Ticks); var e = SunShadowProjection.FromShardMinute(m);
                Check(r.Direction == e.Direction && r.Length == e.Length && r.Strength == e.Strength && r.Source == "local-test-clock", "resolver==FromShardMinute " + m);
                if (m % 60 == 0) resolved.Add(new { m, dir = new[] { r.Direction.X, r.Direction.Y }, r.Length, r.Strength });
            }
            _rows.Add(new { test = "resolver-cross-check", resolved }); _profile.LocalLightingTestTime = false;

            // Override OFF baseline (shard clock 12:00).
            var offA = await Shot("override-off-before", true); var offANoSun = await Shot("override-off-before-nosun", false);
            Check(offA.Source == "shard-clock", "override off uses shard clock: " + offA.Source);
            var floor = FloorMask(offANoSun); var offStat = Measure(offA, offANoSun, floor);
            _rows.Add(new { test = "override-off-before", offA.Source, offStat });
            Check(offStat.Count >= 200, "baseline shows a rendered sun shadow " + offStat.Count);

            var hours = new[] { 8, 12, 16, 20, 0 }; var stats = new Dictionary<int, Stat>(); var frames = new Dictionary<int, Frame>(); var pairs = new Dictionary<int, (Frame On, Frame Off)>();
            foreach (int h in hours) {
                _profile.LocalLightingTestTime = true; LightingTestClock.Seek(_profile, h * 60);
                var on = await Shot($"h{h:00}-sun", true); var off = await Shot($"h{h:00}-nosun", false);
                frames[h] = on; pairs[h] = (on, off);
                Check(on.Source == "local-test-clock", "scene used local clock at " + h + ": " + on.Source);
            }
            // Pixels darkened at EVERY hour are a static fixture artifact (floor border ring, constant receivers): exclude.
            var common = (bool[])floor.Clone();
            foreach (int h in hours) { var m = new bool[floor.Length]; Measure(pairs[h].On, pairs[h].Off, floor, null, m); for (int i = 0; i < m.Length; i++) common[i] &= m[i]; }
            _rows.Add(new { test = "static-artifact-excluded", pixels = common.Count(c => c) });
            foreach (int h in hours) {
                var s = Measure(pairs[h].On, pairs[h].Off, floor, common); stats[h] = s;
                var sky = LightingTestClock.Resolve(_profile, world.ShardClock, .4f, Time.Ticks); LightingTestClock.Seek(_profile, h * 60); sky = LightingTestClock.Resolve(_profile, world.ShardClock, .4f, Time.Ticks);
                Check(Math.Abs(s.MeanDelta - sky.Strength) < .03, $"rendered darkening {s.MeanDelta:F3} matches resolved strength {sky.Strength} at {h}");
                Check(s.Count >= 20, "rendered shadow exists at " + h + ": " + s.Count);
                var d = Screen(sky.Direction);
                _rows.Add(new { test = "hour", hour = h, source = pairs[h].On.Source, rayDir = new[] { sky.Direction.X, sky.Direction.Y }, screenDir = new[] { d.X, d.Y },
                    sky.Length, sky.Strength, brightness = LightingTestClock.TryBrightness(_profile, out float b) ? b : -1, stat = s });
            }
            // Pairwise rows are informational. Pass criterion: across all five hours the measured centroid
            // follows the projected screen vector (dir*length) linearly (|r| high); sign is reported, not assumed.
            foreach (var (a, b) in new[] { (8, 12), (12, 16), (8, 16), (16, 20), (20, 0) }) {
                var sa = Proj(a); var sb = Proj(b);
                double ex = sb.dir.X * sb.len - sa.dir.X * sa.len, ey = sb.dir.Y * sb.len - sa.dir.Y * sa.len;
                double mx = stats[b].Cx - stats[a].Cx, my = stats[b].Cy - stats[a].Cy;
                _rows.Add(new { test = "centroid-shift", from = a, to = b, measured = new[] { mx, my }, projected = new[] { ex, ey }, cos = Cos(mx, my, ex, ey) });
            }
            double pmx = hours.Average(h => Proj(h).dir.X * Proj(h).len), pmy = hours.Average(h => Proj(h).dir.Y * Proj(h).len);
            double cmx = hours.Average(h => stats[h].Cx), cmy = hours.Average(h => stats[h].Cy), sxy = 0, spp = 0, smm = 0;
            foreach (int h in hours) {
                double px = Proj(h).dir.X * Proj(h).len - pmx, py = Proj(h).dir.Y * Proj(h).len - pmy, mx = stats[h].Cx - cmx, my = stats[h].Cy - cmy;
                sxy += px * mx + py * my; spp += px * px + py * py; smm += mx * mx + my * my;
            }
            double corr = sxy / Math.Sqrt(spp * smm + 1e-12), k = sxy / spp;
            _rows.Add(new { test = "centroid-vs-projection-regression", r = corr, pixelsPerProjectedUnit = k });
            Check(Math.Abs(corr) >= .5, $"measured shadow centroid follows projected sun vector across hours r={corr:F3}");
            Check(Math.Abs(stats[8].Cx - stats[16].Cx) + Math.Abs(stats[8].Cy - stats[16].Cy) > 5, "morning vs afternoon shadow centroid moved >5px");
            Check(stats[12].Mean > stats[0].Mean * 1.5, $"noon ambient brighter than midnight {stats[12].Mean:F1} vs {stats[0].Mean:F1}");
            Check(stats[12].MeanDelta != stats[8].MeanDelta || stats[12].Count != stats[8].Count, "shadow strength/size differs noon vs morning");
            Check(!frames[8].Rgba.AsSpan().SequenceEqual(frames[16].Rgba), "08:00 and 16:00 frames differ");

            // Shard-independence: different shard clock, same override hour, must render identically.
            world.ShardClock.Set(2, 0, 0, Time.Ticks); LightingTestClock.Seek(_profile, 16 * 60);
            var other = await Shot("h16-sun-shard0200", true);
            Check(other.Rgba.AsSpan().SequenceEqual(frames[16].Rgba), "override frame independent of shard clock");

            // Override OFF again, shard 12:00: must reproduce the first non-override frame exactly.
            _profile.LocalLightingTestTime = false; world.ShardClock.Set(12, 0, 0, Time.Ticks);
            var offB = await Shot("override-off-after", true);
            bool presentedSame = offB.Rgba.AsSpan().SequenceEqual(offA.Rgba), worldSame = offB.World.AsSpan().SequenceEqual(offA.World);
            _rows.Add(new { test = "override-off-after", offB.Source, presentedSame, worldSame });
            Check(presentedSame && worldSame, "override OFF reproduces non-override frame exactly");
            // Parent off must ignore the override: equals the classic frame.
            _profile.LocalLightingTestTime = true; _profile.LightingSpeedMode = false;
            var parentOff = await Shot("parent-off-override-on", true);
            _profile.LocalLightingTestTime = false;
            var classic = await Shot("classic", true);
            Check(parentOff.Rgba.AsSpan().SequenceEqual(classic.Rgba), "parent speed mode off: override has no effect (classic frame)");

            Check(!NetClient.Socket.IsConnected, "never connected"); Save();
            if (_failures.Count != 0) throw new InvalidOperationException(string.Join("; ", _failures));
            GD.Print("[native-lighting-clock] PASS rendered sun shadow + ambient follow the local clock; override off/shard-independence exact");
            GetTree().Quit(0);
        } catch (Exception error) {
            _failures.Add(error.ToString()); Save(); GD.PrintErr("[native-lighting-clock] FAIL " + error); GetTree().Quit(1);
        } finally { foreach (var item in _items) if (!item.IsDestroyed) item.Destroy(); }
    }
}
