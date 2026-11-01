// SPDX-License-Identifier: BSD-2-Clause
using System;
using System.Text.Json;
using Godot;
using GUO.Renderer.DepthWorld;
using GUO.Renderer.SpeedMode;

namespace GUO.Configuration;

/// <summary>Headless truth tables for GuoEffectPolicy (task 1136). No client data, no GPU.</summary>
public partial class GuoEffectPolicyProbe : Node
{
    public override void _Ready() => GetTree().Quit(Run() ? 0 : 1);

    private static int _failures;

    internal static bool Run()
    {
        bool savedProbe = SpeedModeDebug.Probe;
        string savedReason = SpeedModeDebug.RuntimeUnavailableReason;
        try
        {
            var p = JsonSerializer.Deserialize("{}", ProfileJsonContext.DefaultToUse.Profile);
            Check(SpeedModeDebug.Supported, "speed mode is supported headless (dummy renderer)");

            // SpellLightsEligible
            UoPlusOptions.Set(p, UoPlusFeature.SpellLights, true, 50);
            foreach (bool real in new[] { false, true })
                foreach (bool speed in new[] { false, true })
                {
                    p.RealtimeLights = real; SpeedModeDebug.Probe = speed;
                    Check(GuoEffectPolicy.SpellLightsEligible(p) == (real || speed),
                        $"spell lights realtime={real} speed={speed}");
                }
            SpeedModeDebug.Probe = true; p.RealtimeLights = false;
            SpeedModeDebug.RuntimeUnavailableReason = "probe";
            Check(!GuoEffectPolicy.SpellLightsEligible(p), "speed only but unsupported: not eligible");
            p.RealtimeLights = true;
            Check(GuoEffectPolicy.SpellLightsEligible(p), "realtime still eligible when speed unsupported");
            SpeedModeDebug.RuntimeUnavailableReason = null;
            UoPlusOptions.Set(p, UoPlusFeature.SpellLights, true, 0);
            Check(!GuoEffectPolicy.SpellLightsEligible(p), "density 0: not eligible");
            UoPlusOptions.Set(p, UoPlusFeature.SpellLights, false, 50);
            Check(!GuoEffectPolicy.SpellLightsEligible(p), "feature off: not eligible");
            Check(!GuoEffectPolicy.SpellLightsEligible(null), "null profile: not eligible");
            Check(p.RealtimeLights, "RealtimeLights preference untouched");

            // ClassicShadowsSuppressed
            p.ShadowsEnabled = true; p.ShadowsStatics = true;
            foreach (bool requested in new[] { false, true })
                foreach (bool unsupported in new[] { false, true })
                    foreach (int child in new[] { 0, 1, 2 }) // none, sun, lamps
                        foreach (bool lit in new[] { false, true })
                        {
                            SpeedModeDebug.Probe = requested;
                            SpeedModeDebug.RuntimeUnavailableReason = unsupported ? "probe" : null;
                            p.SpeedModeSunShadows = child == 1; p.SpeedModeLampShadows = child == 2 ? 3 : 0;
                            OrderedWorldCapture.SetEnhancedLit(lit);
                            GuoEffectPolicy.EnhancedShadowsProduced = true;
                            bool expect = requested && !unsupported && child != 0 && lit;
                            Check(GuoEffectPolicy.ClassicShadowsSuppressed(p) == expect,
                                $"suppressed requested={requested} unsupported={unsupported} child={child} lit={lit}");
                        }
            SpeedModeDebug.RuntimeUnavailableReason = null;
            // Lit but no shadows produced (night, no lamp in reach): classic shadows stay.
            SpeedModeDebug.Probe = true; p.SpeedModeSunShadows = true; p.SpeedModeLampShadows = 6;
            OrderedWorldCapture.SetEnhancedLit(true);
            GuoEffectPolicy.EnhancedShadowsProduced = false;
            Check(!GuoEffectPolicy.ClassicShadowsSuppressed(p), "lit but no shadows produced: not suppressed");
            Check(GuoEffectPolicy.ClassicShadowsStatus(p) == "Classic shadows: on (enhanced shadows not active)", "status when none produced");
            GuoEffectPolicy.EnhancedShadowsProduced = true;
            Check(GuoEffectPolicy.ClassicShadowsSuppressed(p), "lit and shadows produced: suppressed");
            Check(!GuoEffectPolicy.ClassicShadowsSuppressed(null), "null profile: not suppressed");

            // Stored preferences survive evaluation.
            SpeedModeDebug.Probe = true; p.SpeedModeSunShadows = true; OrderedWorldCapture.SetEnhancedLit(true); GuoEffectPolicy.EnhancedShadowsProduced = true;
            bool sup = GuoEffectPolicy.ClassicShadowsSuppressed(p);
            _ = GuoEffectPolicy.ClassicShadowsStatus(p);
            Check(sup && p.ShadowsEnabled && p.ShadowsStatics && p.SpeedModeSunShadows,
                "stored shadow preferences unchanged while suppressed");

            // Status strings
            p.ShadowsEnabled = false;
            Check(GuoEffectPolicy.ClassicShadowsStatus(p) == "Classic shadows: off (your setting)", "status off");
            p.ShadowsEnabled = true;
            Check(GuoEffectPolicy.ClassicShadowsStatus(p).Contains("replaced by enhanced shadows") &&
                  GuoEffectPolicy.ClassicShadowsStatus(p).Contains("your setting is kept"), "status replaced");
            OrderedWorldCapture.SetEnhancedLit(false);
            Check(GuoEffectPolicy.ClassicShadowsStatus(p) == "Classic shadows: on (enhanced shadows not active)", "status not lit");
            SpeedModeDebug.Probe = false;
            Check(GuoEffectPolicy.ClassicShadowsStatus(p) == "Classic shadows: on", "status plain on");
        }
        catch (Exception e) { GD.PrintErr("[guo-effect-policy] " + e); _failures++; }
        finally
        {
            SpeedModeDebug.Probe = savedProbe;
            SpeedModeDebug.RuntimeUnavailableReason = savedReason;
            OrderedWorldCapture.SetEnhancedLit(false);
            GuoEffectPolicy.EnhancedShadowsProduced = false;
        }
        GD.Print(_failures == 0 ? "[guo-effect-policy] PASS" : "[guo-effect-policy] FAIL");
        return _failures == 0;
    }

    private static void Check(bool ok, string what)
    {
        if (ok) return;
        _failures++;
        GD.PrintErr("[guo-effect-policy] FAIL: " + what);
    }
}
