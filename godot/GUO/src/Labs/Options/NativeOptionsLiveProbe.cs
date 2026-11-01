// SPDX-License-Identifier: BSD-2-Clause
// Labs-only offline proof (task 1149 rework): (1) Modern Options SHOWN, GUO rows tapped by synthetic touch ->
// Modern closes, matching UoPlusOptionsGump opens; classic OptionsGump GUO page button -> same;
// (2) in a live offline scratch world (World.InGame) panel Okay writes profile.json via the real
// UoPlusOptionsGump.ApplyChanges path, ProfileManager.Load reloads from disk, reopened panel shows saved values.
// Scratch paths only (GUO_OPTIONS_LIVE_OUT); never connects.
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
using GUO.Game.Scenes;
using GUO.Game.Managers;
using GUO.Game.UI.Controls;
using GUO.Game.UI.Gumps;
using GUO.Input.Touch;
using GUO.Input.Touch.Modern;
using GUO.Network;

namespace GUO.Labs.Options;

public partial class NativeOptionsLiveProbe : Node
{
    private const BindingFlags Priv = BindingFlags.Instance | BindingFlags.NonPublic;
    private readonly List<object> _rows = new();
    private readonly List<string> _failures = new();
    private string _root, _stills;
    private int _n;
    private World _world;

    public override void _EnterTree()
    {
        _root = System.Environment.GetEnvironmentVariable("GUO_OPTIONS_LIVE_OUT");
        if (string.IsNullOrWhiteSpace(_root) || !Path.IsPathRooted(_root)) throw new InvalidOperationException("external GUO_OPTIONS_LIVE_OUT required");
        _root = Path.GetFullPath(_root);
        if (_root.StartsWith(Path.GetFullPath(ProjectSettings.GlobalizePath("res://")), StringComparison.OrdinalIgnoreCase)) throw new InvalidOperationException("evidence outside project");
        string scratch = Path.Combine(_root, "fixture"); _stills = Path.Combine(_root, "stills");
        Directory.CreateDirectory(scratch); Directory.CreateDirectory(_stills);
        GUO.Workspace.Workspace.RootOverride = Path.Combine(scratch, "workspace");
        System.Environment.SetEnvironmentVariable("UO_WORKSPACE_DIR", GUO.Workspace.Workspace.RootOverride);
        Settings.CustomSettingsFilepath = Path.Combine(scratch, "settings.json");
        File.WriteAllText(Settings.CustomSettingsFilepath, JsonSerializer.Serialize(new {
            autologin = false, saveaccount = false, login_music = false, is_win_maximized = false,
            profilespath = Path.Combine(scratch, "profiles"), plugins = Array.Empty<string>() }));
    }

    private void Check(bool ok, string reason) { _rows.Add(new { check = reason, ok }); if (!ok) _failures.Add(reason); }
    private async Task Frames(int n) { for (int i = 0; i < n; i++) await ToSignal(GetTree(), SceneTree.SignalName.ProcessFrame); }
    private void Save() => File.WriteAllText(Path.Combine(_root, "native-options-live-results.json"),
        JsonSerializer.Serialize(new { disconnected = !NetClient.Socket.IsConnected, rows = _rows, failures = _failures },
            new JsonSerializerOptions { WriteIndented = true }));
    private async Task Shot(string name)
    {
        await Frames(3);
        await ToSignal(RenderingServer.Singleton, RenderingServer.SignalName.FramePostDraw);
        using var img = GetViewport().GetTexture().GetImage();
        img.SavePng(Path.Combine(_stills, $"{++_n:00}-{name}.png"));
    }

    private async Task TapClient(Vector2 client)
    {
        var at = client * GUO.Client.Game.DpiScale;
        Godot.Input.ParseInputEvent(new InputEventScreenTouch { Index = 0, Position = at, Pressed = true });
        await Frames(3);
        Godot.Input.ParseInputEvent(new InputEventScreenTouch { Index = 0, Position = at, Pressed = false });
        await Frames(8);
    }

    private static T F<T>(object o, string name) =>
        (T)(o.GetType().GetField(name, Priv) ?? throw new MissingFieldException(name)).GetValue(o);

    private static UoPlusOptionsGump Panel() => UIManager.GetGump<UoPlusOptionsGump>();
    private static GuoSection SectionOf(UoPlusOptionsGump g) => F<GuoSection>(g, "_section");

    private static readonly Dictionary<GuoSection, string> Rows = new() {
        [GuoSection.Lighting] = "Lighting and shadows...", [GuoSection.Weather] = "Weather and ambience...", [GuoSection.World] = "World map and water..." };

    private static void Mutate(UoPlusOptionsGump g, GuoSection s)
    {
        switch (s) {
            case GuoSection.Lighting:
                F<HSliderBar>(g, "_speedSoften").Value = 5;
                F<Checkbox>(g, "_speedSunShadows").IsChecked = false;
                F<Combobox>(g, "_speedLampShadows").SelectedIndex = 2;
                break;
            case GuoSection.Weather:
                F<HSliderBar>(g, "_speedHaze").Value = 4;
                F<HSliderBar>(g, "_speedWind").Value = 35;
                F<HSliderBar>(g, "_cap").Value = 200;
                break;
            case GuoSection.World:
                F<Checkbox>(g, "_overview").IsChecked = true;
                F<Combobox>(g, "_overviewStyle").SelectedIndex = 2;
                F<Combobox>(g, "_touchUi").SelectedIndex = 2;
                break;
        }
    }

    private static Dictionary<string, string> Read(UoPlusOptionsGump g, GuoSection s)
    {
        var d = new Dictionary<string, string>();
        switch (s) {
            case GuoSection.Lighting:
                d["SpeedModeSoften"] = F<HSliderBar>(g, "_speedSoften").Value.ToString();
                d["SpeedModeSunShadows"] = F<Checkbox>(g, "_speedSunShadows").IsChecked.ToString().ToLower();
                d["SpeedModeLampShadows"] = F<Combobox>(g, "_speedLampShadows").SelectedIndex.ToString();
                break;
            case GuoSection.Weather:
                d["SpeedModeMorningHaze"] = F<HSliderBar>(g, "_speedHaze").Value.ToString();
                d["WindStrength"] = F<HSliderBar>(g, "_speedWind").Value.ToString();
                d["AmbientParticleCap"] = F<HSliderBar>(g, "_cap").Value.ToString();
                break;
            case GuoSection.World:
                d["ExperimentalWorldOverview"] = F<Checkbox>(g, "_overview").IsChecked.ToString().ToLower();
                d["WorldOverviewStyle"] = F<Combobox>(g, "_overviewStyle").SelectedIndex.ToString();
                d["TouchUi"] = F<Combobox>(g, "_touchUi").SelectedIndex.ToString();
                break;
        }
        return d;
    }

    private static IEnumerable<GUO.Game.UI.Controls.Control> Walk(GUO.Game.UI.Controls.Control c)
    {
        foreach (var k in c.Children.ToArray()) { yield return k; foreach (var d in Walk(k)) yield return d; }
    }

    private static string Norm(string s) => s.Replace("_", "").ToLowerInvariant();
    private static string Json(JsonElement root, string name)
    {
        foreach (var p in root.EnumerateObject()) if (Norm(p.Name) == Norm(name)) {
            var v = p.Value;
            if (name == "WindStrength" && v.ValueKind == JsonValueKind.Number) return Math.Round(v.GetDouble() * 100).ToString();
            if (name == "TouchUi") return v.ValueKind == JsonValueKind.Null ? "0" : (v.GetBoolean() ? "1" : "2");
            return v.ValueKind switch { JsonValueKind.True => "true", JsonValueKind.False => "false", _ => v.ToString() };
        }
        return "<missing>";
    }

    private static bool SameVals(Dictionary<string, string> want, Func<string, string> got, out string diff)
    {
        var bad = want.Where(kv => got(kv.Key) != kv.Value).Select(kv => $"{kv.Key}: want {kv.Value} got {got(kv.Key)}").ToList();
        diff = string.Join("; ", bad); return bad.Count == 0;
    }

    public override async void _Ready()
    {
        bool touchWas = TouchInput.Enabled;
        try {
            string[] args = OS.GetCmdlineUserArgs();
            if (args.Any(a => a is "--account" or "--password" or "--auto-login" or "--autologin" or "--shard-session")) throw new Exception("no account/shard overrides");
            for (int i = 0; Client.Game?.Scene is not LoginScene; i++) { if (i > 1200) throw new Exception("login timeout"); await Frames(1); }
            var game = Client.Game; _world = game.UO.World;
            if (NetClient.Socket.IsConnected) throw new Exception("must bootstrap disconnected");
            var setup = typeof(NetClient).GetMethod("SetupSocket", Priv);
            setup.Invoke(NetClient.Socket, new[] { Enum.Parse(setup.GetParameters()[0].ParameterType, "TcpSocket") });
            Settings.GlobalSettings.ProfilesPath = Path.Combine(_root, "fixture", "profiles");
            typeof(ProfileManager).GetField("_rootPath", BindingFlags.Static | BindingFlags.NonPublic)?.SetValue(null, null);
            ProfileManager.Load("Offline", "options-live", "options-live");
            string profDir = ProfileManager.ProfilePath;
            if (!Path.GetFullPath(profDir).StartsWith(_root, StringComparison.OrdinalIgnoreCase)) throw new Exception("profile not isolated");
            var profile = ProfileManager.CurrentProfile;
            profile.TopbarGumpIsDisabled = true; profile.LightingSpeedMode = true; profile.ModernGumpsOff = false;
            _world.CreatePlayer(1); _world.MapIndex = 0; _world.Player.Graphic = 0x190;
            _world.Player.SetInWorldTile(1417, 1734, 20); _world.ShardClock.Set(12, 0, 0, Time.Ticks);
            var scene = new GameScene(_world); game.SetScene(scene); game.SetWindowSize(1280, 720);
            await Frames(40);
            Check(_world.InGame, "World.InGame true in the offline fixture");
            _rows.Add(new { test = "fixture", inGame = _world.InGame, profileDir = profDir, uoThemeReady = UoTheme.Ready });

            // (1a) Modern shown, GUO rows tapped by synthetic touch through TouchInput/ModernGump input
            TouchInput.Enable(this, false); TouchInput.Enabled = true;
            foreach (var sec in new[] { GuoSection.Lighting, GuoSection.Weather, GuoSection.World }) {
                UIManager.GetGump<UoPlusOptionsGump>()?.Dispose();
                UIManager.GetGump<OptionsGump>()?.Dispose();
                await Frames(5);
                Game.GameActions.OpenSettings(_world);
                await Frames(20);
                var view = ModernGump.Current as ModernOptions;
                Check(view != null && ModernGump.IsOpen && UIManager.GetGump<OptionsGump>() == null, $"{sec}: Modern Options shown (IsOpen), classic not added");
                if (view == null) continue;
                view.Page("GUO"); await Frames(5);
                if (sec == GuoSection.Lighting) await Shot("modern-shown-guo-page");
                var row = view.Find(Rows[sec]);
                Check(row != null, $"{sec}: GUO row found on Modern GUO page");
                if (row == null) { view.Close(); continue; }
                await TapClient(view.CentreOf(row));
                await Frames(10);
                var p = Panel();
                Check(!ModernGump.IsOpen, $"{sec}: Modern closed after row tap");
                Check(p != null && SectionOf(p) == sec, $"{sec}: matching UoPlusOptionsGump open (touch tap)");
                await Shot($"modern-row-result-{sec}");
            }
            UIManager.GetGump<UoPlusOptionsGump>()?.Dispose();
            TouchInput.Enabled = touchWas; await Frames(5);

            // (1b) classic OptionsGump page 13, GUO button MouseUp handler
            foreach (var sec in new[] { GuoSection.Lighting, GuoSection.Weather, GuoSection.World }) {
                UIManager.GetGump<UoPlusOptionsGump>()?.Dispose();
                UIManager.GetGump<OptionsGump>()?.Dispose();
                await Frames(5);
                Game.GameActions.OpenSettings(_world);
                await Frames(10);
                var opt = UIManager.GetGump<OptionsGump>();
                Check(opt != null && !ModernGump.IsOpen, $"{sec}: classic OptionsGump opened by real entry");
                if (opt == null) continue;
                opt.ChangePage(13); await Frames(5);
                var btn = Walk(opt).OfType<NiceButton>().FirstOrDefault(b => !b.IsDisposed && b.TextLabel.Text == Rows[sec]);
                Check(btn != null, $"{sec}: classic GUO button found");
                if (btn == null) continue;
                if (sec == GuoSection.Lighting) await Shot("classic-guo-page");
                btn.InvokeMouseUp(new GUO.Compat.Point(btn.ScreenCoordinateX + btn.Width / 2, btn.ScreenCoordinateY + btn.Height / 2), GUO.Input.MouseButtonType.Left);
                await Frames(5);
                var p = Panel();
                Check(p != null && SectionOf(p) == sec, $"{sec}: matching UoPlusOptionsGump open (classic MouseUp)");
                await Shot($"classic-row-result-{sec}");
            }
            UIManager.GetGump<UoPlusOptionsGump>()?.Dispose();
            UIManager.GetGump<OptionsGump>()?.Dispose();
            await Frames(5);

            // (2) live save, reload from disk, reopen
            string file = Path.Combine(profDir, "profile.json");
            foreach (var sec in new[] { GuoSection.Lighting, GuoSection.Weather, GuoSection.World }) {
                UoPlusOptionsGump.Open(_world, sec); await Frames(5);
                var g = Panel(); Check(g != null, $"{sec}: panel open for save");
                if (g == null) continue;
                string before = File.Exists(file) ? File.ReadAllText(file) : null;
                var mtimeBefore = File.Exists(file) ? File.GetLastWriteTimeUtc(file) : DateTime.MinValue;
                await Task.Delay(1100);
                Mutate(g, sec); await Frames(3);
                var want = Read(g, sec);
                g.OnButtonClick(4); await Frames(5);
                Check(Panel() == null, $"{sec}: Okay closed the panel");
                Check(File.Exists(file), $"{sec}: profile.json exists at ProfilePath");
                string after = File.Exists(file) ? File.ReadAllText(file) : "";
                Check(File.Exists(file) && File.GetLastWriteTimeUtc(file) > mtimeBefore && after != before, $"{sec}: profile.json mtime/content changed");
                using var doc = JsonDocument.Parse(after.Length > 0 ? after : "{}");
                Check(SameVals(want, k => Json(doc.RootElement, k), out string d1), $"{sec}: profile.json holds panel values ({d1})");
                _rows.Add(new { test = $"{sec}-saved", want, mtimeBefore, mtimeAfter = File.GetLastWriteTimeUtc(file) });

                var old = ProfileManager.CurrentProfile;
                ProfileManager.Load("Offline", "options-live", "options-live");
                Check(!ReferenceEquals(old, ProfileManager.CurrentProfile), $"{sec}: profile reloaded from disk into a fresh Profile");
                UoPlusOptionsGump.Open(_world, sec); await Frames(8);
                var g2 = Panel(); Check(g2 != null && SectionOf(g2) == sec, $"{sec}: panel reopened on reloaded profile");
                if (g2 != null) {
                    var got = Read(g2, sec);
                    Check(SameVals(want, k => got[k], out string d2), $"{sec}: reopened controls show saved values ({d2})");
                    await Shot($"reopened-{sec}");
                    g2.OnButtonClick(1);
                }
                await Frames(3);
            }
            Check(!NetClient.Socket.IsConnected, "never connected");
            Save();
            if (_failures.Count != 0) throw new InvalidOperationException(string.Join("; ", _failures));
            GD.Print("[native-options-live] PASS"); TouchInput.Enabled = touchWas; GetTree().Quit(0);
        } catch (Exception e) {
            _failures.Add(e.ToString()); try { Save(); } catch { }
            GD.PrintErr("[native-options-live] FAIL " + e); TouchInput.Enabled = touchWas; GetTree().Quit(1);
        }
    }
}
