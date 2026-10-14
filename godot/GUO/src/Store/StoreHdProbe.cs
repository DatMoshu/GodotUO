// SPDX-License-Identifier: BSD-2-Clause
using System;
using System.Collections.Generic;
using System.IO;
using System.IO.Compression;
using System.Linq;
using System.Security.Cryptography;
using System.Text.Json;
using Godot;
using GUO.Configuration;
using GUO.Renderer;

namespace GUO.Store;

public partial class StoreHdProbe : Node
{
    public override void _Ready() => GetTree().Quit(Run() ? 0 : 1);

    internal static bool Run()
    {
        string temporary = Path.Combine(Path.GetTempPath(), "guo-hd-probe-" + Guid.NewGuid().ToString("N"));
        try
        {
            Directory.CreateDirectory(temporary);
            var profile = JsonSerializer.Deserialize("{}", ProfileJsonContext.DefaultToUse.Profile);
            StorePack.Require(!profile.EnhancedHD && profile.HdFilter == HdFilterMode.Auto, "Existing profiles must default off/Auto");
            profile.EnhancedHD = true; profile.HdFilter = HdFilterMode.PointMipmaps;
            var restored = JsonSerializer.Deserialize(JsonSerializer.Serialize(profile, ProfileJsonContext.DefaultToUse.Profile), ProfileJsonContext.DefaultToUse.Profile);
            StorePack.Require(restored.EnhancedHD && restored.HdFilter == HdFilterMode.PointMipmaps, "HD options must persist");
            StorePack.Require(HdSampling.Select(2, 2, HdFilterMode.Auto) == SamplerState.PointClamp
                && HdSampling.Select(2, 1, HdFilterMode.Auto) == SamplerState.PointMipmapsClamp
                && HdSampling.Select(2, 3, HdFilterMode.Point) == SamplerState.PointClamp
                && HdSampling.Select(2, 2, HdFilterMode.PointMipmaps) == SamplerState.PointMipmapsClamp
                && HdSampling.Select(2, 2, HdFilterMode.Smooth) == SamplerState.SmoothMipmapsClamp, "HD filter contract wrong");
            byte[] rgba = new byte[4 * 6 * 4];
            for (int i = 0; i < rgba.Length; i += 4) { rgba[i] = 127; rgba[i + 1] = 200; rgba[i + 2] = 17; rgba[i + 3] = 255; }
            using var image = Image.CreateFromData(4, 6, false, Image.Format.Rgba8, rgba);
            byte[] png = image.SavePngToBuffer();
            var component = new StoreComponent { Id = "sprite", Type = "hd-art", Target = "client", Entry = "sprite.png", Asset = "static",
                Graphic = 3701, Scale = 2, NativeWidth = 2, NativeHeight = 3 };
            var manifest = new StoreManifest { Schema = "guo/store-pack@2", Id = "hd-test", Version = "1.0.0", Kind = "content", Title = "Synthetic HD test",
                Author = "GUO tests", Licence = "CC0-1.0", MinProfileVersion = 12, Preview = "sprite.png", Target = "client", Dependencies = new(),
                Components = new() { component }, Files = new() { ["sprite.png"] = Convert.ToHexString(SHA256.HashData(png)).ToLowerInvariant() } };
            string zip = Path.Combine(temporary, "fixture.zip");
            using (var archive = ZipFile.Open(zip, ZipArchiveMode.Create))
            {
                using (var stream = archive.CreateEntry("sprite.png").Open()) stream.Write(png);
                using (var stream = archive.CreateEntry("manifest.json").Open()) stream.Write(JsonSerializer.SerializeToUtf8Bytes(manifest));
            }
            string installed = Path.Combine(temporary, "store", manifest.Id, manifest.Version);
            Directory.CreateDirectory(installed); StorePack.Extract(zip, installed);
            using var store = new StoreClient("http://127.0.0.1:18865", Path.Combine(temporary, "store"), int.MaxValue);
            var snapshot = store.VerifyContent(manifest.Id, manifest.Version);
            using var content = StoreRuntimeContent.Load(null, "enu", new StoreContentLock { Pack = manifest.Id, Version = manifest.Version, IdentityHash = snapshot.IdentityHash }, store, apply: false);
            StorePack.Require(content.TryHd(false, 3701, out var hd) && !content.TryHd(true, 3701, out _), "HD namespace binding wrong");
            StorePack.Require(hd.NativeWidth == 2 && hd.NativeHeight == 3 && hd.Width == 4 && hd.Height == 6, "HD footprint wrong");
            using var uploaded = hd.Texture().GetImage();
            StorePack.Require(uploaded.HasMipmaps() && uploaded.GetData().Take(rgba.Length).SequenceEqual(rgba), "HD RGBA detail/mipmaps not preserved");
            component.NativeWidth = 3;
            Refuses(() => { long budget = 0; using var bad = StoreHdAsset.Decode(component, png, ref budget); }, "Mismatched HD dimensions accepted");
            component.NativeWidth = 2;
            Refuses(() => { long budget = 64 * 1024 * 1024; using var bad = StoreHdAsset.Decode(component, png, ref budget); }, "HD memory budget not enforced");
            Refuses(() => { long budget = 0; using var bad = StoreHdAsset.Decode(component, new byte[24], ref budget); }, "Malformed HD PNG accepted");
            GD.Print("[HD packs] PASS: installed binding, default-off/Auto persistence, native footprint, RGBA/mipmaps and four filter choices");
            return true;
        }
        catch (Exception ex) { GD.PrintErr("[HD packs] FAIL: " + ex); return false; }
        finally { if (Directory.Exists(temporary)) Directory.Delete(temporary, true); }
    }

    private static void Refuses(Action action, string message)
    { bool refused = false; try { action(); } catch (InvalidDataException) { refused = true; } StorePack.Require(refused, message); }
}
