// SPDX-License-Identifier: BSD-2-Clause
// GUO-owned options category contract (task 1136). One authoritative store per
// setting; detail panels are the only editors of the settings they own.
using System;
using System.Collections.Generic;
using GUO.Renderer.SpeedMode;
using GUO.Renderer.Water;

namespace GUO.Configuration;

/// <summary>The focused GUO detail panels. All is the legacy combined UO+ panel.</summary>
internal enum GuoSection { All = 0, Lighting = 1, Weather = 2, World = 3 }

internal static class GuoOptions
{
    /// <summary>Raised on the game-loop thread after a GUO detail panel committed to the profile.</summary>
    internal static event Action<GuoSection> Applied;
    internal static void RaiseApplied(GuoSection section) => Applied?.Invoke(section);

    /// <summary>One-line effective-state summary for the GUO page buttons.</summary>
    internal static string Summary(Profile profile, GuoSection section)
    {
        if (profile == null) return "";
        switch (section)
        {
            case GuoSection.Lighting:
                if (profile.LightingSpeedMode)
                {
                    if (!SpeedModeDebug.Supported)
                        return "Enhanced lighting unavailable: " + (SpeedModeDebug.UnavailableReason ?? "not supported here");
                    string text = "Enhanced lighting on";
                    if (profile.SpeedModeSunShadows) text += " · sun shadows";
                    if (profile.SpeedModeLampShadows > 0) text += $" · {profile.SpeedModeLampShadows} lamp shadows";
                    return text;
                }
                return profile.RealtimeLights ? "Classic realtime lights" : "Classic lighting";
            case GuoSection.Weather:
            {
                bool enhanced = profile.LightingSpeedMode && SpeedModeDebug.Supported;
                var parts = new List<string>();
                if (enhanced && profile.SpeedModeWeather) parts.Add("Rain and wet ground on");
                if (enhanced && profile.SpeedModeFog) parts.Add("fog");
                int ambient = 0;
                foreach (var f in UoPlusOptions.Features)
                    if ((f.Id & WeatherFeatures) != 0 && UoPlusOptions.Enabled(profile, f.Id)) ambient++;
                if (ambient > 0) parts.Add(ambient == 1 ? "1 ambient effect" : $"{ambient} ambient effects");
                return parts.Count == 0 ? "Weather effects off" : string.Join(" · ", parts);
            }
            case GuoSection.World:
            {
                var choices = WaterRender.PlayerChoiceLists(profile);
                int index = WaterRender.PlayerChoiceIndex(profile);
                string water = index >= 0 && index < choices.Names.Length ? choices.Names[index] : "Classic";
                return $"Water: {water} · world map {(profile.ExperimentalWorldOverview ? "on" : "off")}";
            }
            default:
                return "";
        }
    }

    internal const UoPlusFeature WeatherFeatures = UoPlusFeature.Fog | UoPlusFeature.Leaves | UoPlusFeature.Dust
        | UoPlusFeature.RainRings | UoPlusFeature.Puddles | UoPlusFeature.WetPlayer | UoPlusFeature.Sway;
    internal const UoPlusFeature LightingFeatures = UoPlusFeature.SpellLights;
    internal const UoPlusFeature WorldFeatures = UoPlusFeature.WaterRipples;

    internal static UoPlusFeature FeaturesOf(GuoSection section) => section switch
    {
        GuoSection.Lighting => LightingFeatures,
        GuoSection.Weather => WeatherFeatures,
        GuoSection.World => WorldFeatures,
        _ => LightingFeatures | WeatherFeatures | WorldFeatures,
    };

    /// <summary>
    /// Puts only one section's settings back to a new profile's values. A
    /// Weather reset never touches lighting and vice versa; All resets all.
    /// </summary>
    internal static void ResetSection(Profile p, GuoSection section)
    {
        if (p == null) return;
        bool all = section == GuoSection.All;
        if (all || section == GuoSection.Lighting)
        {
            p.LightingSpeedMode = false;
            p.RealtimeLights = false;
            p.SpeedModeNightFloor = p.SpeedModeSoften = 3;
            p.SpeedModeLampShadows = 6;
            p.SpeedModeSunShadows = p.SpeedModeWindowLight = true;
        }
        if (all || section == GuoSection.Weather)
        {
            p.SpeedModeWeather = p.SpeedModeReflections = p.SpeedModeFog = true;
            p.SpeedModeMorningHaze = 0;
            p.WindStrength = 0;
            p.AmbientParticleDensity = 50;
            p.AmbientParticleCap = 128;
        }
        if (all || section == GuoSection.World)
        {
            p.ExperimentalWorldOverview = false;
            p.WorldOverviewStyle = 0;
            p.TouchUi = null;
            p.WaterStyle = WaterRender.Classic;
            p.ShardWaterStyle = "";
        }
        var mask = FeaturesOf(section);
        foreach (var f in UoPlusOptions.Features)
        {
            if ((f.Id & mask) == 0) continue;
            switch (f.Id)
            {
                case UoPlusFeature.Fog: p.AmbientFog = false; break;
                case UoPlusFeature.Leaves: p.AmbientLeaves = false; break;
                case UoPlusFeature.Dust: p.AmbientDust = false; break;
                case UoPlusFeature.SpellLights: p.SpellLighting = false; break;
            }
        }
        p.UoPlusFlags &= ~mask;
        if (p.UoPlusDensities != null) foreach (var f in UoPlusOptions.Features) if ((f.Id & mask) != 0) p.UoPlusDensities.Remove(f.Id);
    }
}
