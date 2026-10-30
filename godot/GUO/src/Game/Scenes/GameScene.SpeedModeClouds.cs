// SPDX-License-Identifier: BSD-2-Clause
// PORT DEVIATION (GUO): opt-in above-cloud view, task1057 / ADR-0045.
using Godot;
using GUO.Configuration;
using GUO.Renderer;
using GUO.Renderer.SpeedMode.Weather;

namespace GUO.Game.Scenes;

internal partial class GameScene
{
    private SpeedModeCloudPass _speedCloudPass;
    private RenderTarget2D _speedCloudTarget, _speedCloudWorld;
    private RenderTargets _speedCloudPublication;
    private long _speedCloudGeneration, _speedCloudFrame;
    private Rid _speedCloudItem;

    // Main-thread only. This target is an independent transparent cloud plane.
    partial void DrawSpeedClouds(UltimaBatcher2D batcher, bool overview)
    {
        var profile = ProfileManager.CurrentProfile;
        var worldTarget = batcher.SpeedWeatherTarget;
        if (profile?.ExperimentalWorldOverview != true || !profile.LightingSpeedMode
            || !_world.InGame || _world.Player == null || !float.IsFinite(Camera.Zoom) || Camera.Zoom <= 4
            || worldTarget == null || worldTarget.IsDisposed || _speedCloudPublication == null)
        {
            ReleaseSpeedClouds();
            return;
        }
        if (_speedCloudWorld != worldTarget) ReleaseSpeedClouds();
        try
        {
            if (_speedCloudPass == null)
            {
                _speedCloudWorld = worldTarget;
                _speedCloudTarget = new RenderTarget2D(worldTarget.Parent, worldTarget.Width, worldTarget.Height);
                _speedCloudTarget.ClearColor = Colors.Transparent;
                _speedCloudGeneration++;
                _speedCloudPass = new SpeedModeCloudPass();
                _speedCloudTarget.ViewportNode.AddChild(_speedCloudPass);
                _speedCloudItem = RenderingServer.CanvasItemCreate();
                RenderingServer.CanvasItemSetParent(_speedCloudItem, _speedCloudTarget.CanvasItem);
                RenderingServer.CanvasItemSetDefaultTextureFilter(_speedCloudItem, RenderingServer.CanvasItemTextureFilter.Nearest);
            }
            float altitude = WorldOverview.CloudAltitudeBlend(Camera.Zoom) * 300;
            var player = _world.Player;
            var origin = SpeedModeCameraOrigin();
            var size = new Vector2I(worldTarget.Width, worldTarget.Height);
            _speedCloudPass.Bind(size, new Vector3(origin.X, origin.Y, player.Z),
                new SpeedModeCloudView(true, Camera.Zoom, altitude),
                GUO.Renderer.Water.WaterRender.Wind(_world, profile), Time.Ticks / 1000.0);
            if (!_speedCloudPass.Visible || _speedCloudPass.Material is not ShaderMaterial material || material.Shader == null)
            {
                ResetSpeedClouds(); return;
            }
            if (overview && _world.Map != null)
            {
                var maps = Client.Game.UO.FileManager.Maps;
                int map = _world.Map.Index;
                var transform = WorldOverview.MapTransform(Camera, _offset, player.Z,
                    maps.MapBlocksSize[map, 0] * 8, maps.MapBlocksSize[map, 1] * 8);
                _speedCloudPass.ProtectPin(transform * new Vector2(player.X + .5f, player.Y + .5f) + new Vector2(0, -32), true);
            }
            else _speedCloudPass.ProtectPin(new Vector2(-1000, -1000), false);
            RenderingServer.CanvasItemSetTransform(_speedCloudItem, Transform2D.Identity);
            RenderingServer.CanvasItemClear(_speedCloudItem);
            RenderingServer.CanvasItemSetMaterial(_speedCloudItem, material.GetRid());
            RenderingServer.CanvasItemSetVisible(_speedCloudItem, true);
            RenderingServer.CanvasItemAddRect(_speedCloudItem, new Rect2(Vector2.Zero, size), Colors.White);
            _speedCloudPass.Visible = false;
            if (!_speedCloudPublication.PublishCloud(this, _speedCloudFrame, worldTarget,
                _speedCloudTarget, _speedCloudGeneration)) ResetSpeedClouds();
        }
        catch
        {
            // Optional cloud failure must not replace the ground light election.
            // If retirement fails, Release throws and retains the live target for retry.
            ReleaseSpeedClouds();
        }
    }

    partial void ResetSpeedClouds()
    {
        _speedCloudPublication?.RevokeCloud(this, _speedCloudTarget, _speedCloudGeneration);
        if (_speedCloudItem.IsValid)
        {
            RenderingServer.CanvasItemClear(_speedCloudItem);
            RenderingServer.CanvasItemSetVisible(_speedCloudItem, false);
        }
        if (_speedCloudPass != null && GodotObject.IsInstanceValid(_speedCloudPass)) _speedCloudPass.HideForFallback();
        var profile = ProfileManager.CurrentProfile;
        if (profile?.ExperimentalWorldOverview != true || !profile.LightingSpeedMode) ReleaseSpeedClouds();
    }

    private void ReleaseSpeedClouds()
    {
        // Revoke logical publication AND clear pooled native/retained final draws
        // before any texture, material, raw item or viewport can be freed.
        _speedCloudPublication?.RevokeCloud(this, _speedCloudTarget, _speedCloudGeneration);
        if (_speedCloudItem.IsValid) RenderingServer.FreeRid(_speedCloudItem);
        _speedCloudItem = default;
        if (_speedCloudPass != null && GodotObject.IsInstanceValid(_speedCloudPass))
        {
            _speedCloudPass.GetParent()?.RemoveChild(_speedCloudPass);
            _speedCloudPass.Free();
        }
        _speedCloudTarget?.Dispose();
        _speedCloudPass = null; _speedCloudTarget = _speedCloudWorld = null;
    }
}
