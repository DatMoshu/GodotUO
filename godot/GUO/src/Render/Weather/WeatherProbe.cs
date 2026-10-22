// SPDX-License-Identifier: BSD-2-Clause
using System;
using System.IO;
using Godot;
using GUO.Game;

namespace GUO.Renderer.Weather;
public partial class WeatherProbe : Node
{
    public override void _Ready() => GetTree().Quit(Run() ? 0 : 1);

    internal static bool Run()
    {
        // World constructs ContainerManager, which creates a default config.
        // Preserve existing user files and remove only this probe's fixture.
        string dataDir = Path.Combine(CUOEnviroment.ExecutablePath, "Data");
        string clientDir = Path.Combine(dataDir, "Client");
        string containerFile = Path.Combine(clientDir, "containers.txt");
        bool hadDataDir = Directory.Exists(dataDir), hadClientDir = Directory.Exists(clientDir);
        bool hadContainerFile = File.Exists(containerFile);
        try
        {
            var state = new WeatherState();
            Require(!state.Settings.Enabled, "all three effects must default off");
            state.Settings.WaterDrops = true;
            Require(!state.Settings.Puddles && !state.Settings.WetPlayer, "toggles must be independent");
            Require(WeatherLab.RainIntensity(null) == 0, "unavailable weather must be dry");
            Require(WeatherLab.RainIntensity(WeatherType.WT_RAIN, 35) == 0.5f &&
                WeatherLab.RainIntensity(WeatherType.WT_STORM_BREWING, 70) == 1 &&
                WeatherLab.RainIntensity(WeatherType.WT_STORM_APPROACH, 70) == 1,
                "rain and brewing intensity must use upstream's seventy-drop range");
            Require(WeatherLab.RainIntensity(WeatherType.WT_RAIN, 255) == 1 &&
                WeatherLab.RainIntensity(WeatherType.WT_RAIN, -1) == 0,
                "intensity must be clamped");
            foreach (var type in new WeatherType?[] { null, WeatherType.WT_SNOW,
                WeatherType.WT_INVALID_0, WeatherType.WT_INVALID_1 })
                Require(WeatherLab.RainIntensity(type, 70) == 0, "non-rain events must not stamp water");
            var world = new World();
            WeatherLab.Observe(world.Weather, WeatherType.WT_RAIN, 70, Time.Ticks);
            Require(WeatherLab.RainIntensity(world) == 1, "packet rain must reach effects");
            WeatherLab.Observe(world.Weather, WeatherType.WT_RAIN, 14, Time.Ticks);
            Require(WeatherLab.RainIntensity(world) == 0.2f, "same-type packet must update intensity");
            WeatherLab.Observe(world.Weather, WeatherType.WT_RAIN, 0, Time.Ticks);
            Require(WeatherLab.RainIntensity(world) == 0, "same-type zero packet must stop new rain");
            WeatherLab.Observe(world.Weather, WeatherType.WT_SNOW, 70, Time.Ticks);
            Require(WeatherLab.RainIntensity(world) == 0, "snow must not wet surfaces");
            WeatherLab.Observe(world.Weather, WeatherType.WT_INVALID_0, 70, Time.Ticks);
            Require(WeatherLab.RainIntensity(world) == 0, "clear packet must stop rain");
            WeatherLab.Observe(world.Weather, WeatherType.WT_RAIN, 70,
                unchecked(Time.Ticks - (uint)Constants.WEATHER_TIMER - 1));
            Require(WeatherLab.RainIntensity(world) == 0, "expired packet must stop rain even without a classic draw");
            WeatherLab.Observe(world.Weather, WeatherType.WT_RAIN, 70, Time.Ticks);
            WeatherLab.For(world).Wetness = 1;
            WeatherLab.Clear(world);
            Require(WeatherLab.RainIntensity(world) == 0 && WeatherLab.For(world).Wetness == 0,
                "world/facet clear must remove both signal and visual state");
            var drizzle = new WeatherState();
            drizzle.Settings.WetPlayer = true;
            for (int i = 0; i < 240; i++) drizzle.Step(1f / 60, 0.5f);
            Require(Math.Abs(drizzle.Wetness - 0.5f) < 0.001f,
                "half-intensity rain must wet at half the rate");
            state.Settings.WetPlayer = true;
            for (int i = 0; i < 240; i++) state.Step(1f / 60, 1);
            Require(state.Wetness > 0.99f, "wetness must fill over four seconds");
            for (int i = 0; i < 540; i++) state.Step(1f / 60, 0);
            Require(state.Wetness > 0.49f && state.Wetness < 0.51f, "wetness must fade gradually");
            for (int i = 0; i < 600; i++) state.Step(1f / 60, 0);
            Require(state.Wetness == 0 && state.RainCredit == 0, "dry weather must clear wetness and rain backlog");
            state.Settings.DropBudget = 999;
            state.Step(1, 1);
            Require(state.BodyDrops == 32 && state.RainCredit <= 10.01f, "budget and stalled delta must be capped");
            state.Settings.DropBudget = -1;
            state.Step(0.1f, 1);
            Require(state.BodyDrops == 0, "zero budget must suppress body drops");
            var puddle = new WeatherSurface(null);
            for (int i = 0; i < 1800; i++) puddle.Step(1f / 60, 0.5f, true);
            Require(Math.Abs(puddle.Fill - 0.5f) < 0.001f, "intensity must scale puddle fill");
            puddle.Fill = 0;
            for (int i = 0; i < 1800; i++) puddle.Step(1f / 60, 1, true);
            Require(puddle.Fill > 0.99f, "puddles must fill over thirty seconds");
            for (int i = 0; i < 2701; i++) puddle.Step(1f / 60, 0, true);
            Require(puddle.Fill < 0.001f && puddle.RingAge == 1, "puddles must drain and rings expire");
            puddle.Fill = 1;
            puddle.Step(0.1f, 1, false);
            Require(puddle.Fill == 0, "disabling puddles must clear them immediately");
            Require(!puddle.CheckMouseSelection(), "weather must never intercept picking");
            var queue = new Game.Scenes.RenderLists();
            queue.Add(puddle);
            Require(queue.WorldCount == 1, "shipping weather surfaces must enter the sorted world queue");
            Require(WeatherLab.TryTiles("0x71,113,114", out var ids) && ids.Length == 2, "tile IDs must parse and deduplicate");
            Require(!WeatherLab.TryTiles("0x4000", out _) && !WeatherLab.TryTiles("bad", out _), "invalid IDs must be rejected");
            Require(WeatherLab.TryTiles("", out ids) && ids.Length == 0, "an empty list must allow no puddles");
            GD.Print("[weather] PASS: default off, independent toggles, packet intensity updates/zero/snow/clear/expiry, world cleanup, intensity-scaled wetting and filling, drying/drainage, delta clamp, drop caps, ring expiry, no picking, tile validation");
            return true;
        }
        catch (Exception ex) { GD.PrintErr("[weather] FAIL: " + ex); return false; }
        finally
        {
            if (!hadContainerFile && File.Exists(containerFile)) File.Delete(containerFile);
            if (!hadClientDir && Directory.Exists(clientDir) && Directory.GetFileSystemEntries(clientDir).Length == 0)
                Directory.Delete(clientDir);
            if (!hadDataDir && Directory.Exists(dataDir) && Directory.GetFileSystemEntries(dataDir).Length == 0)
                Directory.Delete(dataDir);
        }
    }
    private static void Require(bool value, string message)
    {
        if (!value) throw new InvalidOperationException(message);
    }
}
