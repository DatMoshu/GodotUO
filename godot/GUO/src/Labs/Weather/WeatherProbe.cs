// SPDX-License-Identifier: BSD-2-Clause
#if GUO_LABS
using System;
using Godot;
using GUO.Game;

namespace GUO.Labs.Weather;
public partial class WeatherProbe : Node
{
    public override void _Ready()
    {
        try
        {
            var state = new WeatherState();
            Require(!state.Settings.Enabled, "all three effects must default off");
            state.Settings.WaterDrops = true;
            Require(!state.Settings.Puddles && !state.Settings.WetPlayer, "toggles must be independent");
            Require(WeatherLab.RainIntensity(null) == 0, "unavailable weather must be dry");
            Require(WeatherLab.RainIntensity(WeatherType.WT_RAIN, 35) == 0.5f &&
                WeatherLab.RainIntensity(WeatherType.WT_STORM_BREWING, 70) == 1,
                "rain and brewing intensity must use upstream's seventy-drop range");
            Require(WeatherLab.RainIntensity(WeatherType.WT_RAIN, 255) == 1 &&
                WeatherLab.RainIntensity(WeatherType.WT_RAIN, -1) == 0,
                "intensity must be clamped");
            foreach (var type in new WeatherType?[] { null, WeatherType.WT_SNOW,
                WeatherType.WT_STORM_APPROACH, WeatherType.WT_INVALID_0, WeatherType.WT_INVALID_1 })
                Require(WeatherLab.RainIntensity(type, 70) == 0, "non-rain events must not stamp water");
            state.Settings.WetPlayer = true;
            for (int i = 0; i < 240; i++) state.Step(1f / 60, true);
            Require(state.Wetness > 0.99f, "wetness must fill over four seconds");
            for (int i = 0; i < 540; i++) state.Step(1f / 60, false);
            Require(state.Wetness > 0.49f && state.Wetness < 0.51f, "wetness must fade gradually");
            for (int i = 0; i < 600; i++) state.Step(1f / 60, false);
            Require(state.Wetness == 0 && state.RainCredit == 0, "dry weather must clear wetness and rain backlog");
            state.Settings.DropBudget = 999;
            state.Step(1, true);
            Require(state.BodyDrops == 32 && state.RainCredit <= 10.01f, "budget and stalled delta must be capped");
            state.Settings.DropBudget = -1;
            state.Step(0.1f, true);
            Require(state.BodyDrops == 0, "zero budget must suppress body drops");
            var puddle = new WeatherSurface(null);
            for (int i = 0; i < 1800; i++) puddle.Step(1f / 60, true, true);
            Require(puddle.Fill > 0.99f, "puddles must fill over thirty seconds");
            for (int i = 0; i < 2701; i++) puddle.Step(1f / 60, false, true);
            Require(puddle.Fill < 0.001f && puddle.RingAge == 1, "puddles must drain and rings expire");
            puddle.Fill = 1;
            puddle.Step(0.1f, true, false);
            Require(puddle.Fill == 0, "disabling puddles must clear them immediately");
            Require(!puddle.CheckMouseSelection(), "weather must never intercept picking");
            Require(WeatherLab.TryTiles("0x71,113,114", out var ids) && ids.Length == 2, "tile IDs must parse and deduplicate");
            Require(!WeatherLab.TryTiles("0x4000", out _) && !WeatherLab.TryTiles("bad", out _), "invalid IDs must be rejected");
            Require(WeatherLab.TryTiles("", out ids) && ids.Length == 0, "an empty list must allow no puddles");
            GD.Print("[weather] PASS: default off, independent toggles, wetting/drying, delta clamp, drop caps, puddle fill/drain, ring expiry, no picking, tile validation");
            GetTree().Quit();
        }
        catch (Exception ex) { GD.PrintErr("[weather] FAIL: " + ex); GetTree().Quit(1); }
    }
    private static void Require(bool value, string message)
    {
        if (!value) throw new InvalidOperationException(message);
    }
}
#endif
