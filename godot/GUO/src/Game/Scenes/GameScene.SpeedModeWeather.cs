// SPDX-License-Identifier: BSD-2-Clause
// PORT DEVIATION (GUO): ADR-0048, same-frame weather and fallback ownership.
using Godot;
using GUO.Configuration;
using GUO.Renderer;
using GUO.Renderer.SpeedMode;
using GUO.Renderer.SpeedMode.Weather;

namespace GUO.Game.Scenes;

internal partial class GameScene
{
    private SpeedModeWeatherRuntime _speedWeatherRuntime;

    partial void BindSpeedLightTarget(UltimaBatcher2D batcher, Texture2D lightTarget,
        Vector4 targetPixelBounds, Vector3 ambient)
        => GUO.Renderer.Water.WaterRender.BindLightTarget(batcher, lightTarget, targetPixelBounds, ambient);

    partial void ClearSpeedLightTarget(UltimaBatcher2D batcher)
    {
        _speedWeatherRuntime?.ResetFrame();
        GUO.Renderer.Water.WaterRender.ClearLightTarget(batcher);
    }

    private void ResetSpeedWeatherFrame()
    {
        if (_speedWeatherRuntime == null) return;
        if (!GodotObject.IsInstanceValid(_speedWeatherRuntime)) { _speedWeatherRuntime = null; return; }
        if (ProfileManager.CurrentProfile?.LightingSpeedMode != true || !SpeedModeDebug.Supported)
            ReleaseSpeedWeather();
        else _speedWeatherRuntime.ResetFrame();
    }

    private void ReleaseSpeedWeather()
    {
        if (_speedWeatherRuntime == null) return;
        if (GodotObject.IsInstanceValid(_speedWeatherRuntime))
        {
            _speedWeatherRuntime.Release();
            _speedWeatherRuntime.GetParent()?.RemoveChild(_speedWeatherRuntime);
            _speedWeatherRuntime.Free();
        }
        _speedWeatherRuntime = null;
    }

    private void DrawSpeedWeather(UltimaBatcher2D batcher, RenderTargets targets,
        Transform2D view, float layerDepth)
    {
        var profile = ProfileManager.CurrentProfile;
        var target = targets.WorldRenderTarget;
        if (profile?.LightingSpeedMode != true || !SpeedModeDebug.Supported || SpeedModeDebug.GBuffer
            || target == null || target.IsDisposed || batcher.SpeedWeatherSurface == null)
        {
            ReleaseSpeedWeather();
            _world.Weather.Draw(batcher, 0, 0, layerDepth);
            return;
        }
        if (_speedWeatherRuntime != null && _speedWeatherRuntime.Target != target) ReleaseSpeedWeather();
        if (_speedWeatherRuntime == null)
        {
            _speedWeatherRuntime = new SpeedModeWeatherRuntime();
            _speedWeatherRuntime.Initialize(target);
            target.ViewportNode.AddChild(_speedWeatherRuntime);
        }
        UltimaBatcher2D.WeatherLines classic = default;
        if (profile.SpeedModeWeather && (_world.Weather.Type == WeatherType.WT_RAIN
            || _world.Weather.Type == WeatherType.WT_STORM_APPROACH))
            classic = batcher.BeginSpeedWeatherLines();
        try { _world.Weather.Draw(batcher, 0, 0, layerDepth); }
        finally { if (classic.Item.IsValid) batcher.EndSpeedWeatherLines(); }
        // Observe after the classic wind/expiry update, preserving its state.
        var state = SpeedModeWeather.Observe(_world, profile);
        var origin = SpeedModeCameraOrigin();
        _speedWeatherRuntime.Arm(_world, profile, state, batcher, classic,
            batcher.SpeedWeatherSurface, batcher.SpeedWeatherColor, view,
            new Vector3(origin.X, origin.Y, _world.Player.Z), 1f / Camera.Zoom, Time.Ticks / 1000.0);
    }
}
