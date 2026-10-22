// SPDX-License-Identifier: BSD-2-Clause
#if GUO_LABS
using System;
using GUO.Renderer.Weather;
using System.IO;
using Godot;
using GUO.Game;
using GUO.Configuration;
using GUO.Game.GameObjects;

namespace GUO.Labs.Weather;

// Explicit local test command. Changes only the scratch client's weather and moves
// its test character through the normal GM command path. Never starts automatically.
public partial class WeatherLiveProof : Node
{
    private World _world;
    private string _output;
    private static bool _running;

    internal static void Start(World world)
    {
        string output = System.Environment.GetEnvironmentVariable("GUO_WEATHER_PROOF_DIR");
        if (_running || !world.InGame || string.IsNullOrWhiteSpace(output))
        {
            GameActions.Print(world, "Weather proof needs an in-world scratch client and GUO_WEATHER_PROOF_DIR; see Labs/Weather/README.md.");
            return;
        }
        _running = true;
        var proof = new WeatherLiveProof { _world = world, _output = output };
        ((SceneTree)Engine.GetMainLoop()).Root.AddChild(proof);
    }

    public override async void _Ready()
    {
        var profile = ProfileManager.CurrentProfile;
        var weatherFlags = UoPlusFeature.RainRings | UoPlusFeature.Puddles | UoPlusFeature.WetPlayer;
        var originalFlags = profile?.UoPlusFlags ?? UoPlusFeature.None;
        var weatherIds = new[] { UoPlusFeature.RainRings, UoPlusFeature.Puddles, UoPlusFeature.WetPlayer };
        var originalDensities = new System.Collections.Generic.Dictionary<UoPlusFeature, int>();
        foreach (var id in weatherIds)
            if (profile?.UoPlusDensities != null && profile.UoPlusDensities.TryGetValue(id, out int density))
                originalDensities[id] = density;
        try
        {
            Directory.CreateDirectory(_output);
            var state = WeatherLab.For(_world);
            // Exposed shore land beside the docks: the dock itself covers wet
            // land, which cannot produce the dry-ground puddles asserted below.
            await GUO.Host.InputProbe.Say(this, "[go 1480 1740 0");
            await ToSignal(GetTree().CreateTimer(4), SceneTreeTimer.SignalName.Timeout);
            if (!_world.InGame) throw new InvalidOperationException("client left the world");
            _world.Weather.Reset();
            state.RainOverride = 0;
            state.Settings.WaterDrops = state.Settings.Puddles = state.Settings.WetPlayer = false;
            foreach (var id in weatherIds) UoPlusOptions.Set(profile, id, false, 100);
            await Shot("weather-live-off");
            // The test explicitly selects the tile under the player, without shipping its ID.
            for (GameObject o = _world.Map.GetTile(_world.Player.X, _world.Player.Y, false); o != null; o = o.TNext)
                if (o is Land land) state.Settings.PuddleTiles.Add(land.Graphic);
            state.Settings.WaterDrops = state.Settings.Puddles = state.Settings.WetPlayer = true;
            foreach (var id in weatherIds) UoPlusOptions.Set(profile, id, true, 100);
            _world.Weather.Generate(WeatherType.WT_RAIN, 70, 15);
            state.RainOverride = 1;
            await ToSignal(GetTree().CreateTimer(32), SceneTreeTimer.SignalName.Timeout);
            await Shot("weather-live-rain");
            int filled = 0, rings = 0;
            foreach (var s in state.Surfaces)
                if (s != null) { if (s.Fill > 0.9f) filled++; if (s.RingAge < 1) rings++; }
            GD.Print($"[weather live] rain: wetness={state.Wetness:F3}, full-puddles={filled}, active-rings={rings}");
            if (state.Wetness < 0.9f || filled == 0 || rings == 0) throw new InvalidOperationException("rain integration did not populate every effect");
            _world.Weather.Reset();
            state.RainOverride = 0;
            await ToSignal(GetTree().CreateTimer(20), SceneTreeTimer.SignalName.Timeout);
            await Shot("weather-live-drying");
            if (state.Wetness != 0) throw new InvalidOperationException("wet player did not dry");
            await ToSignal(GetTree().CreateTimer(27), SceneTreeTimer.SignalName.Timeout);
            await Shot("weather-live-dry");
            foreach (var s in state.Surfaces)
                if (s != null && (s.Fill > 0.001f || s.RingAge < 1)) throw new InvalidOperationException("surface effects did not drain");
            GD.Print("[weather live] PASS: rain integration, puddles, rings, wetness, fade and drainage");
            GetTree().Quit();
        }
        catch (Exception ex) { GD.PrintErr("[weather live] FAIL: " + ex); GetTree().Quit(1); }
        finally
        {
            WeatherLab.For(_world).RainOverride = null;
            // The explicit proof temporarily overrides the shared gates, never saved preferences.
            if (profile != null)
            {
                profile.UoPlusFlags = (profile.UoPlusFlags & ~weatherFlags) | (originalFlags & weatherFlags);
                foreach (var id in weatherIds)
                    if (originalDensities.TryGetValue(id, out int density)) profile.UoPlusDensities[id] = density;
                    else profile.UoPlusDensities?.Remove(id);
            }
            _running = false;
        }
    }

    private async System.Threading.Tasks.Task Shot(string name)
    {
        await ToSignal(RenderingServer.Singleton, RenderingServer.SignalName.FramePostDraw);
        using var image = GetViewport().GetTexture().GetImage();
        if (image.SavePng(Path.Combine(_output, name + ".png")) != Error.Ok)
            throw new IOException("could not save " + name);
    }
}
#endif
