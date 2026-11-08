// SPDX-License-Identifier: BSD-2-Clause

using System;
using System.Collections.Generic;
using Godot;
using GUO.Configuration;

namespace GUO.Host;

/// <summary>
/// <c>--perf-profile NAME</c>: the profile the perf probe measures with,
/// set on the loaded profile once the probe is in the world, so one scratch
/// home can be timed as ClassicUO would draw it and again with every UO+
/// feature on, without hand-editing a profile.json between runs.
/// </summary>
/// <remarks>
/// <list type="bullet">
/// <item><c>plain</c>: what ClassicUO has and nothing it does not. Realtime
/// lights, spell lights, ambient particles, water ripples and every UO+ flag
/// off; Classic water; HD filter Auto. The caller pairs it with
/// <c>--merged-land=off --merged-cover=off --postfx off</c>.</item>
/// <item><c>lean</c>: plain with optional HD, sprite/static/terrain shadows
/// and animated water explicitly disabled, for an effects-off FPS cohort.</item>
/// <item><c>defaults</c>: the profile untouched (a new scratch profile is
/// GUO's shipped defaults).</item>
/// <item><c>uoplus-max</c>: realtime lights with spell lights, every
/// available UO+ feature at density 100, the particle cap at its maximum,
/// the realistic water style with ripples, HD filter Smooth, shadows on.
/// The caller pairs it with a post-processing look.</item>
/// </list>
/// Settings that the renderer reads from the profile each frame (lights,
/// particles, HD filter) take effect at once; water is re-baked through
/// <see cref="GUO.Renderer.Water.WaterRender.Changed"/>.
/// </remarks>
internal static class PerfProfilePreset
{
    public static readonly string[] Names = { "plain", "lean", "defaults", "uoplus-max", "lighting-speed" };

    /// <summary>Applies the named preset to the current profile; returns what was set, for the report.</summary>
    public static string Apply(string name, GUO.Game.World world)
    {
        Profile p = ProfileManager.CurrentProfile;
        if (string.IsNullOrWhiteSpace(name) || p == null)
        {
            return "";
        }

        switch (name)
        {
            case "lean":
            case "plain":
            case "lighting-speed": // PORT DEVIATION (GUO): ADR-0048 matched Debug off/on fixture.
                UoPlusOptions.DisableAll(p);
                p.LightingSpeedMode=name=="lighting-speed";
                p.SpeedModeNightFloor=3; p.SpeedModeSoften=3;
                p.RealtimeLights = false;
                p.AmbientParticleDensity = 0;
                p.WaterStyle = GUO.Renderer.Water.WaterRender.Classic;
                p.HdFilter = HdFilterMode.Auto;
                if (name == "lean")
                {
                    p.EnhancedHD = false;
                    p.ShadowsEnabled = false;
                    p.ShadowsStatics = false;
                    p.TerrainShadowsLevel = 0;
                    p.AnimatedWaterEffect = false;
                }
                break;
            case "defaults":
                break;
            case "uoplus-max":
                p.RealtimeLights = true;
                p.ShadowsEnabled = true;
                p.ShadowsStatics = true;
                p.AmbientParticleDensity = 100;
                p.AmbientParticleCap = 512;
                p.HdFilter = HdFilterMode.Smooth;
                p.WaterStyle = "realistic";
                foreach (UoPlusOptions.Feature f in UoPlusOptions.Features)
                {
                    if (f.Available)
                    {
                        UoPlusOptions.Set(p, f.Id, true, 100);
                    }
                }
                break;
            default:
                GD.PrintErr($"[GUO] perf probe: no profile preset named {name}; the presets are {string.Join(", ", Names)}");
                return "";
        }

        GUO.Renderer.Water.WaterRender.Changed(world);
        string summary = Describe(p);
        GD.Print($"[GUO] perf probe: profile preset {name}: {summary}");
        return summary;
    }

    /// <summary>The render-relevant profile state in one line, for the report header.</summary>
    public static string Describe(Profile p)
    {
        if (p == null)
        {
            return "";
        }

        var on = new List<string>();
        foreach (UoPlusOptions.Feature f in UoPlusOptions.Features)
        {
            if (UoPlusOptions.Enabled(p, f.Id))
            {
                on.Add($"{f.Name.ToLowerInvariant()} {UoPlusOptions.Density(p, f.Id)}%");
            }
        }

        return $"lights {(p.LightingSpeedMode ? "speed" : p.RealtimeLights ? "realtime" : "classic")}, water {p.WaterStyle}, hd {p.HdFilter}, enhanced HD {p.EnhancedHD}, "
               + $"shadows {(p.ShadowsEnabled ? "on" : "off")}, static shadows {p.ShadowsStatics}, terrain shadows {p.TerrainShadowsLevel}, "
               + $"animated water {p.AnimatedWaterEffect}, ambient density {p.AmbientParticleDensity}, particle cap {p.AmbientParticleCap}, "
               + $"uo+ [{(on.Count == 0 ? "none" : string.Join("; ", on))}]";
    }
}
