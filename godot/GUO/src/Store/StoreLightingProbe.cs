// SPDX-License-Identifier: BSD-2-Clause
using System;
using System.Collections.Generic;
using System.IO;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using Godot;
using GUO.Configuration;
using GUO.Game.Scenes;
using GUO.Renderer.Lights;
using GUO.Renderer.PostFx;

namespace GUO.Store;

public partial class StoreLightingProbe : Node
{
    public override void _Ready() => GetTree().Quit(Run() ? 0 : 1);

    internal static bool Run()
    {
        string temporary = Path.Combine(Path.GetTempPath(), "guo-lighting-probe-" + Guid.NewGuid().ToString("N"));
        try
        {
            var profile = JsonSerializer.Deserialize("{}", ProfileJsonContext.DefaultToUse.Profile);
            StorePack.Require(!profile.UseHDLight && !profile.HdSunAuto && profile.HdSunHour == 12, "Existing profiles must default off/noon/manual");
            profile.UseHDLight = true; profile.HdSunHour = 7.5f; profile.HdSunAuto = true;
            var restored = JsonSerializer.Deserialize(JsonSerializer.Serialize(profile, ProfileJsonContext.DefaultToUse.Profile), ProfileJsonContext.DefaultToUse.Profile);
            StorePack.Require(restored.UseHDLight && restored.HdSunAuto && restored.HdSunHour == 7.5f, "Lighting options must persist");
            for (int bits = 0; bits < 8; bits++)
                StorePack.Require(HdLightingPolicy.Enabled((bits & 1) != 0, (bits & 2) != 0, (bits & 4) != 0) == (bits == 3), "Off/missing-pack/death must retain Classic path");
            Directory.CreateDirectory(temporary);
            string installed = Path.Combine(temporary, "store", "lighting-test", "1.0.0");
            Directory.CreateDirectory(installed);
            var manifest = new StoreManifest { Schema = "guo/store-pack@2", Kind = "content", Id = "lighting-test", Version = "1.0.0", Title = "Synthetic lighting", Author = "GUO tests",
                Licence = "CC0-1.0", MinProfileVersion = 12, Target = "client", Preview = "preview.png", Dependencies = new(), Components = new() { new StoreComponent { Id = "lighting", Type = "postfx", Target = "client", Entry = "hd_light.json" } }, Files = new() };
            var payload = new Dictionary<string, byte[]> { ["hd_light.json"] = Encoding.UTF8.GetBytes("{\"name\":\"synthetic\",\"passes\":[{\"shader\":\"hd_sun\"},{\"shader\":\"hd_tonemap\"}]}"), ["preview.png"] = new byte[] { 0 } };
            foreach (string name in StoreLighting.ShaderNames) payload[name + ".gdshader"] = Encoding.UTF8.GetBytes("shader_type canvas_item; render_mode unshaded, blend_disabled; void fragment(){COLOR=vec4(1.0);}");
            foreach (var (name, data) in payload) { File.WriteAllBytes(Path.Combine(installed, name), data); manifest.Files[name] = Convert.ToHexString(SHA256.HashData(data)).ToLowerInvariant(); }
            StorePack.Validate(manifest);
            File.WriteAllBytes(Path.Combine(installed, "manifest.json"), JsonSerializer.SerializeToUtf8Bytes(manifest));
            using var store = new StoreClient("http://127.0.0.1:18865", Path.Combine(temporary, "store"), int.MaxValue);
            var snapshot = store.VerifyContent(manifest.Id, manifest.Version);
            using var content = StoreRuntimeContent.Load(null, "enu", new StoreContentLock { Pack = manifest.Id, Version = manifest.Version, IdentityHash = snapshot.IdentityHash }, store, apply: false);
            var verified = snapshot.Packs[manifest.Id];
            var lighting = content.Lighting;
            StorePack.Require(lighting != null, "Activated typed lighting component was not mounted");
            var incompatible = JsonSerializer.Deserialize<StoreManifest>(JsonSerializer.SerializeToUtf8Bytes(manifest));
            byte[] doubled = Encoding.UTF8.GetBytes("{\"passes\":[{\"shader\":\"hd_tonemap\"},{\"shader\":\"hd_night_vision\"}]}");
            incompatible.Files["hd_light.json"] = Convert.ToHexString(SHA256.HashData(doubled)).ToLowerInvariant();
            File.WriteAllBytes(Path.Combine(installed, "hd_light.json"), doubled);
            Refuses(() => StoreLighting.Decode(new StoreVerifiedPack(installed, incompatible), manifest.Components[0]), "Double lighting composites were accepted");
            File.WriteAllBytes(Path.Combine(installed, "hd_light.json"), payload["hd_light.json"]);
            foreach (byte[] bad in new[] { Encoding.UTF8.GetBytes("# include \"res://outside.gdshaderinc\""), new byte[] { 255 }, new byte[65537] })
            {
                var malformed = JsonSerializer.Deserialize<StoreManifest>(JsonSerializer.SerializeToUtf8Bytes(manifest));
                malformed.Files["hd_sun.gdshader"] = Convert.ToHexString(SHA256.HashData(bad)).ToLowerInvariant();
                File.WriteAllBytes(Path.Combine(installed, "hd_sun.gdshader"), bad);
                Refuses(() => StoreLighting.Decode(new StoreVerifiedPack(installed, malformed), manifest.Components[0]), "Malformed shader was accepted");
            }
            File.WriteAllBytes(Path.Combine(installed, "hd_sun.gdshader"), payload["hd_sun.gdshader"]);
            var stack = PostFxStack.Instance;
            var originalLook = stack.Preset;
            stack.SetHdLighting(lighting);
            StorePack.Require(stack.ActivePreset.Name == "synthetic" && ReferenceEquals(stack.Preset, originalLook) && stack.HdHeightTex == null, "Verified look was not selected lazily or changed ordinary look");
            content.Dispose();
            StorePack.Require(content.Lighting == null && ReferenceEquals(stack.ActivePreset, originalLook) && stack.HdHeightTex == null && stack.IdCanvas == default, "Mount removal did not restore ordinary look/resources");
            File.WriteAllText(Path.Combine(installed, "hd_sun.gdshader"), "changed");
            Refuses(() => StoreLighting.Decode(verified, manifest.Components[0]), "Changed shader was accepted");
            File.Delete(Path.Combine(installed, "hd_sun.gdshader"));
            Refuses(() => StoreLighting.Decode(verified, manifest.Components[0]), "Missing shader was accepted");
            StorePack.Require(HdSun.Evaluate(12).Energy > HdSun.Evaluate(0).Energy && HdSun.Evaluate(-1).IsNight, "Sun curve/wrap wrong");
            HdSun.Hour = 23.99f; HdSun.Auto = true; HdSun.Advance(1); StorePack.Require(HdSun.Hour < 1, "Sun cycle does not wrap");
            StorePack.Require(HdWeather.FlashEnvelope(0) == 1 && HdWeather.FlashEnvelope(3) == 0 && HdWeather.FlashEnvelope(float.NaN) == 0, "Thunder envelope wrong");
            StorePack.Require(HdGi.For(false, 1) == 10 && HdGi.For(true, 1) == 6 && HdGi.For(false, 2) == 6, "Governor wrong");
            StorePack.Require(HdLightingPolicy.IsUnlitFixture(0x0A28) && !HdLightingPolicy.IsUnlitFixture(0x0A0F), "Fixture classification wrong");
            HdWeather.Reset(); HdSun.Auto = false; HdSun.Hour = 12;
            GD.Print("[HD lighting] PASS: default-off persistence, no-pack/death gate, verified shader read/tamper, look restoration, sun/weather/governor and unlit fixture");
            return true;
        }
        catch (Exception ex) { GD.PrintErr("[HD lighting] FAIL: " + ex); return false; }
        finally { PostFxStack.Instance.SetHdLighting(null); if (Directory.Exists(temporary)) Directory.Delete(temporary, true); }
    }

    private static void Refuses(Action action, string message)
    { bool refused = false; try { action(); } catch (Exception ex) when (ex is InvalidDataException or IOException) { refused = true; } StorePack.Require(refused, message); }
}
