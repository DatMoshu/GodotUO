// SPDX-License-Identifier: BSD-2-Clause
#if GUO_LABS
using System;
using Godot;
using GUO.Game;
using GUO.Renderer.Weather;
using GUO.Renderer.Water;

namespace GUO.Labs.WaterRain;

public partial class WaterRainProbe : Node
{
    public override void _Ready()
    {
        try
        {
            Require(!new WeatherSettings().WaterDrops && !WaterRainLab.IsActive(null),
                "water rain must default off and cannot suppress primitives without an initialized world");
            var field = new WaterRipples();
            var rain = new RainRippleEmitter();
            int queries = 0;
            bool Wet(int x, int y) { queries++; return true; }
            rain.Update(field, 1, 1, true, 8, 1000, 1000, Wet);
            Require(queries == 0 && field.Stamps == 0, "an uninitialized field must not query or stamp");
            field.CentreOnForProbe(1000, 1000);
            foreach (float intensity in new[] { 0f, -1f, float.NaN, float.PositiveInfinity })
                rain.Update(field, 1, intensity, true, 8, 1000, 1000, Wet);
            rain.Update(field, 1, 1, false, 8, 1000, 1000, Wet);
            rain.Update(field, 1, 1, true, 0, 1000, 1000, Wet);
            field.Strength = 0;
            rain.Update(field, 1, 1, true, 8, 1000, 1000, Wet);
            field.Strength = 1;
            foreach (float delta in new[] { 0f, -1f, float.NaN, float.PositiveInfinity })
                rain.Update(field, delta, 1, true, 8, 1000, 1000, Wet);
            Require(queries == 0 && field.Stamps == 0, "off/dry/invalid input/zero budget/zero strength must do no work");
            rain.Update(field, 10, 100, true, 999, 1000, 1000, Wet);
            Require(rain.LastAttempts == 8 && rain.LastStamps == 8 && field.Stamps == 8,
                "stalled frames, intensity and budget must be capped");
            float height = 0;
            for (int x = 981; x <= 1019; x++)
            for (int y = 981; y <= 1019; y++) height = Math.Max(height, Math.Abs(field.HeightAt(x, y)));
            Require(height > 0.01f, "rain must disturb the real wave field");
            rain.Update(field, 0.01f, 1, false, 8, 1000, 1000, Wet);
            int was = field.Stamps;
            rain.Update(field, 0.01f, 1, true, 8, 1000, 1000, Wet);
            Require(field.Stamps == was, "re-enabling must not release a stalled-frame backlog");
            rain.Reset();
            for (int i = 0; i < 600; i++)
                rain.Update(field, 1f / 60, 1, true, 8, 1000, 1000, (_, _) => false);
            Require(field.Stamps == was, "dry/covered/unloaded cells must never receive rain");
            rain.Update(field, 0.001f, 1, true, 8, 1000, 1000, Wet);
            Require(rain.LastAttempts == 0, "reaching water must not release rain collected over land");
            foreach (float intensity in new[] { 0.25f, 1f })
            {
                int expected = (int)(10 * RainRippleEmitter.ImpactsPerSecond * intensity);
                foreach (int fps in new[] { 30, 60, 144 })
                    Require(Math.Abs(Count(fps, intensity) - expected) <= 1,
                        $"rain intensity {intensity} at {fps} FPS must follow seconds, not frames");
            }
            Require(Math.Abs(Count(60, WeatherLab.RainIntensity(WeatherType.WT_RAIN, 35)) - 200) <= 1,
                "the shared weather intensity must drive the actual rain consumer");
            Require(Count(60, WeatherLab.RainIntensity(WeatherType.WT_SNOW, 70)) == 0,
                "snow from the shared provider must never disturb water");
            var surface = new RainSurface();
            Require(!surface.Exposed, "unknown and dry cells are ineligible");
            surface.Add(false, true, -20);
            Require(!surface.Exposed, "dry land is ineligible");
            surface.Add(true, false, -5);
            Require(surface.Exposed, "known water above seabed is eligible");
            surface.Add(false, true, 0);
            Require(!surface.Exposed, "a roof, pier, multi or raised ground blocks lower water");
            surface.Add(true, false, 5);
            Require(surface.Exposed, "the highest Wet surface is considered independent of list order");
            rain.Reset();
            rain.Update(field, 0.02f, 1, true, 8, 1000, 1000, Wet);
            rain.Reset();
            rain.Update(field, 0.01f, 1, true, 8, 1000, 1000, Wet);
            Require(rain.LastAttempts == 0, "map/style/reset transitions must discard fractional rain credit");
            GD.Print("[water rain] PASS: inert off/dry/invalid/uninitialized paths, real-field disturbance, caps, no backlog, intensity and 30/60/144 FPS rate, exposed-water height policy, reset");
            GetTree().Quit();
        }
        catch (Exception ex) { GD.PrintErr("[water rain] FAIL: " + ex); GetTree().Quit(1); }
    }

    private static int Count(int fps, float intensity)
    {
        var field = new WaterRipples();
        field.CentreOnForProbe(1000, 1000);
        var rain = new RainRippleEmitter();
        for (int i = 0; i < fps * 10; i++)
            rain.Update(field, 1f / fps, intensity, true, 8, 1000, 1000, (_, _) => true);
        return field.Stamps;
    }

    private static void Require(bool ok, string message)
    {
        if (!ok) throw new InvalidOperationException(message);
    }
}
#endif
