// SPDX-License-Identifier: BSD-2-Clause
using Godot;
using GUO.Configuration;
using GUO.Game;

namespace GUO.Renderer.SpeedMode.Weather;

/// <summary>
/// Consumes one borrowed core ticket. Example: after publishing this frame's
/// light target, call BindCurrent with that sequence and the capture's surface.
/// The owner places passes before UI and calls Clear on every off/abort frame.
/// This adapter does not schedule a render or retain the borrowed core textures.
/// </summary>
internal static class SpeedModeWeatherTargets
{
    internal static bool BindCurrent(World world, Profile profile, SpeedModeWeather state,
        Texture2D expectedSurface, long expectedSequence, uint frameTick,
        Texture2D sceneColor, Vector2I size, Vector3 cameraWorld, float artScale,
        double seconds, SpeedModeRainPass rain, SpeedModeWetPass wet, SpeedModeFogPass fog,
        UltimaBatcher2D batcher = null)
    {
        var frame = SpeedModeBindings.Current;
        if (world == null || state == null || profile?.LightingSpeedMode != true
            || frameTick != Time.Ticks || expectedSequence <= 0 || frame.Sequence != expectedSequence
            || expectedSurface == null || !ReferenceEquals(frame.Surface, expectedSurface)
            || frame.LightTarget == null || size.X <= 0 || size.Y <= 0
            || frame.Surface.GetWidth() != size.X || frame.Surface.GetHeight() != size.Y
            || frame.LightTarget.GetWidth() != size.X || frame.LightTarget.GetHeight() != size.Y
            || frame.TargetPixelBounds != new Vector4(0, 0, size.X, size.Y))
        {
            Clear(rain, wet, fog, batcher);
            return false;
        }
        if (profile.SpeedModeWeather)
            rain.Bind(frame.Surface, size, state.RainIntensity, state.Wind, seconds, world, frame.Origin);
        else rain.HideForFallback();
        wet.Bind(frame.Surface, frame.LightTarget, size, frame.Origin,
            profile.SpeedModeWeather ? state.Wetness : 0,
            profile.SpeedModeWeather ? state.RainIntensity : 0, seconds, artScale, profile.SpeedModeReflections);
        fog.Bind(frame.Surface, size, frame.Origin, cameraWorld, state.FogDensity,
            profile.SpeedModeMorningHaze, state.Wind, seconds, profile.SpeedModeFog);
        if (batcher != null)
        {
            Water.WaterRender.BindLightTarget(batcher, frame.LightTarget, frame.TargetPixelBounds, frame.Ambient);
            Water.WaterRender.BindReflectionTargets(batcher, frame.Surface, sceneColor, size,
                frame.Origin, artScale, profile.SpeedModeReflections);
        }
        return true;
    }

    internal static void Clear(SpeedModeRainPass rain, SpeedModeWetPass wet, SpeedModeFogPass fog,
        UltimaBatcher2D batcher = null)
    {
        rain.HideForFallback();
        wet.HideForFallback();
        fog.HideForFallback();
        if (batcher != null) Water.WaterRender.ClearLightTarget(batcher);
    }
}
