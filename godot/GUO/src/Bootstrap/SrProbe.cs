// SPDX-License-Identifier: BSD-2-Clause
// GUO-owned (SuperRes 4x): the headless lazy-worker proof.
//
// --sr-probe: no shard, no world. Enqueues one land, one static and one
// gump through the lazy queue (pixel copies taken here on the main thread),
// starts the single background worker, waits for the atomic PNGs plus
// manifest rows, and checks 4x size, Rgba8, alpha passthrough (0 stays 0,
// land diamond corners transparent) and queue dedup. Prints one line per
// check; exit 0 only when all pass.

using System;
using System.IO;
using Godot;
using GUO.Assets;
using GUO.Renderer.SuperRes;

namespace GUO.Host;

internal static class SrProbe
{
    public static bool Passed { get; private set; }

    // Small, always-present graphics. Deleted from SR4x first so the worker
    // must regenerate them (they rebuild in seconds via ORT or fallback).
    private const int LandGraphic = 2;
    private const int StaticGraphic = 10;
    private const int GumpGraphic = 101;

    public static async System.Threading.Tasks.Task Run(Node host)
    {
        bool ok = true;

        for (int i = 0; i < 1200 && Client.Game?.UO?.FileManager == null; i++)
        {
            await host.ToSignal(host.GetTree(), SceneTree.SignalName.ProcessFrame);
        }

        var files = Client.Game?.UO?.FileManager;
        if (files == null)
        {
            GD.PrintErr("[GUO] sr probe: the client never loaded its files.");
            Passed = false;
            return;
        }

        // Model path first (resolve + download-once log lives in EnsureModel;
        // here we only report where it resolves, never a secret).
        string modelPath;
        try
        {
            if (!SrModel.TryFindExisting(out modelPath))
            {
                modelPath = SrModel.PreferredPath() + " (missing; worker downloads once or falls back)";
            }
        }
        catch
        {
            modelPath = "(unresolvable)";
        }

        GD.Print($"[GUO] sr probe: model: {modelPath}");
        GD.Print($"[GUO] sr probe: denoise target: {SrModel.DenoiseStrength:0.0}");

        SrRequestQueue.Clear();

        // Native copies on the main thread (the readers are not thread-safe).
        // Decoded in sync helpers: ArtInfo/GumpInfo are ref structs and must
        // not live across an await boundary.
        if (!TryDecode(files, out int landW, out int landH, out uint[] landPx,
                out int staticW, out int staticH, out uint[] staticPx,
                out int gumpW, out int gumpH, out uint[] gumpPx))
        {
            Passed = false;
            return;
        }

        ok &= Expect("land native decodes", landW > 0 && landH > 0 && landPx.Length >= landW * landH,
            $"{landW}x{landH}");
        ok &= Expect("static native decodes", staticW > 0 && staticH > 0 && staticPx.Length >= staticW * staticH,
            $"{staticW}x{staticH}");
        ok &= Expect("gump native decodes", gumpW > 0 && gumpH > 0 && gumpPx.Length >= gumpW * gumpH,
            $"{gumpW}x{gumpH}");
        if (!ok)
        {
            Passed = false;
            return;
        }

        // Force regeneration of exactly these three (harmless: they rebuild).
        foreach (string p in new[]
        {
            Path.Combine(ArtLoader.OverrideDir(), "SR4x", "Art", "Land", LandGraphic + ".png"),
            Path.Combine(ArtLoader.OverrideDir(), "SR4x", "Art", "Statics", StaticGraphic + ".png"),
            Path.Combine(ArtLoader.OverrideDir(), "SR4x", "Gumps", GumpGraphic + ".png"),
        })
        {
            try { if (File.Exists(p)) File.Delete(p); } catch { }
        }

        bool e1 = SrRequestQueue.Enqueue(SrKind.Land, LandGraphic, landW, landH, landPx);
        bool e2 = SrRequestQueue.Enqueue(SrKind.Static, StaticGraphic, staticW, staticH, staticPx);
        bool e3 = SrRequestQueue.Enqueue(SrKind.Gump, GumpGraphic, gumpW, gumpH, gumpPx);
        ok &= Expect("3 enqueued", e1 && e2 && e3, $"pending={SrRequestQueue.PendingCount}");

        // Dedup: re-enqueueing the same graphic must be refused.
        bool dup = SrRequestQueue.Enqueue(SrKind.Land, LandGraphic, landW, landH, landPx);
        ok &= Expect("dedup refuses re-enqueue", !dup, "");

        SrWorker.EnsureStarted();
        GD.Print($"[GUO] sr probe: worker started (backend resolves on first upscale).");

        // Wait for the three PNGs (ORT on CPU can take ~1-2 min for three
        // small tiles; bicubic fallback is seconds).
        string landOut = Path.Combine(ArtLoader.OverrideDir(), "SR4x", "Art", "Land", LandGraphic + ".png");
        string staticOut = Path.Combine(ArtLoader.OverrideDir(), "SR4x", "Art", "Statics", StaticGraphic + ".png");
        string gumpOut = Path.Combine(ArtLoader.OverrideDir(), "SR4x", "Gumps", GumpGraphic + ".png");
        bool done = false;
        for (int i = 0; i < 360; i++)
        {
            await host.ToSignal(host.GetTree(), SceneTree.SignalName.ProcessFrame);
            try
            {
                if (File.Exists(landOut) && File.Exists(staticOut) && File.Exists(gumpOut))
                {
                    done = true;
                    break;
                }
            }
            catch
            {
            }

            if (i % 20 == 0)
            {
                GD.Print($"[GUO] sr probe: waiting ({i / 2}s; pending={SrRequestQueue.PendingCount} done={SrRequestQueue.CompletedCount} backend={SrWorker.Backend})");
            }

            await System.Threading.Tasks.Task.Delay(500);
        }

        ok &= Expect("worker wrote 3 PNGs", done,
            $"pending={SrRequestQueue.PendingCount} done={SrRequestQueue.CompletedCount} backend={SrWorker.Backend}");

        if (done)
        {
            ok &= CheckPng(landOut, landW, landH, landPx, isLand: true);
            ok &= CheckPng(staticOut, staticW, staticH, staticPx, isLand: false);
            ok &= CheckPng(gumpOut, gumpW, gumpH, gumpPx, isLand: false);

            try
            {
                string manifest = Path.Combine(ArtLoader.OverrideDir(), "SR4x", "manifest.json");
                string json = File.Exists(manifest) ? File.ReadAllText(manifest) : "";
                ok &= Expect("manifest has land row", json.Contains($"Art/Land/{LandGraphic}"), "");
                ok &= Expect("manifest has static row", json.Contains($"Art/Statics/{StaticGraphic}"), "");
                ok &= Expect("manifest has gump row", json.Contains($"Gumps/{GumpGraphic}"), "");
                ok &= Expect("manifest marks denoise", json.Contains("denoise"), "");
                ok &= Expect("manifest marks gray_safe", json.Contains("gray_safe"), "");
            }
            catch (Exception e)
            {
                ok &= Expect("manifest readable", false, e.Message);
            }
        }

        GD.Print($"[GUO] sr probe: {(ok ? "ok" : "FAIL")} (backend={SrWorker.Backend})");
        Passed = ok;
    }

    private static bool CheckPng(string path, int natW, int natH, uint[] nativePx, bool isLand)
    {
        string tag = Path.GetFileName(path);
        byte[] bytes;
        try
        {
            bytes = File.ReadAllBytes(path);
        }
        catch (Exception e)
        {
            return Expect($"{tag} readable", false, e.Message);
        }

        if (bytes == null || bytes.Length == 0)
        {
            return Expect($"{tag} non-empty", false, "");
        }

        var image = new Image();
        try
        {
            if (image.LoadPngFromBuffer(bytes) != Error.Ok)
            {
                return Expect($"{tag} is a PNG", false, "");
            }

            if (image.GetFormat() != Image.Format.Rgba8)
            {
                image.Convert(Image.Format.Rgba8);
            }

            int w = image.GetWidth();
            int h = image.GetHeight();
            bool size = w == natW * 4 && h == natH * 4;
            if (!Expect($"{tag} is 4x ({w}x{h} of {natW}x{natH})", size, ""))
            {
                return false;
            }

            if (isLand && w != h)
            {
                return Expect($"{tag} land square", false, $"{w}x{h}");
            }

            // Alpha passthrough: corners that are transparent natively must
            // be transparent at 4x (land diamond). Check the 4x4 block that
            // maps to native (0,0) when that texel is transparent.
            byte[] rgba = image.GetData();
            bool alphaOk = true;
            if (rgba != null && rgba.Length >= w * h * 4)
            {
                // Land corner (0,0) is always outside the diamond (native 0).
                int ax = 0;
                int ay = 0;
                if (rgba[(ay * w + ax) * 4 + 3] != 0)
                {
                    alphaOk = false;
                }
            }

            if (!Expect($"{tag} alpha passthrough (corner 0 stays 0)", alphaOk, ""))
            {
                return false;
            }

            // Hue-safety (SrGray): SR texels over opaque native gray must
            // stay gray, or uo_hue_core's exact PARTIAL_HUED check drops the
            // hue (gray-white with speckles). The worker restores this; the
            // probe proves it on the bytes it just wrote.
            bool grayOk = true;
            try
            {
                grayOk = SrGray.IsSrGraySafe(rgba, w, h, nativePx, natW, natH);
            }
            catch
            {
                grayOk = false;
            }

            return Expect($"{tag} gray-base preserved (hue-safe)", grayOk, "");
        }
        finally
        {
            try { image.Dispose(); } catch { }
        }
    }

    private static bool TryDecode(GUO.Assets.UOFileManager files,
        out int landW, out int landH, out uint[] landPx,
        out int staticW, out int staticH, out uint[] staticPx,
        out int gumpW, out int gumpH, out uint[] gumpPx)
    {
        landW = landH = staticW = staticH = gumpW = gumpH = 0;
        landPx = staticPx = gumpPx = System.Array.Empty<uint>();
        try
        {
            ArtInfo land = files.Arts.GetArt((uint)LandGraphic);
            landW = land.Width; landH = land.Height;
            landPx = land.Pixels.ToArray();
            ArtInfo statik = files.Arts.GetArt(ArtLoader.MAX_LAND_DATA_INDEX_COUNT + (uint)StaticGraphic);
            staticW = statik.Width; staticH = statik.Height;
            staticPx = statik.Pixels.ToArray();
            GumpInfo gump = files.Gumps.GetGump((uint)GumpGraphic);
            gumpW = gump.Width; gumpH = gump.Height;
            gumpPx = gump.Pixels.ToArray();
            return true;
        }
        catch (Exception e)
        {
            GD.PrintErr($"[GUO] sr probe: native decode failed ({e.Message}).");
            return false;
        }
    }

    private static bool Expect(string what, bool pass, string detail)
    {
        GD.Print($"[GUO] sr probe: {(pass ? "pass" : "FAIL")}: {what}" +
            (string.IsNullOrEmpty(detail) ? "" : $" ({detail})"));
        return pass;
    }
}
