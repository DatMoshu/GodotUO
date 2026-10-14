// SPDX-License-Identifier: BSD-2-Clause
// PORT DEVIATION (GUO): DLSS5 arbitrary-sprite bake worker.
// Single lazy background task (mirrors SrWorker): it dequeues the main-
// thread pixel copies, splits them into 320x320 RGB8 tiles with 32px
// overlap (DlssBakeTiles), runs each tile through the still network, blends
// the overlap with a linear ramp, restores exact gray over native-gray
// blocks (SrGray, so PARTIAL_HUED keeps working), keeps alpha passthrough
// (0 stays 0), and writes the full-res PNG atomically to overrides/DLSS
// plus a manifest row.
//
// Threading note: everything here is pure .NET except EnhanceTileRgb, the
// one confined Godot touchpoint. It builds a transient 320x320 RGB8 Image,
// calls Dlss5Service.EnhanceStill (which serializes on its own lock and
// touches no scene tree), reads the bytes back, and disposes both Images
// immediately. A null return (model missing, native error) falls back to
// passthrough for that tile, so the bake still completes; Backend records
// which path the last bake took.

using System;
using System.IO;
using System.Threading;
using System.Threading.Tasks;
using GUO.Assets;
using GUO.Renderer.SuperRes;
using GUO.Utility.Logging;

namespace GUO.Dlss5
{
    internal static class DlssBakeWorker
    {
        private static readonly object StartSync = new object();
        private static bool _started;
        private static Task _worker;
        private static volatile bool _shutdown;
        private static int _busy;
        private static string _backend;

        public static string Backend => _backend ?? "dlss5-pending";

        /// <summary>Starts the single lazy worker (idempotent).</summary>
        public static void EnsureStarted()
        {
            lock (StartSync)
            {
                if (_started || _shutdown)
                {
                    return;
                }

                _started = true;
                _worker = Task.Factory.StartNew(
                    Loop,
                    CancellationToken.None,
                    TaskCreationOptions.LongRunning | TaskCreationOptions.DenyChildAttach,
                    TaskScheduler.Default);
            }
        }

        /// <summary>
        /// Joins the background worker. The bake path holds no native
        /// session of its own (it goes through Dlss5Service, which is shut
        /// down after this returns), but the join still guarantees no
        /// EnhanceStill is in flight when the native instance is freed.
        /// Idempotent, main-thread only, never throws.
        /// </summary>
        public static void Shutdown()
        {
            try
            {
                _shutdown = true;
                try
                {
                    for (int i = 0; i < 50 && Interlocked.CompareExchange(ref _busy, 0, 0) == 1; i++)
                    {
                        Thread.Sleep(100);
                    }
                }
                catch { }
                Task w = null;
                try
                {
                    lock (StartSync) { w = _worker; }
                }
                catch { }
                try
                {
                    w?.Wait(5000);
                }
                catch { }
                try
                {
                    lock (StartSync) { _worker = null; }
                }
                catch { }
            }
            catch { }
        }

        private static void Loop()
        {
            try
            {
                while (!_shutdown)
                {
                    try
                    {
                        if (_shutdown || Dlss5Service.Shared.Disposed)
                        {
                            break;
                        }
                        if (!DlssBakeQueue.TryDequeue(out DlssBakeRequest req))
                        {
                            Thread.Sleep(50);
                            continue;
                        }
                        if (_shutdown)
                        {
                            try { DlssBakeQueue.MarkFailed(req); } catch { }
                            break;
                        }

                        Interlocked.Exchange(ref _busy, 1);
                        try
                        {
                            Process(req);
                        }
                        catch (Exception e)
                        {
                            try { Log.Warn($"[DLSS-bake] {req.Key} failed ({e.GetType().Name}: {e.Message}); using native."); } catch { }
                            try { DlssBakeQueue.MarkFailed(req); } catch { }
                        }
                        finally
                        {
                            Interlocked.Exchange(ref _busy, 0);
                        }
                    }
                    catch
                    {
                        try { Interlocked.Exchange(ref _busy, 0); } catch { }
                        if (_shutdown)
                        {
                            break;
                        }
                        Thread.Sleep(200);
                    }
                }
            }
            catch { }
            finally
            {
                try { Interlocked.Exchange(ref _busy, 0); } catch { }
                try
                {
                    lock (StartSync) { _worker = null; }
                }
                catch { }
            }
        }

        private static string OutputPathFor(DlssBakeRequest req)
        {
            string over = ArtLoader.OverrideDir();
            return req.Kind switch
            {
                DlssBakeKind.Land => Path.Combine(over, "DLSS", "Art", "Land", req.Graphic + ".png"),
                DlssBakeKind.Static => Path.Combine(over, "DLSS", "Art", "Statics", req.Graphic + ".png"),
                _ => Path.Combine(over, "DLSS", "Gumps", req.Graphic + ".png"),
            };
        }

        private static void Process(DlssBakeRequest req)
        {
            string outPath = OutputPathFor(req);
            try
            {
                if (File.Exists(outPath))
                {
                    DlssBakeQueue.EnqueueCompleted(req);
                    return;
                }
            }
            catch
            {
            }

            if (req.Width <= 0 || req.Height <= 0 || req.Pixels == null ||
                req.Pixels.Length < req.Width * req.Height)
            {
                DlssBakeQueue.MarkFailed(req);
                return;
            }

            if (!DlssBakeTiles.IsSizeAllowed(req.Width, req.Height))
            {
                DlssBakeQueue.MarkFailed(req);
                return;
            }

            int w = req.Width;
            int h = req.Height;

            DlssBakeTiles.SplitPixels(req.Pixels, w, h, out byte[] rgb, out byte[] alpha);
            DlssBakeTiles.FillTransparentRgb(rgb, alpha, w, h);

            float intensity = req.Intensity;
            int profile = req.Profile;
            bool anyEnhanced = false;
            bool anyFallback = false;

            byte[] outRgb;
            try
            {
                outRgb = DlssBakeTiles.Stitch(w, h, rgb, (byte[] tileIn) =>
                {
                    byte[] tileOut = EnhanceTileRgb(tileIn, intensity, profile);
                    if (tileOut != null)
                    {
                        anyEnhanced = true;
                        return tileOut;
                    }

                    anyFallback = true;
                    return tileIn; // passthrough for this tile
                });
            }
            catch
            {
                DlssBakeQueue.MarkFailed(req);
                return;
            }

            if (outRgb == null || outRgb.Length < w * h * 3)
            {
                DlssBakeQueue.MarkFailed(req);
                return;
            }

            // Hue-safety (SrGray, same invariant as SR4x): the bake input is
            // the loader-direct unhued hue-0 base, but the network can
            // hallucinate chroma into flat gray, which breaks uo_hue_core's
            // exact r==g==b PARTIAL_HUED check. Restore exact gray over
            // native-gray blocks so the baked PNG hues in-shader like native.
            try
            {
                SrGray.RestoreGrayBlocks(outRgb, req.Pixels, w, h, w, h);
            }
            catch
            {
            }

            // Alpha passthrough: same-res bake, so the native mask copies
            // directly. 0 stays 0 (land diamond corners stay transparent).
            byte[] rgba;
            try
            {
                rgba = DlssBakeTiles.InterleaveRgba(outRgb, alpha, w, h);
            }
            catch
            {
                DlssBakeQueue.MarkFailed(req);
                return;
            }

            byte[] png;
            try
            {
                png = SrPng.EncodeRgba(rgba, w, h);
            }
            catch
            {
                DlssBakeQueue.MarkFailed(req);
                return;
            }

            if (png == null || png.Length == 0)
            {
                DlssBakeQueue.MarkFailed(req);
                return;
            }

            string backend = anyEnhanced
                ? $"dlss5-still-tiled+{Dlss5Service.Shared.Provider}"
                : "passthrough-fallback";
            if (anyEnhanced && anyFallback)
            {
                backend += "+partial-fallback";
            }

            _backend = backend;

            try
            {
                Directory.CreateDirectory(Path.GetDirectoryName(outPath));
                string tmp = outPath + ".tmp-" + Guid.NewGuid().ToString("N");
                try
                {
                    File.WriteAllBytes(tmp, png);
                    File.Move(tmp, outPath, true);
                }
                finally
                {
                    try { if (File.Exists(tmp)) File.Delete(tmp); } catch { }
                }

                DlssBakeManifest.WriteRow(req.Kind, req.Graphic, new DlssBakeManifest.Row
                {
                    NativeW = w,
                    NativeH = h,
                    OutW = w,
                    OutH = h,
                    Scale = 1,
                    Backend = backend,
                    Intensity = intensity,
                    Profile = profile,
                    Tile = DlssBakeTiles.TileSize,
                    Overlap = DlssBakeTiles.Overlap,
                    Sha256 = SrManifest.Sha256Hex(png),
                    CreatedUtc = DateTime.UtcNow.ToString("o"),
                    GraySafe = true,
                });

                DlssBakeQueue.EnqueueCompleted(req);
            }
            catch (Exception e)
            {
                try { Log.Warn($"[DLSS-bake] {req.Key} write failed ({e.Message}); using native."); } catch { }
                DlssBakeQueue.MarkFailed(req);
            }
        }

        /// <summary>
        /// The one confined Godot touchpoint: 320x320 RGB8 bytes through
        /// EnhanceStill. Returns the enhanced RGB8 bytes, or null on any
        /// failure (caller falls back to passthrough). Never throws.
        /// </summary>
        internal static byte[] EnhanceTileRgb(byte[] tileRgb, float intensity, int profile)
        {
            if (tileRgb == null || tileRgb.Length < DlssBakeTiles.TileSize * DlssBakeTiles.TileSize * 3)
            {
                return null;
            }

            Godot.Image img = null;
            Godot.Image enhanced = null;
            try
            {
                img = Godot.Image.CreateFromData(
                    DlssBakeTiles.TileSize, DlssBakeTiles.TileSize, false,
                    Godot.Image.Format.Rgb8, tileRgb);
                if (img == null)
                {
                    return null;
                }

                enhanced = Dlss5Service.Shared.EnhanceStill(img, intensity, profile);
                if (enhanced == null)
                {
                    return null;
                }

                if (enhanced.GetWidth() != DlssBakeTiles.TileSize ||
                    enhanced.GetHeight() != DlssBakeTiles.TileSize)
                {
                    return null;
                }

                if (enhanced.GetFormat() != Godot.Image.Format.Rgb8)
                {
                    enhanced.Convert(Godot.Image.Format.Rgb8);
                }

                byte[] data = enhanced.GetData();
                if (data == null || data.Length < DlssBakeTiles.TileSize * DlssBakeTiles.TileSize * 3)
                {
                    return null;
                }

                var copy = new byte[DlssBakeTiles.TileSize * DlssBakeTiles.TileSize * 3];
                Buffer.BlockCopy(data, 0, copy, 0, copy.Length);
                return copy;
            }
            catch
            {
                return null;
            }
            finally
            {
                try { img?.Dispose(); } catch { }
                try { enhanced?.Dispose(); } catch { }
            }
        }
    }
}
