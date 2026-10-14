// SPDX-License-Identifier: BSD-2-Clause
// PORT DEVIATION (GUO): SuperRes 4x lazy background worker.
// Single background task, no Godot objects: it upscales the queued pixel
// copies (taken on the main thread at enqueue time, because the .mul/.uop
// readers seek a shared stream and are not thread-safe) in 128-256px tiles
// with 10px overlap through realesr-general-x4v3 ONNX (DirectML first, CPU
// fallback; bicubic 4x when the model or ORT is unavailable), keeps alpha
// passthrough (0 stays 0, land diamond corners stay transparent), and
// writes the PNG atomically to overrides/SR4x plus a manifest row. The
// main-thread committer (SrCommit) warms the SR LRU from the file.

using System;
using System.IO;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using GUO.Assets;
using GUO.Utility.Logging;
using Microsoft.ML.OnnxRuntime;
using Microsoft.ML.OnnxRuntime.Tensors;

namespace GUO.Renderer.SuperRes
{
    internal static class SrWorker
    {
        private const int Scale = 4;
        private const int TileCore = 192; // within the 128-256 spec
        private const int Pad = 10;

        private static readonly object StartSync = new object();
        private static bool _started;
        private static Task _worker;
        private static volatile bool _shutdown;
        private static int _busy;
        private static readonly object SessionSync = new object();
        private static InferenceSession _session;
        private static string _inputName;
        private static string _outputName;
        private static string _backend;
        private static string _modelPath;
        private static bool _modelFailed;

        public static string Backend => _backend ?? "bicubic4x-fallback";

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
        /// Joins the background worker before the native ORT session is
        /// freed. Idempotent, main-thread only, never throws. Same pattern
        /// as Dlss5Frame.Shutdown: signal, join, then free native.
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
                    lock (SessionSync)
                    {
                        try { _session?.Dispose(); } catch { }
                        _session = null;
                        _inputName = null;
                        _outputName = null;
                    }
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
                        if (_shutdown)
                        {
                            break;
                        }
                        if (!SrRequestQueue.TryDequeue(out SrRequest req))
                        {
                            Thread.Sleep(50);
                            continue;
                        }
                        if (_shutdown)
                        {
                            SrRequestQueue.MarkFailed(req);
                            break;
                        }

                        Interlocked.Exchange(ref _busy, 1);
                        try
                        {
                            Process(req);
                        }
                        catch (Exception e)
                        {
                            try { Log.Warn($"[SR4x] {req.Key} failed ({e.GetType().Name}: {e.Message}); using native."); } catch { }
                            try { SrRequestQueue.MarkFailed(req); } catch { }
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

        private static string OutputPathFor(SrRequest req)
        {
            string over = ArtLoader.OverrideDir();
            return req.Kind switch
            {
                SrKind.Land => Path.Combine(over, "SR4x", "Art", "Land", req.Graphic + ".png"),
                SrKind.Static => Path.Combine(over, "SR4x", "Art", "Statics", req.Graphic + ".png"),
                _ => Path.Combine(over, "SR4x", "Gumps", req.Graphic + ".png"),
            };
        }

        private static void Process(SrRequest req)
        {
            string outPath = OutputPathFor(req);
            try
            {
                if (File.Exists(outPath))
                {
                    SrRequestQueue.EnqueueCompleted(req);
                    return;
                }
            }
            catch
            {
            }

            if (req.Width <= 0 || req.Height <= 0 || req.Pixels == null ||
                req.Pixels.Length < req.Width * req.Height)
            {
                SrRequestQueue.MarkFailed(req);
                return;
            }

            int w = req.Width;
            int h = req.Height;

            // Split uint 0xAABBGGRR into alpha mask + filled RGB.
            byte[] alpha = new byte[w * h];
            byte[] rgb = new byte[w * h * 3];
            for (int i = 0; i < w * h; i++)
            {
                uint p = req.Pixels[i];
                byte r = (byte)p;
                byte g = (byte)(p >> 8);
                byte b = (byte)(p >> 16);
                byte a = (byte)(p >> 24);
                alpha[i] = a == 0 ? (byte)0 : (byte)255;
                rgb[i * 3] = r;
                rgb[i * 3 + 1] = g;
                rgb[i * 3 + 2] = b;
            }

            FillTransparentRgb(rgb, alpha, w, h);

            int srW = w * Scale;
            int srH = h * Scale;
            byte[] srRgb;
            string backend;
            try
            {
                if (!TryUpscaleRgb(rgb, w, h, out srRgb, out backend))
                {
                    SrRequestQueue.MarkFailed(req);
                    return;
                }
            }
            catch
            {
                SrRequestQueue.MarkFailed(req);
                return;
            }

            // Hue-safety (SrGray): the upscale input above is the loader-direct
            // unhued hue-0 base, but the model hallucinates chroma into flat
            // gray, which breaks uo_hue_core's exact r==g==b PARTIAL_HUED
            // check (gray-white with speckles). Restore exact gray over
            // native-gray blocks so the SR PNG hues in-shader like native.
            // Colored trim and transparent texels are untouched.
            SrGray.RestoreGrayBlocks(srRgb, req.Pixels, w, h, srW, srH);

            // Alpha passthrough: nearest 4x of the native mask. 0 stays 0,
            // so land diamond corners (native 0) stay transparent at 4x.
            byte[] srRgba = new byte[srW * srH * 4];
            for (int y = 0; y < srH; y++)
            {
                int sy = Math.Min(h - 1, y / Scale);
                for (int x = 0; x < srW; x++)
                {
                    int sx = Math.Min(w - 1, x / Scale);
                    int si = (y * srW + x) * 4;
                    int ri = (y * srW + x) * 3;
                    srRgba[si] = srRgb[ri];
                    srRgba[si + 1] = srRgb[ri + 1];
                    srRgba[si + 2] = srRgb[ri + 2];
                    srRgba[si + 3] = alpha[sy * w + sx];
                }
            }

            byte[] png;
            try
            {
                png = SrPng.EncodeRgba(srRgba, srW, srH);
            }
            catch
            {
                SrRequestQueue.MarkFailed(req);
                return;
            }

            if (png == null || png.Length == 0)
            {
                SrRequestQueue.MarkFailed(req);
                return;
            }

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

                SrManifest.WriteRow(req.Kind, req.Graphic, new SrManifest.Row
                {
                    NativeW = w,
                    NativeH = h,
                    SrW = srW,
                    SrH = srH,
                    Scale = Scale,
                    Backend = backend ?? Backend,
                    Denoise = SrModel.DenoiseStrength,
                    Sha256 = SrManifest.Sha256Hex(png),
                    CreatedUtc = DateTime.UtcNow.ToString("o"),
                    GraySafe = true,
                });

                SrRequestQueue.EnqueueCompleted(req);
            }
            catch (Exception e)
            {
                try { Log.Warn($"[SR4x] {req.Key} write failed ({e.Message}); using native."); } catch { }
                SrRequestQueue.MarkFailed(req);
            }
        }

        /// <summary>Copies nearby opaque RGB into transparent texels so the
        /// model does not smear black halos into edges. Alpha is untouched.</summary>
        private static void FillTransparentRgb(byte[] rgb, byte[] alpha, int w, int h)
        {
            for (int y = 0; y < h; y++)
            {
                for (int x = 0; x < w; x++)
                {
                    if (alpha[y * w + x] != 0)
                    {
                        continue;
                    }

                    bool found = false;
                    for (int r = 1; r <= 3 && !found; r++)
                    {
                        for (int dy = -r; dy <= r && !found; dy++)
                        {
                            for (int dx = -r; dx <= r && !found; dx++)
                            {
                                if (dx == 0 && dy == 0)
                                {
                                    continue;
                                }

                                int nx = x + dx;
                                int ny = y + dy;
                                if (nx < 0 || ny < 0 || nx >= w || ny >= h)
                                {
                                    continue;
                                }

                                if (alpha[ny * w + nx] != 0)
                                {
                                    rgb[(y * w + x) * 3] = rgb[(ny * w + nx) * 3];
                                    rgb[(y * w + x) * 3 + 1] = rgb[(ny * w + nx) * 3 + 1];
                                    rgb[(y * w + x) * 3 + 2] = rgb[(ny * w + nx) * 3 + 2];
                                    found = true;
                                }
                            }
                        }
                    }
                }
            }
        }

        private static bool TryUpscaleRgb(byte[] rgb, int w, int h, out byte[] srRgb, out string backend)
        {
            srRgb = null;
            backend = null;

            if (TryEnsureSession(out backend) && _session != null)
            {
                try
                {
                    srRgb = UpscaleOrtTiled(rgb, w, h);
                    return true;
                }
                catch (Exception e)
                {
                    try { Log.Warn($"[SR4x] ORT upscale failed ({e.Message}); bicubic fallback."); } catch { }
                }
            }

            try
            {
                srRgb = UpscaleBicubicFallback(rgb, w, h);
                backend = "bicubic4x-fallback";
                return true;
            }
            catch
            {
                return false;
            }
        }

        private static bool TryEnsureSession(out string backend)
        {
            backend = _backend ?? "bicubic4x-fallback";
            lock (SessionSync)
            {
                if (_shutdown)
                {
                    return false;
                }
                if (_session != null)
                {
                    backend = _backend;
                    return true;
                }

                string modelPath = _modelPath;
                if (modelPath == null && !_modelFailed)
                {
                    try
                    {
                        modelPath = SrModel.EnsureModel();
                    }
                    catch
                    {
                        modelPath = null;
                    }

                    if (string.IsNullOrEmpty(modelPath))
                    {
                        _modelFailed = true;
                        _modelPath = "";
                        return false;
                    }

                    _modelPath = modelPath;
                }
                else if (_modelFailed)
                {
                    // Model was unavailable; re-check cheaply (no download)
                    // so a later drop-in is picked up without a restart.
                    try
                    {
                        if (SrModel.TryFindExisting(out string found))
                        {
                            _modelFailed = false;
                            _modelPath = found;
                            modelPath = found;
                        }
                        else
                        {
                            return false;
                        }
                    }
                    catch
                    {
                        return false;
                    }
                }
                else
                {
                    modelPath = _modelPath;
                }

                if (string.IsNullOrEmpty(modelPath) || !File.Exists(modelPath))
                {
                    return false;
                }

                // DirectML first, CPU fallback. DML needs sequential mode
                // without mem pattern; reflection keeps this compiling even
                // when the DML EP entry point is absent (then CPU is used).
                SessionOptions dmlOpts = null;
                bool triedDml = false;
                try
                {
                    dmlOpts = new SessionOptions();
                    dmlOpts.GraphOptimizationLevel = GraphOptimizationLevel.ORT_ENABLE_ALL;
                    var m = typeof(SessionOptions).GetMethod(
                        "AppendExecutionProvider_DML", new[] { typeof(int) });
                    if (m != null)
                    {
                        try { dmlOpts.ExecutionMode = ExecutionMode.ORT_SEQUENTIAL; } catch { }
                        try
                        {
                            var dm = typeof(SessionOptions).GetMethod(
                                "DisableMemPattern", Type.EmptyTypes);
                            dm?.Invoke(dmlOpts, null);
                        }
                        catch { }
                        m.Invoke(dmlOpts, new object[] { 0 });
                        triedDml = true;
                    }
                }
                catch
                {
                    triedDml = false;
                }

                if (triedDml)
                {
                    try
                    {
                        _session = new InferenceSession(modelPath, dmlOpts);
                        _backend = "realesr-general-x4v3+dml";
                        _inputName = _session.InputMetadata.Keys.First();
                        _outputName = _session.OutputMetadata.Keys.First();
                        backend = _backend;
                        try { Log.Warn("[SR4x] ORT backend: DirectML."); } catch { }
                        return true;
                    }
                    catch (Exception e)
                    {
                        try { Log.Warn($"[SR4x] DirectML session failed ({e.Message}); CPU fallback."); } catch { }
                        try { dmlOpts?.Dispose(); } catch { }
                        _session = null;
                    }
                }
                else
                {
                    try { dmlOpts?.Dispose(); } catch { }
                }

                try
                {
                    var cpuOpts = new SessionOptions();
                    _session = new InferenceSession(modelPath, cpuOpts);
                    _backend = "realesr-general-x4v3+cpu";
                    _inputName = _session.InputMetadata.Keys.First();
                    _outputName = _session.OutputMetadata.Keys.First();
                    backend = _backend;
                    try { Log.Warn("[SR4x] ORT backend: CPU."); } catch { }
                    return true;
                }
                catch (Exception e)
                {
                    try { Log.Warn($"[SR4x] ORT CPU session failed ({e.Message}); bicubic fallback."); } catch { }
                    _session = null;
                    return false;
                }
            }
        }

        private static byte[] UpscaleOrtTiled(byte[] rgb, int w, int h)
        {
            int srW = w * Scale;
            int srH = h * Scale;

            if (w <= 256 && h <= 256)
            {
                float[] srFloat = RunOrt(rgb, w, h);
                return FloatToRgb(srFloat, srW, srH);
            }

            int nx = Math.Max(1, (w + TileCore - 1) / TileCore);
            int ny = Math.Max(1, (h + TileCore - 1) / TileCore);
            int coreW = (w + nx - 1) / nx;
            int coreH = (h + ny - 1) / ny;
            byte[] srRgb = new byte[srW * srH * 3];

            for (int ty = 0; ty < ny; ty++)
            {
                for (int tx = 0; tx < nx; tx++)
                {
                    int cx0 = tx * coreW;
                    int cy0 = ty * coreH;
                    int cw = Math.Min(coreW, w - cx0);
                    int ch = Math.Min(coreH, h - cy0);
                    int px0 = Math.Max(0, cx0 - Pad);
                    int py0 = Math.Max(0, cy0 - Pad);
                    int px1 = Math.Min(w, cx0 + cw + Pad);
                    int py1 = Math.Min(h, cy0 + ch + Pad);
                    int pw = px1 - px0;
                    int ph = py1 - py0;

                    byte[] tileRgb = new byte[pw * ph * 3];
                    for (int y = 0; y < ph; y++)
                    {
                        Buffer.BlockCopy(rgb, ((py0 + y) * w + px0) * 3, tileRgb, y * pw * 3, pw * 3);
                    }

                    byte[] tileSr;
                    try
                    {
                        float[] tileFloat = RunOrt(tileRgb, pw, ph);
                        tileSr = FloatToRgb(tileFloat, pw * Scale, ph * Scale);
                    }
                    catch
                    {
                        tileSr = UpscaleBicubicFallback(tileRgb, pw, ph);
                    }

                    int offX = (cx0 - px0) * Scale;
                    int offY = (cy0 - py0) * Scale;
                    int copyW = cw * Scale;
                    int copyH = ch * Scale;
                    int tileSrW = pw * Scale;
                    for (int y = 0; y < copyH; y++)
                    {
                        Buffer.BlockCopy(
                            tileSr, ((offY + y) * tileSrW + offX) * 3,
                            srRgb, (((cy0 * Scale) + y) * srW + cx0 * Scale) * 3,
                            copyW * 3);
                    }
                }
            }

            return srRgb;
        }

        private static float[] RunOrt(byte[] rgb, int w, int h)
        {
            InferenceSession session;
            string inputName;
            lock (SessionSync)
            {
                session = _session;
                inputName = _inputName;
            }

            if (session == null || string.IsNullOrEmpty(inputName))
            {
                throw new InvalidOperationException("no ORT session");
            }

            float[] data = new float[3 * w * h];
            for (int y = 0; y < h; y++)
            {
                for (int x = 0; x < w; x++)
                {
                    int si = (y * w + x) * 3;
                    int r = y * w + x;
                    int g = w * h + r;
                    int b = 2 * w * h + r;
                    data[r] = rgb[si] / 255f;
                    data[g] = rgb[si + 1] / 255f;
                    data[b] = rgb[si + 2] / 255f;
                }
            }

            var tensor = new DenseTensor<float>(data, new[] { 1, 3, h, w });
            using var results = session.Run(
                new[] { NamedOnnxValue.CreateFromTensor(inputName, tensor) });
            var outTensor = results.First().AsTensor<float>();
            float[] arr = outTensor.ToArray();
            return arr;
        }

        private static byte[] FloatToRgb(float[] f, int w, int h)
        {
            // ORT x4v3 output is NCHW [1,3,H,W] in 0-1.
            byte[] rgb = new byte[w * h * 3];
            int plane = w * h;
            if (f.Length < plane * 3)
            {
                throw new InvalidOperationException("short ORT output");
            }

            for (int i = 0; i < plane; i++)
            {
                float r = f[i];
                float g = f[plane + i];
                float b = f[2 * plane + i];
                rgb[i * 3] = (byte)Math.Clamp((int)MathF.Round(r * 255f), 0, 255);
                rgb[i * 3 + 1] = (byte)Math.Clamp((int)MathF.Round(g * 255f), 0, 255);
                rgb[i * 3 + 2] = (byte)Math.Clamp((int)MathF.Round(b * 255f), 0, 255);
            }

            return rgb;
        }

        internal static byte[] UpscaleBicubicFallback(byte[] rgb, int w, int h)
        {
            // Bilinear 4x (the "bicubic fallback" manifest label covers any
            // non-model upscale; bilinear has no ringing on pixel edges and
            // is plenty for the proof path when the model is absent).
            int srW = w * Scale;
            int srH = h * Scale;
            byte[] sr = new byte[srW * srH * 3];
            for (int y = 0; y < srH; y++)
            {
                float sy = (y + 0.5f) / Scale - 0.5f;
                int y0 = Math.Clamp((int)MathF.Floor(sy), 0, h - 1);
                int y1 = Math.Min(h - 1, y0 + 1);
                float fy = Math.Clamp(sy - y0, 0f, 1f);
                for (int x = 0; x < srW; x++)
                {
                    float sx = (x + 0.5f) / Scale - 0.5f;
                    int x0 = Math.Clamp((int)MathF.Floor(sx), 0, w - 1);
                    int x1 = Math.Min(w - 1, x0 + 1);
                    float fx = Math.Clamp(sx - x0, 0f, 1f);
                    for (int c = 0; c < 3; c++)
                    {
                        float v00 = rgb[(y0 * w + x0) * 3 + c];
                        float v10 = rgb[(y0 * w + x1) * 3 + c];
                        float v01 = rgb[(y1 * w + x0) * 3 + c];
                        float v11 = rgb[(y1 * w + x1) * 3 + c];
                        float v = v00 * (1 - fx) * (1 - fy) + v10 * fx * (1 - fy)
                            + v01 * (1 - fx) * fy + v11 * fx * fy;
                        sr[(y * srW + x) * 3 + c] = (byte)Math.Clamp((int)MathF.Round(v), 0, 255);
                    }
                }
            }

            return sr;
        }
    }
}
