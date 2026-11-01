// SPDX-License-Identifier: BSD-2-Clause
// Task1057 disconnected actual GameScene overlay lifetime; probe-only reflection.
using System;
using System.Reflection;
using System.Threading.Tasks;
using Godot;
using GUO.Game.Scenes;

namespace GUO.Renderer.SpeedMode.Weather;

public partial class NativeCloudCompositeProbe
{
    private static object OptionalField(object value, string field) => value.GetType().GetField(field, Private).GetValue(value);
    private async Task NativeWarm(int count = 6)
    {
        for (int i = 0; i < count; i++) { _draw.Invoke(_game, null); await Painted(); }
    }
    private void NoBorrow(string reason) => Require(OptionalField(_batcher, "_cloudDrawTarget") == null, reason);

    private async Task QualifyNativeLifetime()
    {
        bool oldRetained = UltimaBatcher2D.RetainCanvas;
        var world = _game.UO.World;
        try
        {
            await QualifyNativeView();
            foreach (bool retained in new[] { false, true })
            {
                UltimaBatcher2D.RetainCanvas = retained;
                _profile.ExperimentalWorldOverview = true; _profile.LightingSpeedMode = false;
                Zoom(5); await NativeWarm();
                var before = await Capture("native-parent-off-before-" + retained);
                _profile.LightingSpeedMode = true; await NativeWarm();
                await Capture("native-active-" + retained);
                var overlay = Field<RenderTarget2D>(_scene, "_speedCloudTarget");
                Require(ReferenceEquals(OptionalField(_batcher, "_cloudDrawOwner"), _scene), "actual active cloud borrower missing");

                // The real scene early-return path, after active foreground output.
                var player = world.Player;
                var property = world.GetType().GetProperty("Player", BindingFlags.Instance | BindingFlags.Public);
                _targets.BeginCloudFrame(_scene, _batcher); _batcher.BeginFrame(deferRetainedHiding: true);
                try
                {
                    property.SetValue(world, null);
                    Require(!_scene.Draw(_batcher, _targets), "actual no-player scene did not early-return");
                }
                finally { property.SetValue(world, player); _batcher.EndFrame(); }
                NoBorrow("native early return retained cloud borrow");

                // Actual world production/final draw followed by controller abort discipline.
                _targets.BeginCloudFrame(_scene, _batcher); _batcher.BeginFrame(deferRetainedHiding: true);
                try
                {
                    _scene.Draw(_batcher, _targets); _batcher.SetRenderTarget(null); _targets.Draw(_batcher);
                    Require(ReferenceEquals(OptionalField(_batcher, "_cloudDrawOwner"), _scene), "abort fixture has no active overlay");
                }
                finally { _targets.ClearCloudFrame(); _batcher.EndFrame(false); }
                NoBorrow("native abort retained cloud borrow"); await Painted();
                await NativeWarm(); await Capture("native-after-abort-" + retained);

                _profile.LightingSpeedMode = false; await NativeWarm();
                var after = await Capture("native-parent-off-return-" + retained);
                Require(before.Final.AsSpan().SequenceEqual(after.Final), "native parent-off return differs");
                Require(overlay.IsDisposed, "actual off did not dispose cloud overlay");
                NoBorrow("native off retained freed target");
                _frames.Add(new { test = "native-active-lifetime", retained, earlyReturn = true, abort = true,
                    parentOffExact = true, freedOverlay = true }); Results();
            }

            _profile.LightingSpeedMode = true; Zoom(5); await NativeWarm();
            var oldScene = _scene;
            var oldOverlay = Field<RenderTarget2D>(_scene, "_speedCloudTarget");
            long oldGeneration = Field<long>(_scene, "_speedCloudGeneration");
            _draw.Invoke(_game, null); // unload BEFORE queued capture reaches FramePreDraw
            _game.SetScene(new LoginScene(world));
            NoBorrow("queued native unload retained cloud target");
            Require(oldOverlay.IsDisposed && OptionalField(oldScene, "_speedCloudPass") == null,
                "native scene unload did not free cloud consumer");
            await Painted();
            Require(SpeedModeBindings.Current.Surface == null && SpeedModeBindings.Current.LightTarget == null,
                "queued old scene republished speed bindings");
            GUO.Configuration.ProfileManager.Load("Offline", "cloud-composite-1057", "cloud-composite-1057");
            _profile = GUO.Configuration.ProfileManager.CurrentProfile;
            _profile.LightingSpeedMode = true; _profile.ExperimentalWorldOverview = true;
            world.CreatePlayer(1); world.MapIndex = 0; world.Player.Graphic = 0x190;
            world.Player.Name = "Disconnected cloud owner reuse"; world.Player.SetInWorldTile(1431, 1691, 20);
            _scene = new GameScene(world); _game.SetScene(_scene); _game.SetProcess(false);
            Zoom(5); await NativeWarm(12);
            var current = await Capture("native-new-owner");
            _targets.RevokeCloud(oldScene, oldOverlay, oldGeneration);
            Require(ReferenceEquals(OptionalField(_batcher, "_cloudDrawOwner"), _scene), "old scene release cleared new scene");
            await Painted();
            using (var actual = GetViewport().GetTexture().GetImage())
                Require(current.Final.AsSpan().SequenceEqual(actual.GetData()), "old scene release changed new frame");

            var prior = Field<RenderTarget2D>(_scene, "_speedCloudTarget");
            DisplayServer.WindowSetSize(new Vector2I(960, 640));
            _game.SetWindowSize(960, 640); await NativeWarm();
            var resized = Field<RenderTarget2D>(_scene, "_speedCloudTarget");
            Require(prior.IsDisposed && !ReferenceEquals(prior, resized) && resized.Width == 960 && resized.Height == 640,
                "native resize did not retire/recreate cloud target");
            Require(ReferenceEquals(OptionalField(_batcher, "_cloudDrawTarget"), resized), "native resize sampled former target");
            DisplayServer.WindowSetSize(new Vector2I(1280, 720)); _game.SetWindowSize(1280, 720);
            await NativeWarm(); await Capture("native-resize-return");
            _frames.Add(new { test = "native-queued-unload-new-owner-resize", queued = true, oldOwnerRelease = true,
                freedConsumer = true, resized = true, surfaceRetired = true }); Results();
        }
        finally { UltimaBatcher2D.RetainCanvas = oldRetained; }
    }
}
