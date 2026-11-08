// SPDX-License-Identifier: BSD-2-Clause
// Task1057: disconnected diagnostic only; no production composition change.
using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Reflection;
using System.Text.Json;
using System.Threading.Tasks;
using Godot;
using GUO.Configuration;
using GUO.Game.Scenes;
using GUO.Network;

namespace GUO.Renderer.SpeedMode.Weather;

/// <summary>Captures actual world/light/final images; controlled light is explicitly diagnostic.</summary>
public partial class NativeCloudCompositeProbe : Node
{
    private const BindingFlags Private = BindingFlags.Instance | BindingFlags.NonPublic;
    private string _output, _stills;
    private readonly List<object> _frames = new();
    private readonly List<string> _failures = new();
    private GUO.GameController _game;
    private GameScene _scene;
    private RenderTargets _targets;
    private UltimaBatcher2D _batcher;
    private Profile _profile;
    private MethodInfo _draw, _reset;
    private uint _oldTick;
    private bool _tickSaved;
    private ImageTexture _white, _pattern;
    private int _serial;

    private static T Field<T>(object owner, string name) =>
        (T)(owner.GetType().GetField(name, Private)?.GetValue(owner)
            ?? throw new InvalidOperationException("Missing fixture field: " + name));
    private static void Require(bool ok, string text)
    {
        if (!ok) throw new InvalidOperationException(text);
    }
    private async Task Painted() => await ToSignal(RenderingServer.Singleton,
        RenderingServer.SignalName.FramePostDraw);
    private async Task Frame() => await ToSignal(GetTree(), SceneTree.SignalName.ProcessFrame);

    private static string ExternalFolder(string path)
    {
        Require(!string.IsNullOrWhiteSpace(path) && Path.IsPathRooted(path),
            "Set GUO_CLOUD_COMPOSITE_OUT to an absolute external evidence folder.");
        string full = Path.GetFullPath(path);
        string repo = Path.GetFullPath(Path.Combine(ProjectSettings.GlobalizePath("res://"), "..", ".."));
        Require(!string.Equals(full, repo, StringComparison.OrdinalIgnoreCase) &&
            !full.StartsWith(repo + Path.DirectorySeparatorChar, StringComparison.OrdinalIgnoreCase),
            "Evidence must be outside checkout.");
        return full;
    }

    private void Zoom(float value)
    {
        _scene.Camera.SetZoomRange(.5f, 1024, .1f);
        _scene.Camera.Zoom = value;
        _scene.Camera.Update(true, 0, GUO.Input.Mouse.Position);
    }

    // Controlled classic Multiply input. This is NOT a native sun/lamp mask.
    private static readonly BlendState DiagnosticMultiply = new()
    {
        ColorSourceBlend = Blend.DestinationColor,
        ColorDestinationBlend = Blend.Zero,
        AlphaSourceBlend = Blend.Zero,
        AlphaDestinationBlend = Blend.One
    };

    private void DiagnosticFrame(bool reset, Texture2D light)
    {
        bool complete = false;
        _targets.BeginCloudFrame(_scene, _batcher);
        _batcher.BeginFrame(deferRetainedHiding: true);
        try
        {
            _scene.Draw(_batcher, _targets);
            if (reset) _reset.Invoke(_scene, null);
            var previousOverride = _targets.LightOverride;
            var previousSpeedBlend = _targets.SpeedLightBlend;
            var previousBlend = (Func<BlendState>)typeof(RenderTargets).GetField("_lightsBlendState", Private).GetValue(_targets);
            var previousHue = (Func<Vector3>)typeof(RenderTargets).GetField("_lightsHue", Private).GetValue(_targets);
            // Deliberately bypass speed branch election only in this diagnostic frame.
            // Actual frames below retain the ordinary DrawFrame election.
            try
            {
                _targets.SpeedLightBlend = null;
                _targets.LightOverride = light;
                _targets.SetLightsConfiguration(() => DiagnosticMultiply, () => new Vector3(0, 0, 1));
                _batcher.SetRenderTarget(null);
                _targets.Draw(_batcher);
            }
            finally
            {
                _targets.LightOverride = previousOverride; _targets.SpeedLightBlend = previousSpeedBlend;
                _targets.SetLightsConfiguration(previousBlend, previousHue);
            }
            TextureAtlas.FlushAll();
            LandPages.Sync();
            complete = true;
        }
        finally { _batcher.EndFrame(complete); }
    }

    private async Task<(byte[] World, byte[] Final, Image.Format FinalFormat)> Capture(string name, bool reset = false, Texture2D diagnostic = null)
    {
        Require(!NetClient.Socket.IsConnected, "Fixture unexpectedly connected.");
        if (diagnostic == null)
        {
            _draw.Invoke(_game, null);
            if (reset) _reset.Invoke(_scene, null);
        }
        else DiagnosticFrame(reset, diagnostic);
        await Painted();
        using var world = _targets.WorldRenderTarget.Texture.GetImage();
        using var light = _targets.LightRenderTarget.Texture.GetImage();
        using var final = GetViewport().GetTexture().GetImage();
        Require(final.GetWidth() == 1280 && final.GetHeight() == 720,
            "Final root capture must be actual 1280x720.");
        string prefix = $"guo_1057_{++_serial:000}-{name}_20261030";
        Save(world, prefix + "-world"); Save(light, prefix + "-classic-light"); Save(final, prefix + "-final");
        if (diagnostic != null)
        {
            using var input = diagnostic.GetImage(); Save(input, prefix + "-diagnostic-light");
        }
        _frames.Add(new { serial = _serial, name, tick = Time.Ticks, zoom = _scene.Camera.Zoom,
            overview = _scene.OverviewActive, cloudsReset = reset,
            source = diagnostic == null ? "actual-DrawFrame-no-extra-ground-effects" : "synthetic-multiply-RenderTargets.Draw",
            worldWidth = world.GetWidth(), worldHeight = world.GetHeight(),
            finalWidth = final.GetWidth(), finalHeight = final.GetHeight(),
            finalFormat = final.GetFormat().ToString(),
            worldFile = prefix + "-world.png", finalFile = prefix + "-final.png" });
        Results();
        return (world.GetData(), final.GetData(), final.GetFormat());
    }

    private void Save(Image image, string name) =>
        Require(image.SavePng(Path.Combine(_stills, name + ".png")) == Error.Ok, "PNG save failed: " + name);
    private void Results() => File.WriteAllText(Path.Combine(_output, "data", "composite-results.json"),
        JsonSerializer.Serialize(new { status = "diagnostic-not-visual-acceptance", disconnected = !NetClient.Socket.IsConnected,
            frames = _frames, failures = _failures }, new JsonSerializerOptions { WriteIndented = true }));

    public override async void _Ready()
    {
#if !DEBUG
        GD.PrintErr("[cloud-composite] Debug-only diagnostic."); GetTree().Quit(1); return;
#else
        try
        {
            CloudPublicationStateRegression.Run();
            Require(DisplayServer.GetName() != "headless", "Real renderer required.");
            _output = ExternalFolder(System.Environment.GetEnvironmentVariable("GUO_CLOUD_COMPOSITE_OUT"));
            _stills = Path.Combine(_output, "stills");
            string scratch = Path.Combine(_output, "data", "fixture");
            var args = OS.GetCmdlineUserArgs();
            Require(!args.Any(a => a is "--account" or "--password" or "--auto-login" or "--autologin" or "--shard-session"),
                "No login/account arguments permitted.");
            int cache = Array.IndexOf(args, "--cache-dir");
            Require(cache >= 0 && cache + 1 < args.Length &&
                string.Equals(Path.GetFullPath(args[cache + 1]), Path.Combine(scratch, "cache"), StringComparison.OrdinalIgnoreCase),
                "Explicit isolated --cache-dir required.");
            Directory.CreateDirectory(_stills); Directory.CreateDirectory(scratch);
            GUO.Workspace.Workspace.RootOverride = Path.Combine(scratch, "workspace");
            Settings.CustomSettingsFilepath = Path.Combine(scratch, "settings.json");
            File.WriteAllText(Settings.CustomSettingsFilepath, JsonSerializer.Serialize(new {
                autologin = false, saveaccount = false, login_music = false, is_win_maximized = false,
                profilespath = Path.Combine(scratch, "profiles"), plugins = Array.Empty<string>() }));
            AddChild(new GUO.Host.Main());
            for (int i = 0; Client.Game?.Scene is not LoginScene; i++)
            {
                Require(i < 1200, "Login bootstrap timeout."); await Frame();
            }
            _game = Client.Game;
            Require(!NetClient.Socket.IsConnected, "Disconnected bootstrap required.");
            var setup = typeof(NetClient).GetMethod("SetupSocket", Private);
            setup.Invoke(NetClient.Socket, new[] { Enum.Parse(setup.GetParameters()[0].ParameterType, "TcpSocket") });
            Settings.GlobalSettings.ProfilesPath = Path.Combine(scratch, "profiles");
            typeof(ProfileManager).GetField("_rootPath", BindingFlags.Static | BindingFlags.NonPublic)
                .SetValue(null, Settings.GlobalSettings.ProfilesPath);
            ProfileManager.Load("Offline", "cloud-composite-1057", "cloud-composite-1057");
            Require(Path.GetFullPath(ProfileManager.ProfilePath).StartsWith(scratch + Path.DirectorySeparatorChar,
                StringComparison.OrdinalIgnoreCase), "Fixture profile escaped scratch folder.");
            _profile = ProfileManager.CurrentProfile;
            _profile.DefaultScale = 1; _profile.WindowBorderless = false;
            Settings.GlobalSettings.IsWindowMaximized = false;
            DisplayServer.WindowSetMode(DisplayServer.WindowMode.Windowed);
            DisplayServer.WindowSetSize(new Vector2I(1280, 720));
            _profile.GameWindowFullSize = true; _profile.TopbarGumpIsDisabled = true;
            _profile.UseCircleOfTransparency = false; _profile.NoColorObjectsOutOfRange = false;
            _profile.ExperimentalWorldOverview = true; _profile.LightingSpeedMode = true;
            _profile.SpeedModeSunShadows = false; _profile.SpeedModeLampShadows = 0;
            _profile.SpeedModeWindowLight = false; _profile.SpeedModeWeather = false;
            _profile.SpeedModeFog = false; _profile.SpeedModeReflections = false;
            _profile.WaterStyle = "classic"; _profile.WindStrength = 0;
            var world = _game.UO.World;
            world.CreatePlayer(1); world.MapIndex = 0; world.Player.Graphic = 0x190;
            world.Player.Name = "Disconnected cloud composite diagnostic";
            world.Player.SetInWorldTile(1431, 1691, 20);
            _scene = new GameScene(world); _game.SetScene(_scene); _game.SetWindowSize(1280, 720);
            for (int i = 0; i < 12; i++) await Frame();
            _game.SetProcess(false); _oldTick = Time.Ticks; _tickSaved = true; Time.Ticks = 500000;
            _draw = _game.GetType().GetMethod("DrawFrame", Private);
            _reset = typeof(GameScene).GetMethod("ResetSpeedClouds", Private);
            _targets = Field<RenderTargets>(_game, "_renderTargets");
            _batcher = Field<UltimaBatcher2D>(_game, "_uoSpriteBatch");
            foreach (float zoom in new[] { 4f, 4.25f, 4.5f, 5f, 6f, 5f, 4.5f, 4.25f, 4f })
            {
                Zoom(zoom);
                // Overview cache can need several actual draws to settle; never compare across zooms.
                for (int i = 0; i < (zoom > 5 ? 40 : 8); i++) { _draw.Invoke(_game, null); await Painted(); }
                await Capture("actual-reset", true); await Capture("actual-cloud");
            }
            Zoom(5);
            for (int i = 0; i < 8; i++) { _draw.Invoke(_game, null); await Painted(); }
            using (var white = Image.CreateEmpty(1280, 720, false, Image.Format.Rgba8))
            using (var pattern = Image.CreateEmpty(1280, 720, false, Image.Format.Rgba8))
            {
                white.Fill(Colors.White); pattern.Fill(Colors.White);
                pattern.FillRect(new Rect2I(320, 0, 320, 720), new Color(.2f, .2f, .2f, 1));
                _white = ImageTexture.CreateFromImage(white); _pattern = ImageTexture.CreateFromImage(pattern);
            }
            foreach (bool reset in new[] { false, true })
            {
                // Repeat first to settle same-frame producer/cache state, then compare raw world.
                await Capture("diagnostic-warm", reset, _white);
                var before = await Capture("diagnostic-white", reset, _white);
                var after = await Capture("diagnostic-pattern", reset, _pattern);
                Require(before.World.AsSpan().SequenceEqual(after.World), "Controlled final-light change altered pre-light world.");
                Require(!before.Final.AsSpan().SequenceEqual(after.Final), "Controlled final-light source had no final-output witness.");
            }
            if (System.Environment.GetEnvironmentVariable("GUO_CLOUD_NATIVE_LIFETIME") == "1")
                await QualifyNativeLifetime();
            _game.SetScene(new LoginScene(world)); await Painted();
            Require(!NetClient.Socket.IsConnected, "Fixture stayed disconnected.");
            Results(); GD.Print("[cloud-composite] CAPTURED; native attribution and visual acceptance still required.");
            GetTree().Quit(0);
        }
        catch (Exception error)
        {
            _failures.Add(error.ToString());
            if (_output != null && Directory.Exists(Path.Combine(_output, "data"))) Results();
            GD.PrintErr("[cloud-composite] FAIL " + error); GetTree().Quit(1);
        }
        finally
        {
            if (_tickSaved) Time.Ticks = _oldTick;
            if (_targets != null) _targets.LightOverride = null;
            _white?.Dispose(); _pattern?.Dispose();
        }
#endif
    }
}
