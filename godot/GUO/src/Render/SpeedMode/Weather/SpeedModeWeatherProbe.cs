// SPDX-License-Identifier: BSD-2-Clause
using System;
using System.Collections.Generic;
using System.Text.Json;
using Godot;
using GUO.Configuration;
using GUO.Game;
using GUO.Renderer.Water;

namespace GUO.Renderer.SpeedMode.Weather;

/// <summary>Offline state/target contract. Rendered acceptance is a separate scenario step.</summary>
public partial class SpeedModeWeatherProbe : Node
{
    public override void _Ready()
    {
        try
        {
            var state = new SpeedModeWeather();
            Require(state.Wetness == 0 && state.Phase == WetnessPhase.Dry, "starts dry");
            state.Advance(WeatherType.WT_RAIN, 35, -4, 0.5f, 15);
            Near(state.RainIntensity, 0.5f, "count/70");
            Near(state.Wetness, 0.25f, "15 seconds wetting");
            Near(state.Wind, -1.5f, "wind retains direction");
            Require(state.Phase == WetnessPhase.Wetting, "wetting phase");
            state.Advance(WeatherType.WT_RAIN, 35, 0, 0, 15);
            Near(state.Wetness, 0.5f, "target reached without overshoot");
            Require(state.Phase == WetnessPhase.Holding, "holding phase");
            state.Advance(null, 70, 0, 0, 45);
            Near(state.Wetness, 0.25f, "45 seconds draining");
            Require(state.Phase == WetnessPhase.Drying, "drying phase");
            state.Advance(WeatherType.WT_INVALID_1, 255, 0, 0, 45);
            Near(state.Wetness, 0, "fully drained");
            Require(state.Phase == WetnessPhase.Dry, "dry phase");
            state.Advance(WeatherType.WT_STORM_BREWING, 255, 127, 99, 60);
            Near(state.RainIntensity, 1, "rain clamps");
            Near(state.Wetness, 1, "full rain wets in 60 seconds");
            Near(state.Wind, 2, "wind clamps");
            state.Advance(WeatherType.WT_SNOW, 70, 0, 0, 180);
            Near(state.Wetness, 0, "snow drains in 180 seconds");
            state.Advance(WeatherType.WT_STORM_APPROACH, 70, 0, float.NaN, 60);
            Near(state.Wetness, 0, "approaching storm is not rain");
            state.Advance(WeatherType.WT_RAIN, -1, 0, float.PositiveInfinity, 60);
            Near(state.RainIntensity, 0, "negative count clamps");
            foreach (float invalid in new[] { -1f, float.NaN, float.PositiveInfinity })
            {
                state.Advance(WeatherType.WT_RAIN, 70, 0, 0, invalid);
                Near(state.Wetness, 0, "invalid delta never integrates");
            }
            var fine = new SpeedModeWeather();
            for (int i = 0; i < 3600; i++) fine.Advance(WeatherType.WT_RAIN, 70, 0, 0, 1f / 60f);
            var coarse = new SpeedModeWeather();
            coarse.Advance(WeatherType.WT_RAIN, 70, 0, 0, 60);
            Near(fine.Wetness, coarse.Wetness, "frame cadence independence", 0.0001f);

            foreach (var sample in new[] { (Count: -1, Mist: 0f, Haze: 0f),
                (Count: 0, Mist: 0f, Haze: 0f), (Count: 35, Mist: 0.5f, Haze: 0.125f),
                (Count: 70, Mist: 1f, Haze: 0.25f), (Count: 255, Mist: 1f, Haze: 0.25f) })
            {
                state.Advance(WeatherType.WT_STORM_BREWING, sample.Count, 0, 0, 0);
                Near(state.FogDensity, sample.Mist, "storm-brewing mist follows clamped count");
                state.Advance(WeatherType.WT_RAIN, sample.Count, 0, 0, 0);
                Near(state.FogDensity, sample.Haze, "rain haze has quarter strength");
            }
            foreach (WeatherType? type in new WeatherType?[] { null, WeatherType.WT_SNOW,
                WeatherType.WT_STORM_APPROACH, WeatherType.WT_INVALID_0,
                WeatherType.WT_INVALID_1, (WeatherType)99 })
            {
                state.Advance(WeatherType.WT_STORM_BREWING, 70, 0, 0, 0);
                state.Advance(type, 255, 0, 0, 0);
                Near(state.FogDensity, 0, "other weather clears mist immediately");
                Near(SpeedModeFogPass.CombinedDensity(state.FogDensity, 20), 0.2f,
                    "morning haze remains constant without weather mist");
            }

            var profile = JsonSerializer.Deserialize("{}", ProfileJsonContext.DefaultToUse.Profile);
            Require(profile.WaterStyle == "classic" && profile.WindStrength == 0, "old profiles retain defaults");
            Require(!profile.LightingSpeedMode && profile.SpeedModeWeather && profile.SpeedModeReflections,
                "old profiles keep main mode off and weather/reflection sub-options on");
            profile.WindStrength = float.NaN;
            Near(profile.WindStrength, 0, "profile rejects invalid wind");
            profile.WindStrength = 0.7f;
            var restored = JsonSerializer.Deserialize(JsonSerializer.Serialize(profile, ProfileJsonContext.DefaultToUse.Profile), ProfileJsonContext.DefaultToUse.Profile);
            Near(restored.WindStrength, 0.7f, "wind persists");
            Require(profile.SpeedModeFog && profile.SpeedModeMorningHaze == 0,
                "fog sub-option defaults on with zero constant haze");
            profile.SpeedModeMorningHaze = 200;
            Require(profile.SpeedModeMorningHaze == 100, "morning haze clamps high");
            profile.SpeedModeMorningHaze = -1;
            Require(profile.SpeedModeMorningHaze == 0, "morning haze clamps low");
            profile.SpeedModeMorningHaze = 20;
            profile.SpeedModeFog = false;
            profile.SpeedModeWeather = false;
            profile.SpeedModeReflections = false;
            restored = JsonSerializer.Deserialize(JsonSerializer.Serialize(profile, ProfileJsonContext.DefaultToUse.Profile), ProfileJsonContext.DefaultToUse.Profile);
            Require(!restored.SpeedModeFog && restored.SpeedModeMorningHaze == 20, "fog preferences persist");
            Require(!restored.SpeedModeWeather && !restored.SpeedModeReflections, "weather/reflection preferences persist");
            Near(SpeedModeFogPass.CombinedDensity(0.25f, 20), 0.45f, "morning haze adds a constant term");
            Near(SpeedModeFogPass.CombinedDensity(1, 20), 1, "combined haze clamps");
            Near(SpeedModeFogPass.CombinedDensity(float.NaN, 0), 0, "invalid weather density is dry");
            var fogFallback = new SpeedModeFogPass();
            fogFallback.Bind(null, new Vector2I(2, 2), Vector2I.Zero, Vector3.Zero, 1, 100, 0, 0, true);
            Require(!fogFallback.Visible && fogFallback.Material == null, "fog fallback creates no shader");
            fogFallback.Free();
            Require(SpeedModePuddleSurfaces.IsCandidate(true, 0x03E9, ""), "land cobble id");
            Require(!SpeedModePuddleSurfaces.IsCandidate(false, 0x03E9, ""), "land/static id spaces are separate");
            Require(SpeedModePuddleSurfaces.IsCandidate(false, 0x0515, ""), "static cobble id");
            Require(SpeedModePuddleSurfaces.IsCandidate(false, 0, " Stone Pavers "), "normalized paving name");
            Require(!SpeedModePuddleSurfaces.IsCandidate(true, 0, "grass"), "grass is not a puddle candidate");
            var uniforms = new Dictionary<string, Variant>();
            new LightingWaterStyle().Bind((name, value) => uniforms[name] = value);
            Require(uniforms["water_environment"].AsBool(), "lighting style opts in");
            Require(uniforms["water_model"].AsInt32() == 4, "lighting model");
            new UoRealtimeWaterStyle().Bind((name, value) => uniforms[name] = value);
            Require(!uniforms["water_environment"].AsBool(), "old style disables environment");
            using var field = new Image();
            field.SetData(2, 2, false, Image.Format.Rgbah, new byte[2 * 2 * 8]);
            Require(field.GetFormat() == Image.Format.Rgbah, "RGBA16F fixture supported");
            var surfaceOrigin = new Vector2I(6000, 4000);
            SpeedModeRainProbe.WriteFixturePixel(field, 0, 0, 6000.25f, 4000.5f, 17, surfaceOrigin, 7);
            var relative = field.GetPixel(0, 0);
            Near(relative.R, 0.25f, "surface stores relative X");
            Near(relative.G, 0.5f, "surface stores relative Y");
            Near(relative.R + surfaceOrigin.X, 6000.25f, "origin restores distant world X");
            Near(relative.G + surfaceOrigin.Y, 4000.5f, "origin restores distant world Y");
            Near(relative.B, 7, "origin does not change Z");
            Near(relative.A, 17, "origin does not change class/flags");
            var rain = new RainRippleEmitter();
            var ripples = new WaterRipples();
            ripples.CentreOnForProbe(0, 0);
            rain.Update(ripples, 0.25f, 1, true, 8, 0, 0, (_, _) => false);
            Require(rain.LastAttempts == 8 && rain.LastStamps == 0, "covered water rejects every impact");
            rain.Update(ripples, 0.25f, 1, true, 8, 0, 0, (_, _) => true);
            Require(rain.LastStamps == 8, "exposed water receives bounded impacts");
            using var sheet = SpeedModeRainPass.CreateSheet();
            using var sheetImage = sheet.GetImage();
            Require(sheetImage.GetWidth() == 96 && sheetImage.GetHeight() == 32, "one procedural drop sheet");
            for (int region = 0; region < 3; region++)
            {
                int pixels = 0;
                for (int y = 0; y < 32; y++)
                for (int x = 0; x < 32; x++)
                    if (sheetImage.GetPixel(region * 32 + x, y).R > 0) pixels++;
                Require(pixels > 0 && pixels < 1024, "each rain component has a bounded shape");
            }
            var fallback = new SpeedModeRainPass();
            fallback.Bind(null, new Vector2I(2, 2), 1, 0, 0);
            Require(!fallback.Active && !fallback.Visible && fallback.Material == null
                && !SpeedModeRainPass.ReplacesClassic(null), "fallback creates no shader and never suppresses classic rain");
            AddChild(fallback);
            using var identitySurface = ImageTexture.CreateFromImage(field);
            SpeedModeRainProbe.CheckOwnership(fallback, identitySurface, new Vector2I(2, 2));
            RemoveChild(fallback);
            fallback.Free();
            CheckTargets(identitySurface, new Vector2I(2, 2));
            CheckRuntimeCallbacks(identitySurface);
            GD.Print("[speed-weather] PASS: intensity, wetting/draining, phases, frame cadence, wind, persistence, storm-brewing/rain fog mapping, zero snow/storm-approach mist, fog preferences/density/fallback, style isolation, half-float fixture, bounded rain stamps, sheet/fallback, world/tick ownership and Current-ticket binding/options/fallback; rendered shader proof separate");
            GetTree().Quit();
        }
        catch (Exception e)
        {
            GD.PrintErr("[speed-weather] FAIL: " + e);
            GetTree().Quit(1);
        }
    }

    private static void Near(float actual, float expected, string label, float epsilon = 0.00001f)
        => Require(Math.Abs(actual - expected) <= epsilon, $"{label}: {actual} != {expected}");
    private static void Require(bool value, string message)
    {
        if (!value) throw new InvalidOperationException(message);
    }

    private void CheckTargets(Texture2D source, Vector2I size)
    {
        var viewport = new SubViewport { Size = size };
        AddChild(viewport);
        var rain = new SpeedModeRainPass();
        var wet = new SpeedModeWetPass();
        var fog = new SpeedModeFogPass();
        viewport.AddChild(rain); viewport.AddChild(wet); viewport.AddChild(fog);
        var world = (World)System.Runtime.CompilerServices.RuntimeHelpers.GetUninitializedObject(typeof(World));
        var profile = new Profile { LightingSpeedMode = true, SpeedModeMorningHaze = 20 };
        var state = new SpeedModeWeather();
        state.Advance(WeatherType.WT_RAIN, 70, 0, 0, 60);
        var owner = new object();
        var origin = new Vector2I(6000, 4000);
        uint tick = Time.Ticks;
        bool Bind(long sequence) => SpeedModeWeatherTargets.BindCurrent(world, profile, state,
            source, sequence, tick, source, size, new Vector3(6000, 4000, 0), 1, 0, rain, wet, fog);
        try
        {
            SpeedModeBindings.PublishSurface(owner, source, origin, size);
            Require(!Bind(SpeedModeBindings.Current.Sequence) && rain.Material == null,
                "surface-only ticket must preserve classic rain without allocating shaders");
            SpeedModeBindings.PublishLight(owner, source, new Vector4(0, 0, size.X, size.Y), Vector3.Zero);
            long sequence = SpeedModeBindings.Current.Sequence;
            Require(Bind(sequence) && SpeedModeRainPass.ReplacesClassic(world), "matching complete frame binds weather");
            Require(((ShaderMaterial)rain.Material).GetShaderParameter("surface_origin").AsVector2() == new Vector2(6000, 4000),
                "binding uses core's integer origin");
            Near(((ShaderMaterial)fog.Material).GetShaderParameter("fog_density").AsSingle(), 0.45f,
                "core binding combines quarter rain haze and explicit morning preference");
            Require(((ShaderMaterial)wet.Material).GetShaderParameter("reflections_enabled").AsBool(),
                "reflection option reaches wet/water overlay");
            profile.SpeedModeWeather = false; profile.SpeedModeReflections = false;
            Require(Bind(sequence) && !rain.Active && !wet.Visible && fog.Visible,
                "weather/reflection controls leave independent fog enabled");
            Require(!Bind(sequence + 1) && !fog.Visible, "mismatched ticket clears all passes");
            profile.SpeedModeWeather = true; profile.SpeedModeReflections = true;
            Require(Bind(sequence), "valid same-frame ticket can bind again");
            Time.Ticks++;
            Require(!Bind(sequence) && !SpeedModeRainPass.ReplacesClassic(world), "stale tick clears classic suppression");
            Time.Ticks = tick;
            profile.LightingSpeedMode = false;
            Require(!Bind(sequence) && !rain.Visible && !wet.Visible && !fog.Visible, "main option off clears every pass");
            SpeedModeBindings.Clear(owner);
            Require(!Bind(sequence), "cleared core frame never binds");
        }
        finally
        {
            Time.Ticks = tick;
            SpeedModeBindings.Clear(owner);
            RemoveChild(viewport); viewport.Free();
        }
    }

    private void CheckRuntimeCallbacks(Texture2D source)
    {
        var target = new RenderTarget2D(this, 2, 2);
        var batcher = new UltimaBatcher2D(target.CanvasItem);
        var runtime = new SpeedModeWeatherRuntime();
        runtime.Initialize(target);
        target.ViewportNode.AddChild(runtime);
        var owner = new object();
        var world = (World)System.Runtime.CompilerServices.RuntimeHelpers.GetUninitializedObject(typeof(World));
        var profile = new Profile { LightingSpeedMode = true };
        var state = new SpeedModeWeather();
        state.Advance(WeatherType.WT_RAIN, 70, 0, 0, 60);
        try
        {
            runtime.Arm(world, profile, state, batcher, default, source, source,
                Transform2D.Identity, Vector3.Zero, 1, 0);
            SpeedModeBindings.PublishSurface(owner, source, Vector2I.Zero, new Vector2I(2, 2));
            Require(!runtime.Bound, "surface publication alone never enables runtime weather");
            SpeedModeBindings.PublishLight(owner, source, new Vector4(0, 0, 2, 2), Vector3.Zero);
            Require(runtime.Bound && runtime.BoundSequence == SpeedModeBindings.Current.Sequence,
                "production publication callback binds the armed frame synchronously");
            SpeedModeBindings.Clear(owner);
            Require(!runtime.Bound && runtime.BoundSequence == 0, "core invalidation retires runtime outputs");
            SpeedModeBindings.PublishSurface(owner, source, Vector2I.Zero, new Vector2I(2, 2));
            SpeedModeBindings.PublishLight(owner, source, new Vector4(0, 0, 2, 2), Vector3.Zero);
            Require(!runtime.Bound, "invalidation requires a new world snapshot before reuse");
            runtime.Arm(world, profile, state, batcher, default, source, source,
                Transform2D.Identity, Vector3.Zero, 1, 0);
            Require(runtime.Bound, "already-published matching frame can bind when armed");
            runtime.Release();
            SpeedModeBindings.PublishLight(owner, source, new Vector4(0, 0, 2, 2), Vector3.Zero);
            Require(!runtime.Bound, "release unsubscribes the production callback");
        }
        finally
        {
            SpeedModeBindings.Clear(owner);
            runtime.Release(); target.ViewportNode.RemoveChild(runtime); runtime.Free();
            batcher.Dispose(); target.Dispose();
        }
    }
}
