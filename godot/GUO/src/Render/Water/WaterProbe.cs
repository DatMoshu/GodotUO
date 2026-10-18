// SPDX-License-Identifier: BSD-2-Clause
// PORT DEVIATION (GUO): water render modes (ADR-0037). Headless contract probe.
using System;
using System.Text.Json;
using Godot;
using GUO.Configuration;

namespace GUO.Renderer.Water;

/// <summary>
/// The water option's contract without a shard: Classic by default and for
/// old profiles, the choice persists, unknown styles fall back to Classic,
/// a style replaces upstream's Animated water effect, and the water bit
/// survives the batcher's packing beside circle-of-transparency. Pixels are
/// checked by the client.render.water scenario, not here.
/// </summary>
public partial class WaterProbe : Node
{
    public override void _Ready() => GetTree().Quit(Run() ? 0 : 1);

    internal static bool Run()
    {
        string savedOverride = WaterRender.Override;
        try
        {
            WaterRender.Override = null;
            var profile = JsonSerializer.Deserialize("{}", ProfileJsonContext.DefaultToUse.Profile);
            Require(profile.WaterStyle == WaterRender.Classic && !WaterRender.IsStyled(profile), "existing profiles must be Classic");

            profile.WaterStyle = "uo";
            var restored = JsonSerializer.Deserialize(JsonSerializer.Serialize(profile, ProfileJsonContext.DefaultToUse.Profile), ProfileJsonContext.DefaultToUse.Profile);
            Require(restored.WaterStyle == "uo" && WaterRender.Resolve(restored) is UoRealtimeWaterStyle, "the water style must persist");

            restored.WaterStyle = "no-such-style";
            Require(WaterRender.Resolve(restored) == null, "an unknown style must draw Classic");
            restored.WaterStyle = "";
            Require(WaterRender.Resolve(restored) == null, "an empty style must draw Classic");

            restored.WaterStyle = WaterRender.Classic;
            WaterRender.Override = "uo";
            Require(WaterRender.IsStyled(restored), "--water-style must win over the profile");
            WaterRender.Override = null;

            restored.AnimatedWaterEffect = true;
            Require(WaterRender.UpstreamWobble(restored), "Classic keeps upstream's Animated water effect");
            restored.WaterStyle = "uo";
            Require(!WaterRender.UpstreamWobble(restored), "a water style replaces upstream's Animated water effect");

            (string[] ids, string[] names) = WaterRender.ChoiceLists();
            Require(ids.Length == names.Length && ids.Length >= 2 && ids[0] == WaterRender.Classic, "Classic must be the first choice");
            for (int i = 0; i < ids.Length; i++)
            {
                Require(WaterRender.ChoiceIndex(ids[i]) == i && WaterRender.ChoiceId(i) == ids[i], "choice index round trip");
            }
            Require(WaterRender.ChoiceIndex("no-such-style") == 0 && WaterRender.ChoiceId(99) == WaterRender.Classic, "out-of-range choices are Classic");

            // The mode byte: water bit 0x40 beside the mode and the
            // circle-of-transparency bit (alpha > 1 in GetHueVector).
            Vector3 hue = new Vector3(0, ShaderHueTranslator.SHADER_HUED + WaterRender.ModeBit, 1.5f);
            int mode = (int)Math.Round(UltimaBatcher2D.Encode(hue).B * 255f);
            Require(mode == (0x80 | WaterRender.ModeBit | ShaderHueTranslator.SHADER_HUED), $"packed mode byte 0x{mode:X2}");
            int plain = (int)Math.Round(UltimaBatcher2D.Encode(new Vector3(0, ShaderHueTranslator.SHADER_HUED, 1f)).B * 255f);
            Require((plain & WaterRender.ModeBit) == 0, "a tile without water must not carry the bit");

            GD.Print("[water] PASS: Classic default and persistence, unknown styles Classic, --water-style override, upstream effect replaced, choices, mode byte");
            return true;
        }
        catch (Exception ex)
        {
            GD.PrintErr("[water] FAIL: " + ex.Message);
            return false;
        }
        finally
        {
            WaterRender.Override = savedOverride;
        }
    }

    private static void Require(bool ok, string what)
    {
        if (!ok)
        {
            throw new InvalidOperationException(what);
        }
    }
}
