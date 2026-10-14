// SPDX-License-Identifier: BSD-2-Clause
// PORT DEVIATION (GUO): SuperRes 4x lazy upscaler model resolution.
// Resolves realesr-general-x4v3.onnx under %LOCALAPPDATA%/GUO/models or
// UO_CACHE_DIR/models, downloading once when missing with a clear log
// (no secrets). No Godot objects: safe from the background worker.

using System;
using System.IO;
using System.Net.Http;
using GUO.Utility.Logging;

namespace GUO.Renderer.SuperRes
{
    internal static class SrModel
    {
        public const string FileName = "realesr-general-x4v3.onnx";

        /// <summary>Target denoise strength (~0.4). The x4v3 ONNX bakes its
        /// denoise; this is the provenance value stored in the manifest and
        /// the blend the fallback honours (model-first, bicubic rest).</summary>
        public const float DenoiseStrength = 0.4f;

        // Primary + fallback hosts for the 4.65MB ONNX. Both are public
        // mirrors of the official x4v3 weights; only the host is logged.
        public const string PrimaryUrl =
            "https://huggingface.co/Heliosoph/realesrgan-onnx/resolve/main/realesr-general-x4v3.onnx";
        public const string FallbackUrl =
            "https://github.com/Rahmanvai/upscaler-model/raw/main/realesr-general-x4v3.onnx";

        /// <summary>Explicit override (full path) when set.</summary>
        public static string EnvOverride()
        {
            try
            {
                string v = Environment.GetEnvironmentVariable("GUO_SR_MODEL");
                return string.IsNullOrWhiteSpace(v) ? null : v.Trim();
            }
            catch
            {
                return null;
            }
        }

        public static string CacheModelsDir()
        {
            try
            {
                string cache = Environment.GetEnvironmentVariable("UO_CACHE_DIR");
                if (!string.IsNullOrWhiteSpace(cache))
                {
                    return Path.Combine(cache, "models");
                }
            }
            catch
            {
            }

            return null;
        }

        public static string LocalModelsDir()
        {
            try
            {
                string local = Environment.GetEnvironmentVariable("LOCALAPPDATA");
                if (string.IsNullOrEmpty(local))
                {
                    local = Path.Combine(
                        Environment.GetFolderPath(Environment.SpecialFolder.UserProfile),
                        "AppData", "Local");
                }

                return Path.Combine(local, "GUO", "models");
            }
            catch
            {
                return null;
            }
        }

        /// <summary>Where a fresh download lands: UO_CACHE_DIR/models first,
        /// else %LOCALAPPDATA%/GUO/models.</summary>
        public static string PreferredPath()
        {
            string over = EnvOverride();
            if (!string.IsNullOrEmpty(over))
            {
                return over;
            }

            string cache = CacheModelsDir();
            if (!string.IsNullOrEmpty(cache))
            {
                return Path.Combine(cache, FileName);
            }

            return Path.Combine(LocalModelsDir() ?? Path.GetTempPath(), FileName);
        }

        /// <summary>True when the model file already exists anywhere we look.</summary>
        public static bool TryFindExisting(out string path)
        {
            path = null;
            try
            {
                string over = EnvOverride();
                if (!string.IsNullOrEmpty(over) && File.Exists(over))
                {
                    path = over;
                    return true;
                }

                string cache = CacheModelsDir();
                if (!string.IsNullOrEmpty(cache))
                {
                    string p = Path.Combine(cache, FileName);
                    if (File.Exists(p))
                    {
                        path = p;
                        return true;
                    }
                }

                string local = LocalModelsDir();
                if (!string.IsNullOrEmpty(local))
                {
                    string p = Path.Combine(local, FileName);
                    if (File.Exists(p))
                    {
                        path = p;
                        return true;
                    }
                }
            }
            catch
            {
            }

            return false;
        }

        /// <summary>Existing path, else a download-once to the preferred
        /// path, else null (caller falls back to bicubic). Logs clearly.</summary>
        public static string EnsureModel()
        {
            if (TryFindExisting(out string found))
            {
                return found;
            }

            string dest = PreferredPath();
            try
            {
                Log.Warn($"[SR4x] model missing at {dest}; downloading once " +
                    $"(realesr-general-x4v3, ~5MB). Set GUO_SR_MODEL to skip.");
            }
            catch
            {
            }

            foreach (string url in new[] { PrimaryUrl, FallbackUrl })
            {
                try
                {
                    string host;
                    try { host = new Uri(url).Host; } catch { host = "model host"; }
                    try { Log.Warn($"[SR4x] fetching model from {host} ..."); } catch { }

                    Directory.CreateDirectory(Path.GetDirectoryName(dest));
                    string tmp = dest + ".dl-" + Guid.NewGuid().ToString("N") + ".tmp";
                    try
                    {
                        using var http = new HttpClient { Timeout = TimeSpan.FromMinutes(5) };
                        using var resp = http.GetAsync(url).GetAwaiter().GetResult();
                        resp.EnsureSuccessStatusCode();
                        using (var fs = new FileStream(tmp, FileMode.Create, FileAccess.Write))
                        {
                            resp.Content.CopyToAsync(fs).GetAwaiter().GetResult();
                            fs.Flush(true);
                        }

                        var info = new FileInfo(tmp);
                        if (info.Length < 1024 * 1024)
                        {
                            try { File.Delete(tmp); } catch { }
                            try { Log.Warn($"[SR4x] download from {host} too small ({info.Length}B); trying next."); } catch { }
                            continue;
                        }

                        File.Move(tmp, dest, true);
                        try { Log.Warn($"[SR4x] model ready at {dest} ({info.Length / 1048576}MB)."); } catch { }
                        return dest;
                    }
                    finally
                    {
                        try { if (File.Exists(tmp)) File.Delete(tmp); } catch { }
                    }
                }
                catch (Exception e)
                {
                    try { Log.Warn($"[SR4x] model download failed ({e.GetType().Name}: {e.Message}); no secrets logged."); } catch { }
                }
            }

            try { Log.Warn("[SR4x] model unavailable; using bicubic 4x fallback (manifest marks backend)."); } catch { }
            return null;
        }
    }
}
