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

            // Ripples: a stamp pushes the surface down, the wave spreads out
            // from it, the window keeps it when it follows the player, and it calms.
            var ripples = new WaterRipples();
            ripples.CentreOnForProbe(1000, 1000);
            ripples.Stamp(1000, 1000, 0.6f, 0.8f);
            Require(ripples.HeightAt(1000, 1000) < -0.2f, "a stamp must push the surface down");
            Require(Math.Abs(ripples.HeightAt(1003, 1000)) < 1e-6f, "a stamp must stay local");
            ripples.StepForProbe(30);
            Require(Math.Abs(ripples.HeightAt(1003, 1000)) > 1e-3f, "the wave must spread");
            ripples.CentreOnForProbe(1030, 1000);
            Require(Math.Abs(ripples.HeightAt(1003, 1000)) > 1e-3f, "following the player must keep the waves");
            ripples.StepForProbe(600);
            float left = 0f;
            for (int x = 990; x <= 1020; x++)
            {
                left = Math.Max(left, Math.Abs(ripples.HeightAt(x, 1000)));
            }
            Require(left < 0.01f, $"the water must calm (still {left:F4})");
            Require(ripples.Texture != null && ripples.Texture.GetWidth() == WaterRipples.Size, "the ripple texture");

            int packs = PackStyles();

            GD.Print($"[water] PASS: Classic default and persistence, unknown styles Classic, --water-style override, upstream effect replaced, choices, mode byte, ripples spread, follow and calm, {packs} Store pack styles installed, mounted and bound");
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
            WaterRender.ProbePackStyles = null;
        }
    }

    /// <summary>
    /// The committed style packs (design/packs/water): each is zipped as
    /// water_packs.py does, installed through StorePack.Extract, verified,
    /// mounted by StoreRuntimeContent and offered, resolved and bound by
    /// WaterRender. A pack that refuses a style file is in the corpus.
    /// </summary>
    private static int PackStyles()
    {
        string source = System.IO.Path.GetFullPath(System.IO.Path.Combine(ProjectSettings.GlobalizePath("res://"), "..", "..", "design", "packs", "water"));
        string temporary = System.IO.Path.Combine(System.IO.Path.GetTempPath(), "guo-water-probe-" + Guid.NewGuid().ToString("N"));
        var expected = new System.Collections.Generic.Dictionary<string, int> { ["guo-water-pixel"] = 2, ["guo-water-cel"] = 3, ["guo-water-realistic"] = 4, ["guo-water-classicplus"] = 5 };
        try
        {
            var styles = new System.Collections.Generic.List<IWaterStyle>();
            using var store = new GUO.Store.StoreClient("http://127.0.0.1:18865", System.IO.Path.Combine(temporary, "store"), int.MaxValue);
            foreach ((string id, int model) in expected)
            {
                string folder = System.IO.Path.Combine(source, id);
                var manifest = JsonSerializer.Deserialize<GUO.Store.StoreManifest>(System.IO.File.ReadAllBytes(System.IO.Path.Combine(folder, "pack.json")));
                byte[] water = System.IO.File.ReadAllBytes(System.IO.Path.Combine(folder, "water.json"));
                byte[] preview = Image.CreateEmpty(4, 4, false, Image.Format.Rgba8).SavePngToBuffer();
                manifest.Files = new() { ["water.json"] = Hash(water), ["preview.png"] = Hash(preview) };
                string zip = System.IO.Path.Combine(temporary, id + ".zip");
                System.IO.Directory.CreateDirectory(temporary);
                using (var archive = System.IO.Compression.ZipFile.Open(zip, System.IO.Compression.ZipArchiveMode.Create))
                {
                    foreach ((string name, byte[] data) in new[] { ("water.json", water), ("preview.png", preview), ("manifest.json", JsonSerializer.SerializeToUtf8Bytes(manifest)) })
                    {
                        using var stream = archive.CreateEntry(name).Open();
                        stream.Write(data);
                    }
                }
                string installed = System.IO.Path.Combine(temporary, "store", manifest.Id, manifest.Version);
                System.IO.Directory.CreateDirectory(installed);
                GUO.Store.StorePack.Extract(zip, installed);
                var snapshot = store.VerifyContent(manifest.Id, manifest.Version);
                var contentLock = new GUO.Store.StoreContentLock { Pack = manifest.Id, Version = manifest.Version, IdentityHash = snapshot.IdentityHash };
                GUO.Store.StoreShardContent.RequireMountable(contentLock, snapshot);
                using var content = GUO.Store.StoreRuntimeContent.Load(null, "enu", contentLock, store, apply: false);
                Require(content.WaterStyles.Count == 1 && content.WaterStyles[0].Identity == id + ":sea", id + " must mount one style");
                var style = new PackWaterStyle(content.WaterStyles[0].Identity, content.WaterStyles[0].Style);
                var bound = new System.Collections.Generic.Dictionary<string, Variant>();
                style.Bind((name, value) => bound[name] = value);
                Require(bound["water_model"].AsInt32() == model, id + " must pick its model");
                Require(bound["water_params3"].AsVector4().X == content.WaterStyles[0].Style.Grain, id + " must bind its grain");
                styles.Add(style);
            }

            WaterRender.ProbePackStyles = styles.ToArray();
            var profile = JsonSerializer.Deserialize("{}", ProfileJsonContext.DefaultToUse.Profile);
            profile.WaterStyle = "guo-water-cel:sea";
            Require(WaterRender.Resolve(profile)?.Id == "guo-water-cel:sea", "a pack style must resolve by its identity");
            Require(WaterRender.ChoiceIndex("guo-water-realistic:sea") > 1, "Options must offer the pack styles after the built-ins");
            var restored = JsonSerializer.Deserialize(JsonSerializer.Serialize(profile, ProfileJsonContext.DefaultToUse.Profile), ProfileJsonContext.DefaultToUse.Profile);
            Require(WaterRender.Resolve(restored)?.Id == "guo-water-cel:sea", "a pack style must persist");
            WaterRender.ProbePackStyles = Array.Empty<IWaterStyle>();
            Require(WaterRender.Resolve(restored) == null, "a style whose pack is gone must draw Classic");
            return styles.Count;
        }
        finally
        {
            if (System.IO.Directory.Exists(temporary))
            {
                System.IO.Directory.Delete(temporary, true);
            }
        }
    }

    private static string Hash(byte[] data) =>
        Convert.ToHexString(System.Security.Cryptography.SHA256.HashData(data)).ToLowerInvariant();

    private static void Require(bool ok, string what)
    {
        if (!ok)
        {
            throw new InvalidOperationException(what);
        }
    }
}
