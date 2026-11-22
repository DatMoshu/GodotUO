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
            ShardConsent();
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

            WakeChecks();
            CreatureChecks();
            RecentreChecks();

            int packs = PackStyles();

            GD.Print($"[water] PASS: Classic default and persistence, unknown styles Classic, --water-style override, upstream effect replaced, choices, mode byte, ripples spread, follow and calm, boat wakes follow speed, {packs} Store pack styles installed, mounted and bound");
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
    /// Boat wakes: none at rest, a trough astern (and none ahead) under way, none
    /// shortly after stopping, none where water may not be disturbed.
    /// </summary>
    private static void WakeChecks()
    {
        var hull = new GUO.Compat.Rectangle(-2, -6, 2, 6);
        const float dt = 1f / 60f;
        var field = new WaterRipples();
        field.CentreOnForProbe(2000, 2000);
        field.SetFrameForProbe(dt);
        var boat = new WaterRipples.BoatState { X = 2000, Y = 2000, HY = -1f };
        for (int i = 0; i < 120; i++) field.Wake(boat, hull, 2000, 2000, (_, _) => true);
        Require(field.Stamps == 0, "a boat at rest leaves no wake");

        // North at 3 cells a second.
        float y = 2000;
        for (int i = 0; i < 120; i++) { y -= 3f * dt; field.Wake(boat, hull, 2000, y, (_, _) => true); }
        Require(field.Stamps > 0, "a boat under way leaves a wake");
        float cy = y + 0f;
        float astern = 0f;
        for (int dy = 6; dy <= 8; dy++) astern = Math.Min(astern, field.HeightAt(2000, (int)MathF.Round(cy + dy)));
        Require(astern < -0.01f, $"the trough is astern of the hull ({astern:F3})");
        // An arm spreads sideways with distance astern: more disturbed 3 cells off the track 3 cells astern than ahead of the hull.
        float arm = 0f;
        for (int dx = -3; dx <= 3; dx++) arm = Math.Max(arm, Math.Abs(field.HeightAt(2000 + dx, (int)MathF.Round(cy + 9f))));
        Require(arm > 0.01f, "the wake continues astern");
        // The arms are crests with foam on them, and they open: 5 cells astern the foam is off the track, to both sides.
        field.StepForProbe(1);
        float port = 0f, starboard = 0f;
        for (int dx = 1; dx <= 4; dx++)
        {
            port = Math.Max(port, field.FoamAt(2000 - dx, (int)MathF.Round(cy + 11f)));
            starboard = Math.Max(starboard, field.FoamAt(2000 + dx, (int)MathF.Round(cy + 11f)));
        }
        Require(port > 0.05f && starboard > 0.05f, $"both arms carry foam astern ({port:F3}, {starboard:F3})");
        Require(field.FoamAt(2000, (int)MathF.Round(cy - 12f)) == 0f, "no foam ahead of the bow");
        // A faster hull leaves a longer trail: more marks alive at 3 cells a second than at 1.
        var slow = new WaterRipples();
        slow.CentreOnForProbe(2000, 2000);
        slow.SetFrameForProbe(dt);
        var slowBoat = new WaterRipples.BoatState { X = 2000, Y = 2000, HY = -1f };
        float ys = 2000;
        for (int i = 0; i < 600; i++) { ys -= 1f * dt; slow.Wake(slowBoat, hull, 2000, ys, (_, _) => true); if (i % 3 == 2) slow.StepForProbe(1); }
        var fast = new WaterRipples();
        fast.CentreOnForProbe(2000, 2000);
        fast.SetFrameForProbe(dt);
        var fastBoat = new WaterRipples.BoatState { X = 2000, Y = 2000, HY = -1f };
        float yf = 2000;
        for (int i = 0; i < 600; i++) { yf -= 3f * dt; fast.Wake(fastBoat, hull, 2000, yf, (_, _) => true); if (i % 3 == 2) fast.StepForProbe(1); }
        Require(fast.MarkCount > slow.MarkCount * 1.3f, $"a faster hull leaves a longer trail ({slow.MarkCount} marks at 1 cell/s, {fast.MarkCount} at 3)");
        Require(Math.Abs(field.HeightAt(2000, (int)MathF.Round(cy - 12f))) < 1e-4f, "nothing is disturbed far ahead of the bow");

        DumpField(field, 2000, (int)MathF.Round(y), "under-way");
        for (int i = 0; i < 30; i++) field.StepForProbe(1);
        int whileMoving = field.Stamps;
        for (int i = 0; i < 90; i++) field.Wake(boat, hull, 2000, y, (_, _) => true);
        int afterStop = field.Stamps;
        for (int i = 0; i < 120; i++) field.Wake(boat, hull, 2000, y, (_, _) => true);
        Require(field.Stamps == afterStop && afterStop - whileMoving < 4 * 70, "the wake stops soon after the boat does");
        field.StepForProbe(140); // 7 s of the field's own time: the longest arm lives 6
        float rest = 0f;
        for (int dx = -6; dx <= 6; dx++) for (int dy = -14; dy <= 14; dy++) rest = Math.Max(rest, Math.Abs(field.HeightAt(2000 + dx, (int)y + dy)));
        Require(rest < 0.02f, $"the wake is gone 7 s after stopping (still {rest:F4})");
        Require(field.MarkCount == 0 && field.PeakFoam == 0f, "no mark or foam is left 7 s after stopping");

        var closed = new WaterRipples();
        closed.CentreOnForProbe(2000, 2000);
        closed.SetFrameForProbe(dt);
        var other = new WaterRipples.BoatState { X = 2000, Y = 2000, HY = -1f };
        float y2 = 2000;
        for (int i = 0; i < 120; i++) { y2 -= 3f * dt; closed.Wake(other, hull, 2000, y2, (_, _) => false); }
        Require(closed.Stamps == 0, "no wake is made on land or under a pier");
    }

    /// <summary>
    /// Swimmers: a moving one makes small short-lived rings, one that stands still makes none, a
    /// crowd does not build the surface up, and nothing is made where water may not be disturbed.
    /// </summary>
    // The wet map follows the window: after a re-centre nothing is clipped by a map of the old window.
    private static void RecentreChecks()
    {
        Func<int, int, bool> open = (x, y) => x < 1010 || x >= 1020 && x < 1040 || x >= 1050; // land: 1010..1019, 1040..1049
        var f = new WaterRipples();
        f.CentreOnForProbe(1000, 1000);
        f.SetWetForProbe(open);
        f.Stamp(1005, 1000, 2f, 0.8f);
        f.Stamp(1030, 1000, 2f, 0.8f);
        f.StepForProbe(4);
        f.CentreOnForProbe(1030, 1000); // window slides 30 cells; the wet map must slide with it
        float water = Math.Abs(f.HeightAt(1030, 1000)) + Math.Abs(f.HeightAt(1028, 1000));
        Require(water > 1e-3f, $"ripples kept on water right after the re-centre ({water:F4})");
        f.StepForProbe(6); // before any sliced rebuild has run
        float land = 0f, kept = 0f;
        for (int x = 1010; x < 1020; x++) land = Math.Max(land, Math.Abs(f.HeightAt(x, 1000)));
        for (int x = 1040; x < 1050; x++) land = Math.Max(land, Math.Abs(f.HeightAt(x, 1000)));
        for (int x = 1026; x <= 1034; x++) kept = Math.Max(kept, Math.Abs(f.HeightAt(x, 1000)));
        Require(land < 1e-6f, $"no ripple height on land after a re-centre ({land:F5})");
        Require(kept > 1e-3f, $"ripples still on water after a re-centre and steps ({kept:F5})");
        Require(f.DryTexels == 0, $"no dry texel carries height ({f.DryTexels})");
        GD.Print($"[water probe] recentre: land {land:F5}, kept {kept:F4}, dry texels {f.DryTexels}");
    }

    private static void CreatureChecks()
    {
        var f = new WaterRipples();
        f.CentreOnForProbe(2000, 2000);
        long t = 1000;
        // Still: never asked, nothing made, and the field stays flat.
        f.StepForProbe(40);
        Require(f.SwimStamps == 0 && f.PeakHeight < 1e-4f, "a swimmer that stands still makes nothing");
        // Moving for 2 s at 60 fps: a ring about every 350 ms, no more.
        for (int i = 0; i < 120; i++) { t += 16; f.Swimmer(7, 2000 + i * 0.02f, 2000, t, true); if (i % 3 == 0) f.StepForProbe(1); }
        Require(f.SwimStamps is >= 4 and <= 7, $"a moving swimmer makes a ring every ~350 ms ({f.SwimStamps} in 2 s)");
        float moving = f.PeakHeight;
        Require(moving is > 0.05f and < 0.6f, $"a swimmer's ring is visible but small (peak {moving:F3})");
        // Then still: five stacked rings die away (the sea is flat to the eye within 8 s) ...
        f.StepForProbe(160);
        Require(f.PeakHeight < 0.03f, $"a swimmer's rings are gone 8 s after it stops (peak {f.PeakHeight:F4})");
        // ... and the one ring of a single swim step within 3 s.
        var one = new WaterRipples();
        one.CentreOnForProbe(2000, 2000);
        one.Swimmer(3, 2000, 2000, 100, true);
        one.StepForProbe(2);
        float ring = one.PeakHeight;
        one.StepForProbe(80);
        Require(ring > 0.1f && one.PeakHeight < 0.04f, $"one swim step's ring lasts about 4 s (peak {ring:F3} -> {one.PeakHeight:F4})");
        // A crowd: twelve swimmers on a few cells for 6 s never build the surface past one big ring.
        var crowd = new WaterRipples();
        crowd.CentreOnForProbe(2000, 2000);
        float worst = 0f;
        t = 5000;
        for (int i = 0; i < 360; i++)
        {
            t += 16;
            for (uint s = 0; s < 12; s++) crowd.Swimmer(100 + s, 2000 + s % 4 * 0.5f, 2000 + s / 4 * 0.5f, t, true);
            if (i % 3 == 0) { crowd.StepForProbe(1); worst = Math.Max(worst, crowd.PeakHeight); }
        }
        Require(worst < 0.6f, $"a crowd of swimmers does not build up (peak {worst:F3})");
        float far = 0f;
        for (int dx = 10; dx <= 25; dx++) far = Math.Max(far, Math.Abs(crowd.HeightAt(2000 + dx, 2000)));
        Require(far < 0.03f, $"a crowd's rings stay near it (10 cells off {far:F3})");
        // Where water may not be disturbed (beach, pier): nothing.
        var shore = new WaterRipples();
        shore.CentreOnForProbe(2000, 2000);
        for (int i = 0; i < 120; i++) shore.Swimmer(9, 2000, 2000, 9000 + i * 16, false);
        Require(shore.SwimStamps == 0 && shore.Stamps == 0, "no swimmer ring on a beach or under a pier");
        GD.Print($"[water probe] creatures: moving peak {moving:F3}, crowd peak {worst:F3} far {far:F4}, stamps {f.SwimStamps}");
    }

    // GUO_WAKE_DUMP=<dir>: write the height field around the hull as a PNG (grey = flat), for a look at the wake's shape without the shader.
    private static void DumpField(WaterRipples field, int cx, int cy, string name)
    {
        string dir = System.Environment.GetEnvironmentVariable("GUO_WAKE_DUMP");
        if (string.IsNullOrEmpty(dir)) return;
        System.IO.Directory.CreateDirectory(dir);
        const int W = 24, H = 36, S = 12;
        var img = Image.CreateEmpty(W * S, H * S, false, Image.Format.Rgb8);
        for (int py = 0; py < H * S; py++)
            for (int px = 0; px < W * S; px++)
            {
                float h = field.HeightAt(cx - W / 2 + px / S, cy - H / 2 + py / S);
                float v = Math.Clamp(0.5f + h * 1.6f, 0f, 1f);
                img.SetPixel(px, py, new Color(v, v, v));
            }
        img.SavePng(System.IO.Path.Combine(dir, name + ".png"));
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

    private static void ShardConsent()
    {
        string file = GUO.Host.ShardSession.FilePath;
        var session = GUO.Host.ShardSession.Current;
        var settings = Settings.GlobalSettings;
        string temporary = System.IO.Path.Combine(System.IO.Path.GetTempPath(), "guo-water-consent-" + Guid.NewGuid().ToString("N") + ".json");
        void Offer(string style, bool locked)
        {
            System.IO.File.WriteAllText(temporary, JsonSerializer.Serialize(new GUO.Host.ShardSession.Data
            {
                Host = "127.0.0.1", Port = 2593, ContentLock = "probe-only", ContentUrl = "https://shard.example/content",
                Water = new GUO.Store.StoreWaterPolicy { Default = style, Lock = locked }
            }));
            GUO.Host.ShardSession.FilePath = temporary;
            GUO.Host.ShardSession.Load();
        }
        try
        {
            Settings.GlobalSettings = new Settings { IP = "127.0.0.1", Port = 2593 };
            Offer("uo", true);
            var p = new Profile();
            Require(!WaterRender.ShardAccepted(p) && WaterRender.Resolve(p) == null, "shard offers never enable water without consent");
            WaterRender.AcceptShard(p, true);
            Require(WaterRender.ShardAccepted(p) && WaterRender.Resolve(p)?.Id == "uo" && p.WaterStyle == "classic",
                "accepting uses the default without overwriting the personal style");
            Require(WaterRender.PlayerChoiceLists(p).Ids.Length == 1 && WaterRender.PlayerChoiceId(p, 0) == "uo", "locked choices");
            WaterRender.SetPlayerChoice(p, "classic");
            Require(WaterRender.Resolve(p)?.Id == "uo", "a locked-out style cannot bypass the policy");
            var copy = JsonSerializer.Deserialize(JsonSerializer.Serialize(p, ProfileJsonContext.DefaultToUse.Profile), ProfileJsonContext.DefaultToUse.Profile);
            Require(WaterRender.ShardAccepted(copy), "consent persists with the profile");
            GUO.Host.ShardSession.SetCurrentForProbe(null);
            Require(!WaterRender.ShardAccepted(copy) && WaterRender.Resolve(copy) == null, "leaving the shard restores the personal choice");
            GUO.Host.ShardSession.Load();
            Require(WaterRender.ShardAccepted(copy) && WaterRender.Resolve(copy)?.Id == "uo", "reconnecting to the unchanged shard preserves consent");
            WaterRender.AcceptShard(p, false);
            Require(WaterRender.Resolve(p) == null && p.WaterStyle == "classic", "withdraw consent restores the exact personal choice");
            Offer("uo", false);
            Require(!WaterRender.ShardAccepted(copy), "changed policy requires new consent");
            WaterRender.AcceptShard(p, true);
            WaterRender.SetPlayerChoice(p, "classic");
            Require(WaterRender.Resolve(p) == null && p.WaterStyle == "classic", "unlocked policy permits an override");
            Offer("missing-pack:water", true);
            WaterRender.AcceptShard(p, true);
            Require(WaterRender.Resolve(p) == null && WaterRender.PlayerChoiceLists(p).Ids[0] == "classic", "missing styles fall back without installing packs");
            Settings.GlobalSettings.Port = 2594;
            Require(!WaterRender.ShardAccepted(p) && WaterRender.ShardPolicy == null, "a policy cannot cross connection endpoints");
            GD.Print("[water consent] PASS: offer off, accept/decline/revoke, persistence/reconnect, lock/override, changed policy, missing pack and endpoint isolation");
        }
        finally
        {
            System.IO.File.WriteAllText(temporary, JsonSerializer.Serialize(session));
            GUO.Host.ShardSession.Load();
            GUO.Host.ShardSession.FilePath = file;
            Settings.GlobalSettings = settings;
            if (System.IO.File.Exists(temporary)) System.IO.File.Delete(temporary);
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
