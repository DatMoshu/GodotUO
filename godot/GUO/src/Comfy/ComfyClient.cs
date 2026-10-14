// SPDX-License-Identifier: BSD-2-Clause

using System;
using System.Collections.Generic;
using System.IO;
using System.Net.Http;
using System.Text;
using System.Text.Json;
using System.Threading;
using System.Threading.Tasks;
using GUO.Assets;

namespace GUO.Comfy
{
    /// <summary>
    /// The local ComfyUI server, spoken to the same way tools/comfy/run.py
    /// speaks to it: upload an image, queue an API-format workflow, poll
    /// /history, download every file ref it finds. No third-party packages:
    /// HttpClient plus System.Text.Json, like StoreClient.
    /// Long generations run minutes; callers poll on a worker thread and
    /// marshal results back themselves (gumps pick them up in Update).
    /// </summary>
    internal static class ComfyClient
    {
        public const string TemplatePath = "res://assets/comfy/guo_img2splat_api.json";
        public const string AudioTemplatePath = "res://assets/comfy/guo_audio_api.json";
        public const string FluxTemplatePath = "res://assets/comfy/guo_flux_edit_api.json";
        public const string RepaintTemplatePath = "res://assets/comfy/guo_repaint_api.json";
        public const string InpaintTemplatePath = "res://assets/comfy/guo_inpaint_api.json";
        public const string DepthControlModel = "control_v11f1p_sd15_depth.pth";
        public const string DepthControlModelAlt = "control_v11f1p_sd15_depth.safetensors";
        public const string FluxDepthControlModel = "flux-depth-controlnet.safetensors";
        public const string DepthFallbackStatus = "Depth model missing — ran without depth (put control_v11f1p_sd15_depth.pth in ComfyUI/models/controlnet/)";
        public const float DefaultDepthStrength = 0.6f;
        public const float DefaultDepthStart = 0f;
        public const float DefaultDepthEnd = 0.8f;

        /// <summary>
        /// Auto-batch throttle: at most this many prompts poll in flight at
        /// once, and the submit burst pauses when the server already holds
        /// more than <see cref="AutoQueuePauseThreshold"/> queued prompts.
        /// ComfyUI itself serialises execution; this only bounds client
        /// polling pressure, not server throughput.
        /// </summary>
        public const int AutoMaxInFlight = 4;
        public const int AutoQueuePauseThreshold = 8;

        public sealed class AudioResult
        {
            public byte[] Audio;
            public string FileName;
        }

        /// <summary>
        /// Stable Audio music/SFX (the frozen WORKINGAUDIO template): prompt,
        /// duration seconds, seed; reprompt stays off (its TextGenerate path
        /// crashes the matmul on some clips). Downloads the first MP3.
        /// </summary>
        public static async Task<AudioResult> GenerateAudioAsync(
            string url, string prompt, float duration, int seed,
            Action<string> report, CancellationToken ct, int timeoutMinutes = 30)
        {
            report?.Invoke("Queueing audio...");
            using JsonDocument doc = JsonDocument.Parse(LoadTemplate(AudioTemplatePath));
            var nodes = new Dictionary<string, object>();
            foreach (JsonProperty node in doc.RootElement.EnumerateObject())
            {
                var inputs = new Dictionary<string, object>();
                if (node.Value.TryGetProperty("inputs", out JsonElement ins))
                {
                    foreach (JsonProperty input in ins.EnumerateObject())
                    {
                        inputs[input.Name] = ReadNodeValue(input.Value);
                    }
                }

                nodes[node.Name] = Node(
                    node.Value.GetProperty("class_type").GetString(), inputs);
            }

            SetInput(nodes, "88", "value", prompt);
            SetInput(nodes, "83", "value", duration);
            SetInput(nodes, "71", "seed", (object)seed);
            SetInput(nodes, "82", "value", false);
            string promptId = await QueueWorkflowAsync(url, nodes, ct);
            report?.Invoke("Generating audio...");
            var deadline = DateTime.UtcNow.AddMinutes(timeoutMinutes);
            while (true)
            {
                ct.ThrowIfCancellationRequested();
                foreach (FileRef file in await TryFetchFilesAsync(
                    url, promptId, new[] { ".mp3", ".wav", ".ogg", ".flac" }, ct))
                {
                    byte[] bytes = await DownloadViewAsync(url, file, ct);
                    report?.Invoke($"Downloaded {file.FileName} ({bytes.Length / 1024} KiB).");
                    return new AudioResult { Audio = bytes, FileName = file.FileName };
                }

                string execError = await GetPromptErrorAsync(url, promptId, ct);
                if (execError != null)
                {
                    throw ThrowForExecutionError(execError, promptId, null);
                }

                if (DateTime.UtcNow > deadline)
                {
                    throw new TimeoutException($"no audio from prompt {promptId} in {timeoutMinutes} min");
                }

                await Task.Delay(TimeSpan.FromSeconds(5), ct);
            }
        }

        /// <summary>ComfyUI base URL: UO_COMFY_URL, else the local default.</summary>
        public static string ResolveUrl()
        {
            string url = Environment.GetEnvironmentVariable("UO_COMFY_URL");
            if (!string.IsNullOrWhiteSpace(url))
            {
                return url.TrimEnd('/');
            }

            return "http://127.0.0.1:8188";
        }

        public sealed class PlyResult
        {
            public byte[] Ply;
            public string FileName;
            public string Subfolder = "";
            public string Type = "output";

            /// <summary>
            /// The workflow's own render of the finished splat (node 144),
            /// for before/after previews. Optional: a missing render never
            /// fails the run; the PLY is the payload.
            /// </summary>
            public byte[] PreviewPng;
            public string PreviewFileName = "";
        }

        public sealed class FileRef
        {
            public string FileName = "";
            public string Subfolder = "";
            public string Type = "output";
        }

        public sealed class PngResult
        {
            public byte[] Png;
            public string FileName;
        }

        /// <summary>Client artwork as PNG plus raw pixels (masking needs both).</summary>
        public sealed class ArtShot
        {
            public byte[] Png;
            public byte[] Rgba;
            public int Width;
            public int Height;
            public int TightWidth;
            // Tight bbox origin + height: the MoGe replacer anchors the
            // billboard from bbox-center to the tile anchor (RSP), which
            // needs the full tight rect, not just its width.
            public int TightX0;
            public int TightY0;
            public int TightHeight;

            /// <summary>
            /// Diffusion working copy: the art nearest-upscaled so its long
            /// side suits SD1.5 (multiples of 8). The repaint uploads this;
            /// the mask and the override stay at the native size.
            /// </summary>
            public byte[] WorkPng;
            public int WorkWidth;
            public int WorkHeight;
        }

        /// <summary>Long side of the diffusion working copy; see ArtShot.</summary>
        public const int WorkLongSide = 512;

        /// <summary>
        /// Static (or land) artwork as PNG bytes, main thread: the file
        /// readers and Godot Images stay off worker threads. Swizzle mirrors
        /// the authoring preview (uint 0x00BBGGRR, 0 is transparent).
        /// </summary>
        public static ArtShot ExtractArtShot(ushort graphic, bool isLand)
        {
            uint index = isLand ? graphic : 0x4000u + graphic;
            ArtInfo art = Client.Game.UO.FileManager.Arts.GetArt(index);
            if (art.Width <= 0 || art.Height <= 0 || art.Pixels.Length < art.Width * art.Height)
            {
                return null;
            }

            byte[] rgba = new byte[art.Width * art.Height * 4];
            int x0 = art.Width, x1 = -1, y0 = art.Height, y1 = -1;
            for (int i = 0; i < art.Width * art.Height; i++)
            {
                uint pixel = art.Pixels[i];
                int at = i * 4;
                rgba[at] = (byte)pixel;
                rgba[at + 1] = (byte)(pixel >> 8);
                rgba[at + 2] = (byte)(pixel >> 16);
                rgba[at + 3] = pixel == 0 ? (byte)0 : (byte)255;
                if (pixel != 0)
                {
                    int x = i % art.Width, y = i / art.Width;
                    if (x < x0)
                    {
                        x0 = x;
                    }

                    if (x > x1)
                    {
                        x1 = x;
                    }

                    if (y < y0)
                    {
                        y0 = y;
                    }

                    if (y > y1)
                    {
                        y1 = y;
                    }
                }
            }

            using Godot.Image image = Godot.Image.CreateFromData(
                art.Width, art.Height, false, Godot.Image.Format.Rgba8, rgba);

            // Diffusion models collapse on postage-stamp inputs (a lamp is
            // tens of pixels; the VAE eats 8x, so denoise runs in a ~5x7
            // latent and decodes to mush). Work at an integer-upscaled copy
            // and shrink the result back on return.
            int longest = Math.Max(art.Width, art.Height);
            int scale = Math.Max(1, (WorkLongSide + longest - 1) / longest);
            int workW = Math.Max(8, ((art.Width * scale + 7) / 8) * 8);
            int workH = Math.Max(8, ((art.Height * scale + 7) / 8) * 8);
            byte[] workPng;
            using (Godot.Image work = Godot.Image.CreateFromData(
                art.Width, art.Height, false, Godot.Image.Format.Rgba8, rgba))
            {
                work.Resize(workW, workH, Godot.Image.Interpolation.Nearest);
                workPng = work.SavePngToBuffer();
            }

            return new ArtShot
            {
                Png = image.SavePngToBuffer(),
                Rgba = rgba,
                Width = art.Width,
                Height = art.Height,
                TightWidth = x1 >= x0 ? x1 - x0 + 1 : 0,
                TightX0 = x1 >= x0 ? x0 : 0,
                TightY0 = y1 >= y0 ? y0 : 0,
                TightHeight = y1 >= y0 ? y1 - y0 + 1 : 0,
                WorkPng = workPng,
                WorkWidth = workW,
                WorkHeight = workH,
            };
        }

        /// <summary>
        /// Simple depth map for the repaint ControlNet path: opaque pixels
        /// map by luminance into 96-255 (near), transparent maps to 0 (far).
        /// Main thread (Godot Image). No MiDaS/DepthAnything preprocessor on
        /// the server (checked /object_info: ControlNetLoader and
        /// ControlNetApplyAdvanced exist, no depth preprocessors), so the
        /// map is built here and uploaded as the ControlNet image input.
        /// </summary>
        public static byte[] BuildDepthMapPng(byte[] workPng)
        {
            if (workPng == null || workPng.Length == 0)
            {
                throw new ArgumentException("no artwork bytes for depth map");
            }

            using var src = new Godot.Image();
            if (src.LoadPngFromBuffer(workPng) != Godot.Error.Ok)
            {
                throw new InvalidDataException("artwork is not a PNG");
            }

            if (src.GetFormat() != Godot.Image.Format.Rgba8)
            {
                src.Convert(Godot.Image.Format.Rgba8);
            }

            int w = src.GetWidth();
            int h = src.GetHeight();
            byte[] rgba = src.GetData();
            byte[] gray = new byte[w * h * 4];
            for (int i = 0; i < w * h; i++)
            {
                byte r = rgba[i * 4];
                byte g = rgba[i * 4 + 1];
                byte b = rgba[i * 4 + 2];
                byte a = rgba[i * 4 + 3];
                byte d;
                if (a == 0)
                {
                    d = 0;
                }
                else
                {
                    float lum = (0.299f * r + 0.587f * g + 0.114f * b) / 255f;
                    d = (byte)(96 + lum * 159f);
                }

                gray[i * 4] = d;
                gray[i * 4 + 1] = d;
                gray[i * 4 + 2] = d;
                gray[i * 4 + 3] = 255;
            }

            using Godot.Image dst = Godot.Image.CreateFromData(
                w, h, false, Godot.Image.Format.Rgba8, gray);
            return dst.SavePngToBuffer();
        }

        private static readonly HttpClient _http = new HttpClient(
            new HttpClientHandler { AllowAutoRedirect = false, UseProxy = false })
        {
            Timeout = TimeSpan.FromSeconds(120)
        };

        /// <summary>
        /// Image-to-splat end to end: upload the PNG, queue the frozen
        /// MultisMaker1 template with the prompt and a random seed, wait for
        /// the SplatToFile3D output, download the PLY. Progress reports go to
        /// <paramref name="report"/> (worker thread; marshal before touching UI).
        /// </summary>
        public static async Task<PlyResult> ImageToSplatAsync(
            string url, byte[] png, string prompt, Action<string> report,
            CancellationToken ct, int timeoutMinutes = 30)
        {
            string name = $"guo_gen_{DateTime.UtcNow:yyyyMMdd_HHmmss}.png";
            report?.Invoke("Uploading artwork...");
            string stored = await UploadImageAsync(url, png, name, ct);
            report?.Invoke("Queueing 3D workflow...");
            string template = LoadTemplate();
            string promptId = await QueueImageToSplatAsync(url, template, stored, prompt, ct);
            report?.Invoke("Generating 3D (minutes)...");
            var deadline = DateTime.UtcNow.AddMinutes(timeoutMinutes);
            while (true)
            {
                ct.ThrowIfCancellationRequested();
                PlyResult found = await TryFetchPlyAsync(url, promptId, ct);
                if (found != null)
                {
                    report?.Invoke($"Downloaded {found.FileName} ({found.Ply.Length / 1024} KiB).");
                    // The splat render lands right after the file write.
                    // Bounded wait: the PLY is the payload, a missing
                    // render must not fail the run.
                    report?.Invoke("Fetching splat preview...");
                    var pngDeadline = DateTime.UtcNow.AddMinutes(2);
                    while (DateTime.UtcNow <= pngDeadline)
                    {
                        ct.ThrowIfCancellationRequested();
                        PngResult preview = await TryFetchSplatPreviewAsync(url, promptId, ct);
                        if (preview != null)
                        {
                            found.PreviewPng = preview.Png;
                            found.PreviewFileName = preview.FileName;
                            break;
                        }

                        await Task.Delay(TimeSpan.FromSeconds(5), ct);
                    }

                    return found;
                }

                if (DateTime.UtcNow > deadline)
                {
                    throw new TimeoutException($"no PLY from prompt {promptId} in {timeoutMinutes} min");
                }

                await Task.Delay(TimeSpan.FromSeconds(5), ct);
            }
        }

        public static string LoadTemplate()
        {
            return LoadTemplate(TemplatePath);
        }

        public static string LoadTemplate(string path)
        {
            using Godot.FileAccess file = Godot.FileAccess.Open(path, Godot.FileAccess.ModeFlags.Read);
            if (file == null)
            {
                throw new FileNotFoundException($"ComfyUI template missing: {path}");
            }

            return file.GetAsText();
        }

        public static async Task<string> UploadImageAsync(string url, byte[] png, string name, CancellationToken ct)
        {
            using var form = new MultipartFormDataContent();
            form.Add(new ByteArrayContent(png), "image", name);
            form.Add(new StringContent("true"), "overwrite");
            using HttpResponseMessage res =
                await _http.PostAsync($"{url}/upload/image", form, ct);
            res.EnsureSuccessStatusCode();
            using JsonDocument doc = JsonDocument.Parse(await res.Content.ReadAsStringAsync(ct));
            if (doc.RootElement.TryGetProperty("name", out JsonElement n))
            {
                return n.GetString();
            }

            return name;
        }

        /// <summary>
        /// Fills the frozen MultisMaker1 template: LoadImage 132, prompt 138,
        /// seed 101. Node ids are the converter's, stable for this template.
        /// </summary>
        public static async Task<string> QueueImageToSplatAsync(
            string url, string templateJson, string imageName, string prompt, CancellationToken ct)
        {
            using JsonDocument doc = JsonDocument.Parse(templateJson);
            var nodes = new Dictionary<string, object>();
            foreach (JsonProperty node in doc.RootElement.EnumerateObject())
            {
                var inputs = new Dictionary<string, object>();
                if (node.Value.TryGetProperty("inputs", out JsonElement ins))
                {
                    foreach (JsonProperty input in ins.EnumerateObject())
                    {
                        inputs[input.Name] = ReadNodeValue(input.Value);
                    }
                }

                nodes[node.Name] = Node(
                    node.Value.GetProperty("class_type").GetString(), inputs);
            }

            SetInput(nodes, "132", "image", imageName);
            SetInput(nodes, "138", "value", prompt);
            SetInput(nodes, "101", "seed", (object)Random.Shared.Next(1, int.MaxValue));

            var body = new Dictionary<string, object>
            {
                ["prompt"] = nodes,
                ["client_id"] = "guo-client",
            };
            string json = JsonSerializer.Serialize(body);
            using var content = new StringContent(json, Encoding.UTF8, "application/json");
            using HttpResponseMessage res = await _http.PostAsync($"{url}/prompt", content, ct);
            res.EnsureSuccessStatusCode();
            using JsonDocument queued = JsonDocument.Parse(await res.Content.ReadAsStringAsync(ct));
            if (queued.RootElement.TryGetProperty("prompt_id", out JsonElement id))
            {
                return id.GetString();
            }

            throw new InvalidDataException("ComfyUI /prompt returned no prompt_id");
        }

        public static async Task<PlyResult> TryFetchPlyAsync(string url, string promptId, CancellationToken ct)
        {
            foreach (FileRef file in await TryFetchFilesAsync(url, promptId, new[] { ".ply" }, ct))
            {
                return new PlyResult
                {
                    Ply = await DownloadViewAsync(url, file, ct),
                    FileName = file.FileName,
                    Subfolder = file.Subfolder,
                    Type = file.Type,
                };
            }

            return null;
        }

        /// <summary>
        /// The workflow's render of the finished splat: node 144's
        /// PreviewGaussianSplat first, any preview image as fallback.
        /// Null until the render lands in the prompt's history.
        /// </summary>
        public static async Task<PngResult> TryFetchSplatPreviewAsync(string url, string promptId, CancellationToken ct)
        {
            using HttpResponseMessage res =
                await _http.GetAsync($"{url}/history/{promptId}", ct);
            res.EnsureSuccessStatusCode();
            using JsonDocument hist = JsonDocument.Parse(await res.Content.ReadAsStringAsync(ct));
            if (!hist.RootElement.TryGetProperty(promptId, out JsonElement entry)
                || !entry.TryGetProperty("outputs", out JsonElement outputs))
            {
                return null;
            }

            var ordered = new List<string> { "144" };
            foreach (JsonProperty node in outputs.EnumerateObject())
            {
                if (!ordered.Contains(node.Name))
                {
                    ordered.Add(node.Name);
                }
            }

            string[] extensions = new[] { ".png", ".jpg", ".jpeg", ".webp" };
            foreach (string id in ordered)
            {
                if (!outputs.TryGetProperty(id, out JsonElement nodeOut))
                {
                    continue;
                }

                var refs = new List<FileRef>();
                FindFiles(nodeOut, extensions, refs);
                foreach (FileRef file in refs)
                {
                    return new PngResult
                    {
                        Png = await DownloadViewAsync(url, file, ct),
                        FileName = file.FileName,
                    };
                }
            }

            return null;
        }

        public static async Task<List<FileRef>> TryFetchFilesAsync(
            string url, string promptId, string[] extensions, CancellationToken ct)
        {
            var found = new List<FileRef>();
            using HttpResponseMessage res =
                await _http.GetAsync($"{url}/history/{promptId}", ct);
            res.EnsureSuccessStatusCode();
            using JsonDocument hist = JsonDocument.Parse(await res.Content.ReadAsStringAsync(ct));
            if (!hist.RootElement.TryGetProperty(promptId, out JsonElement entry)
                || !entry.TryGetProperty("outputs", out JsonElement outputs))
            {
                return found;
            }

            foreach (JsonProperty node in outputs.EnumerateObject())
            {
                FindFiles(node.Value, extensions, found);
            }

            return found;
        }

        public static async Task<byte[]> DownloadViewAsync(string url, FileRef file, CancellationToken ct)
        {
            using HttpResponseMessage dl = await _http.GetAsync(
                $"{url}/view?filename={Uri.EscapeDataString(file.FileName)}"
                + $"&subfolder={Uri.EscapeDataString(file.Subfolder)}&type={Uri.EscapeDataString(file.Type)}", ct);
            dl.EnsureSuccessStatusCode();
            return await dl.Content.ReadAsByteArrayAsync(ct);
        }

        /// <summary>
        /// Classic img2img repaint (the frozen guo_repaint_api template,
        /// tools/comfy/img2img.py's graph): upload, denoise with a prompt
        /// through DreamShaper, download the first PNG. Same poll shape as
        /// the 3D flow. The auto batch calls <see cref="SubmitRepaintAsync"/>
        /// up front for every graphic and then polls each prompt id with
        /// <see cref="WaitForPromptImagesAsync"/> instead of this helper.
        /// </summary>
        public static async Task<PngResult> ImageToImageAsync(
            string url, byte[] png, string prompt, int seed, float denoise,
            Action<string> report, CancellationToken ct, int timeoutMinutes = 20,
            string sampler = "euler", string scheduler = "normal", int steps = 20, float cfg = 7f,
            byte[] depthPng = null, float depthStrength = DefaultDepthStrength,
            float depthStart = DefaultDepthStart, float depthEnd = DefaultDepthEnd,
            string depthControlModel = DepthControlModel)
        {
            if (depthPng != null)
            {
                string resolved = await ResolveDepthModelAsync(url, depthControlModel, ct);
                if (resolved == null)
                {
                    depthPng = null;
                    report?.Invoke(DepthFallbackStatus);
                }
                else
                {
                    depthControlModel = resolved;
                }
            }

            string name = $"guo_repaint_{DateTime.UtcNow:yyyyMMdd_HHmmss}.png";
            report?.Invoke("Uploading artwork...");
            string stored = await UploadImageAsync(url, png, name, ct);
            string depthStored = null;
            if (depthPng != null)
            {
                report?.Invoke("Uploading depth map...");
                depthStored = await UploadImageAsync(url, depthPng, $"guo_depth_{DateTime.UtcNow:yyyyMMdd_HHmmss}.png", ct);
            }

            report?.Invoke("Queueing repaint...");
            try
            {
                string promptId = await QueueRepaintAsync(
                    url, LoadTemplate(RepaintTemplatePath), stored, prompt, seed, denoise,
                    sampler, scheduler, steps, cfg, ct, depthStored, depthControlModel,
                    depthStrength, depthStart, depthEnd, report);
                return await WaitForPromptImagesAsync(url, promptId, "Repainting (a minute or two)...",
                    report, ct, timeoutMinutes, pollSeconds: 2, depthModelHint: depthStored != null ? depthControlModel : null);
            }
            catch (InvalidOperationException ex)
            {
                bool missing = ex.Message.StartsWith("Depth ControlNet model missing", StringComparison.Ordinal);
                bool canRetry = missing && depthStored != null && depthControlModel == DepthControlModel;
                if (!canRetry)
                {
                    throw;
                }

                report?.Invoke($"Depth model {DepthControlModel} missing, retrying with {DepthControlModelAlt}...");
                string promptId = await QueueRepaintAsync(
                    url, LoadTemplate(RepaintTemplatePath), stored, prompt, seed, denoise,
                    sampler, scheduler, steps, cfg, ct, depthStored, DepthControlModelAlt,
                    depthStrength, depthStart, depthEnd, report);
                return await WaitForPromptImagesAsync(url, promptId, "Repainting (a minute or two)...",
                    report, ct, timeoutMinutes, pollSeconds: 2, depthModelHint: DepthControlModelAlt);
            }
        }

        /// <summary>
        /// Upload + QueuePrompt without waiting: the auto batch calls this
        /// once per graphic up front so the server holds every prompt while
        /// the client polls them in parallel. Unique upload names keep
        /// concurrent items from overwriting each other server-side.
        /// </summary>
        public static async Task<string> SubmitRepaintAsync(
            string url, string templateJson, byte[] png, string prompt, int seed,
            float denoise, string sampler, string scheduler, int steps, float cfg,
            CancellationToken ct, byte[] depthPng = null,
            float depthStrength = DefaultDepthStrength,
            float depthStart = DefaultDepthStart,
            float depthEnd = DefaultDepthEnd,
            string depthControlModel = DepthControlModel,
            Action<string> report = null)
        {
            if (depthPng != null)
            {
                string resolved = await ResolveDepthModelAsync(url, depthControlModel, ct);
                if (resolved == null)
                {
                    depthPng = null;
                    report?.Invoke(DepthFallbackStatus);
                }
                else
                {
                    depthControlModel = resolved;
                }
            }

            string name = $"guo_repaint_{DateTime.UtcNow:yyyyMMdd_HHmmss_fff}_{seed:x8}.png";
            string stored = await UploadImageAsync(url, png, name, ct);
            string depthStored = null;
            if (depthPng != null)
            {
                depthStored = await UploadImageAsync(url, depthPng, $"guo_depth_{DateTime.UtcNow:yyyyMMdd_HHmmss_fff}_{seed:x8}.png", ct);
            }

            return await QueueRepaintAsync(
                url, templateJson, stored, prompt, seed, denoise,
                sampler, scheduler, steps, cfg, ct, depthStored, depthControlModel,
                depthStrength, depthStart, depthEnd, report);
        }

        /// <summary>
        /// Upload + QueuePrompt without waiting (Flux twin of
        /// <see cref="SubmitRepaintAsync"/>): same submit-all-up-front shape
        /// for the guo_flux_edit_api graph.
        /// </summary>
        public static async Task<string> SubmitFluxEditAsync(
            string url, string templateJson, byte[] png, string prompt, int seed,
            float cfg, int steps, string sampler, CancellationToken ct,
            byte[] depthPng = null, float depthStrength = DefaultDepthStrength,
            float depthStart = DefaultDepthStart, float depthEnd = DefaultDepthEnd,
            string depthControlModel = FluxDepthControlModel,
            Action<string> report = null)
        {
            if (depthPng != null)
            {
                string resolved = await ResolveDepthModelAsync(url, depthControlModel, ct);
                if (resolved == null)
                {
                    depthPng = null;
                    report?.Invoke(DepthFallbackStatus);
                }
                else
                {
                    depthControlModel = resolved;
                }
            }

            string name = $"guo_flux_{DateTime.UtcNow:yyyyMMdd_HHmmss_fff}_{seed:x8}.png";
            string stored = await UploadImageAsync(url, png, name, ct);
            string depthStored = null;
            if (depthPng != null)
            {
                depthStored = await UploadImageAsync(url, depthPng, $"guo_depth_{DateTime.UtcNow:yyyyMMdd_HHmmss_fff}_{seed:x8}.png", ct);
            }

            return await QueueFluxEditAsync(
                url, templateJson, stored, prompt, seed, cfg, steps, sampler, ct,
                depthStored, depthControlModel, depthStrength, depthStart, depthEnd, report);
        }

        /// <summary>
        /// Fills the frozen repaint template: CheckpointLoaderSimple 1,
        /// LoadImage 2, prompt 4, KSampler 6. Node ids are the converter's,
        /// stable for this template (see also QueueFluxEditAsync). The auto
        /// batch QueuePrompts one of these per graphic up front, then polls
        /// each prompt id with WaitForPromptImagesAsync.
        /// </summary>
        public static async Task<string> QueueRepaintAsync(
            string url, string templateJson, string imageName, string prompt, int seed,
            float denoise, string sampler, string scheduler, int steps, float cfg,
            CancellationToken ct, string depthImageName = null,
            string depthControlModel = DepthControlModel,
            float depthStrength = DefaultDepthStrength,
            float depthStart = DefaultDepthStart,
            float depthEnd = DefaultDepthEnd,
            Action<string> report = null)
        {
            if (!string.IsNullOrEmpty(depthImageName))
            {
                string resolved = await ResolveDepthModelAsync(url, depthControlModel, ct);
                if (resolved == null)
                {
                    depthImageName = null;
                    report?.Invoke(DepthFallbackStatus);
                }
                else
                {
                    depthControlModel = resolved;
                }
            }

            using JsonDocument doc = JsonDocument.Parse(templateJson);
            var nodes = new Dictionary<string, object>();
            foreach (JsonProperty node in doc.RootElement.EnumerateObject())
            {
                var inputs = new Dictionary<string, object>();
                if (node.Value.TryGetProperty("inputs", out JsonElement ins))
                {
                    foreach (JsonProperty input in ins.EnumerateObject())
                    {
                        inputs[input.Name] = ReadNodeValue(input.Value);
                    }
                }

                nodes[node.Name] = Node(
                    node.Value.GetProperty("class_type").GetString(), inputs);
            }

            SetInput(nodes, "1", "ckpt_name", "DreamShaper_8_pruned.safetensors");
            SetInput(nodes, "2", "image", imageName);
            SetInput(nodes, "4", "text", prompt);
            SetInput(nodes, "6", "seed", (object)seed);
            SetInput(nodes, "6", "steps", (object)steps);
            SetInput(nodes, "6", "cfg", (object)cfg);
            SetInput(nodes, "6", "sampler_name", sampler);
            SetInput(nodes, "6", "scheduler", scheduler);
            SetInput(nodes, "6", "denoise", (object)denoise);
            if (!string.IsNullOrEmpty(depthImageName))
            {
                ApplyDepthControlNet(nodes, depthImageName, depthControlModel,
                    depthStrength, depthStart, depthEnd, flux: false);
            }

            return await QueueWorkflowAsync(url, nodes, ct);
        }

        /// <summary>Poll a queued prompt until its first image downloads.</summary>
        private static Task<PngResult> WaitForImagesAsync(
            string url, string promptId, string doing,
            Action<string> report, CancellationToken ct, int timeoutMinutes)
        {
            return WaitForPromptImagesAsync(url, promptId, doing, report, ct, timeoutMinutes, pollSeconds: 2);
        }

        /// <summary>
        /// Poll one already-queued prompt id until its first image downloads.
        /// The auto batch submits every prompt up front, then awaits one of
        /// these per prompt id in parallel: results match back by prompt id,
        /// so a ~1s generation no longer pays the old serial 5s poll quantum
        /// behind every earlier item. Single generations use a 2s quantum;
        /// the batch passes 1s (ComfyUI serialises execution; polling is
        /// cheap history reads, throttled by <see cref="AutoMaxInFlight"/>).
        /// </summary>
        public static async Task<PngResult> WaitForPromptImagesAsync(
            string url, string promptId, string doing,
            Action<string> report, CancellationToken ct, int timeoutMinutes = 20,
            int pollSeconds = 1, string depthModelHint = null)
        {
            report?.Invoke(doing);
            var deadline = DateTime.UtcNow.AddMinutes(timeoutMinutes);
            int pollMs = Math.Max(500, pollSeconds * 1000);
            while (true)
            {
                ct.ThrowIfCancellationRequested();
                foreach (FileRef file in await TryFetchFilesAsync(
                    url, promptId, new[] { ".png", ".jpg", ".jpeg", ".webp" }, ct))
                {
                    byte[] bytes = await DownloadViewAsync(url, file, ct);
                    report?.Invoke($"Downloaded {file.FileName} ({bytes.Length / 1024} KiB).");
                    return new PngResult { Png = bytes, FileName = file.FileName };
                }

                if (DateTime.UtcNow > deadline)
                {
                    throw new TimeoutException($"no image from prompt {promptId} in {timeoutMinutes} min");
                }

                await Task.Delay(pollMs, ct);
            }
        }

        internal static async Task<string> GetPromptErrorAsync(string url, string promptId, CancellationToken ct)
        {
            try
            {
                using HttpResponseMessage res = await _http.GetAsync($"{url}/history/{promptId}", ct);
                res.EnsureSuccessStatusCode();
                using JsonDocument hist = JsonDocument.Parse(await res.Content.ReadAsStringAsync(ct));
                if (!hist.RootElement.TryGetProperty(promptId, out JsonElement entry))
                {
                    return null;
                }

                if (!entry.TryGetProperty("status", out JsonElement status))
                {
                    return null;
                }

                bool completed = status.TryGetProperty("completed", out JsonElement done)
                    && done.ValueKind == JsonValueKind.True;
                string str = status.TryGetProperty("status_str", out JsonElement s)
                    ? s.GetString() ?? "" : "";
                if (!completed || str != "error")
                {
                    return null;
                }

                if (status.TryGetProperty("messages", out JsonElement msgs))
                {
                    return msgs.GetRawText();
                }

                return "execution error";
            }
            catch
            {
                return null;
            }
        }

        internal static Exception ThrowForExecutionError(string serverSays, string promptId, string depthModelHint)
        {
            string low = (serverSays ?? "").ToLowerInvariant();
            bool looksControl = low.Contains("control") || low.Contains("control_net") || low.Contains("depth");
            if (looksControl && !string.IsNullOrEmpty(depthModelHint))
            {
                return new InvalidOperationException(DepthMissingMessage(depthModelHint, serverSays));
            }

            return new InvalidOperationException($"ComfyUI execution failed for {promptId}: {serverSays}");
        }

        /// <summary>
        /// Server queue depth (running + pending) for throttling the auto
        /// submit burst. Returns -1 when the endpoint is unreachable.
        /// </summary>
        public static async Task<int> GetQueuePendingAsync(string url, CancellationToken ct)
        {
            try
            {
                using HttpResponseMessage res = await _http.GetAsync($"{url}/queue", ct);
                res.EnsureSuccessStatusCode();
                using JsonDocument doc = JsonDocument.Parse(await res.Content.ReadAsStringAsync(ct));
                int n = 0;
                if (doc.RootElement.TryGetProperty("queue_running", out JsonElement running)
                    && running.ValueKind == JsonValueKind.Array)
                {
                    n += running.GetArrayLength();
                }

                if (doc.RootElement.TryGetProperty("queue_pending", out JsonElement pending)
                    && pending.ValueKind == JsonValueKind.Array)
                {
                    n += pending.GetArrayLength();
                }

                return n;
            }
            catch (OperationCanceledException)
            {
                throw;
            }
            catch
            {
                return -1;
            }
        }

        /// <summary>Best-effort POST /interrupt so Cancel stops server work too.</summary>
        public static async Task InterruptAsync(string url, CancellationToken ct)
        {
            try
            {
                using var content = new StringContent("{}", Encoding.UTF8, "application/json");
                using HttpResponseMessage res = await _http.PostAsync($"{url}/interrupt", content, ct);
            }
            catch
            {
            }
        }

        private static readonly Dictionary<string, (DateTime Expiry, HashSet<string> Models)> _controlNetCache =
            new Dictionary<string, (DateTime, HashSet<string>)>(StringComparer.OrdinalIgnoreCase);
        private static readonly object _controlNetCacheLock = new object();
        private static readonly TimeSpan ControlNetCacheTtl = TimeSpan.FromSeconds(30);

        /// <summary>
        /// ControlNet model files the server currently offers (ControlNetLoader
        /// control_net_name list from /object_info). Null when the server
        /// cannot be reached or answers an unexpected shape: callers proceed
        /// with depth and let the existing execution-error paths surface.
        /// Empty when the node is missing or lists nothing: callers skip depth
        /// injection so the queued graph stays byte-identical to depth-off
        /// instead of submitting a ControlNetLoader (node 91/201) that fails
        /// validation. Depth-off callers never call this (untouched path).
        /// </summary>
        public static async Task<HashSet<string>> GetControlNetModelsAsync(string url, CancellationToken ct)
        {
            string key = (url ?? "").TrimEnd('/');
            lock (_controlNetCacheLock)
            {
                if (_controlNetCache.TryGetValue(key, out var cached)
                    && DateTime.UtcNow < cached.Expiry && cached.Models != null)
                {
                    return new HashSet<string>(cached.Models, StringComparer.OrdinalIgnoreCase);
                }
            }

            HashSet<string> result = await QueryControlNetModelsAsync(key, ct);
            if (result != null)
            {
                lock (_controlNetCacheLock)
                {
                    _controlNetCache[key] = (DateTime.UtcNow + ControlNetCacheTtl,
                        new HashSet<string>(result, StringComparer.OrdinalIgnoreCase));
                }

                return new HashSet<string>(result, StringComparer.OrdinalIgnoreCase);
            }

            return null;
        }

        private static async Task<HashSet<string>> QueryControlNetModelsAsync(string url, CancellationToken ct)
        {
            try
            {
                using HttpResponseMessage res = await _http.GetAsync($"{url}/object_info/ControlNetLoader", ct);
                if (res.IsSuccessStatusCode)
                {
                    using JsonDocument doc = JsonDocument.Parse(await res.Content.ReadAsStringAsync(ct));
                    if (TryParseControlNetList(doc.RootElement, out HashSet<string> models, true))
                    {
                        return models;
                    }
                }
            }
            catch (OperationCanceledException)
            {
                throw;
            }
            catch
            {
            }

            try
            {
                using HttpResponseMessage res2 = await _http.GetAsync($"{url}/object_info", ct);
                res2.EnsureSuccessStatusCode();
                using JsonDocument doc2 = JsonDocument.Parse(await res2.Content.ReadAsStringAsync(ct));
                if (TryParseControlNetList(doc2.RootElement, out HashSet<string> models2, false))
                {
                    return models2;
                }

                return null;
            }
            catch (OperationCanceledException)
            {
                throw;
            }
            catch
            {
                return null;
            }
        }

        private static bool TryParseControlNetList(JsonElement root, out HashSet<string> models, bool isSingleNodeEndpoint)
        {
            models = null;
            try
            {
                if (root.ValueKind != JsonValueKind.Object)
                {
                    return false;
                }

                JsonElement loader;
                if (root.TryGetProperty("ControlNetLoader", out JsonElement wrapped)
                    && wrapped.ValueKind == JsonValueKind.Object)
                {
                    loader = wrapped;
                }
                else if (root.TryGetProperty("input", out _))
                {
                    loader = root;
                }
                else
                {
                    if (!isSingleNodeEndpoint)
                    {
                        models = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
                        return true;
                    }

                    return false;
                }

                if (!loader.TryGetProperty("input", out JsonElement input)
                    || input.ValueKind != JsonValueKind.Object)
                {
                    models = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
                    return true;
                }

                var set = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
                foreach (string section in new[] { "required", "optional" })
                {
                    if (input.TryGetProperty(section, out JsonElement sec)
                        && sec.ValueKind == JsonValueKind.Object
                        && sec.TryGetProperty("control_net_name", out JsonElement ctrl)
                        && ctrl.ValueKind == JsonValueKind.Array
                        && ctrl.GetArrayLength() > 0)
                    {
                        JsonElement list = ctrl[0];
                        if (list.ValueKind == JsonValueKind.Array)
                        {
                            foreach (JsonElement m in list.EnumerateArray())
                            {
                                if (m.ValueKind == JsonValueKind.String)
                                {
                                    string name = m.GetString();
                                    if (!string.IsNullOrEmpty(name))
                                    {
                                        set.Add(name);
                                    }
                                }
                            }
                        }
                    }
                }

                models = set;
                return true;
            }
            catch
            {
                return false;
            }
        }

        /// <summary>
        /// Which depth model to queue, or null when depth must be skipped.
        /// Present returns the request; the DreamShaper .pth falls back to its
        /// .safetensors twin when only that is installed; otherwise (empty
        /// list, node missing) null so callers run the byte-identical
        /// depth-off graph. Unknown (unreachable server) returns the request
        /// so existing error paths still surface.
        /// </summary>
        public static async Task<string> ResolveDepthModelAsync(string url, string requested, CancellationToken ct)
        {
            if (string.IsNullOrEmpty(requested))
            {
                return null;
            }

            HashSet<string> available;
            try
            {
                available = await GetControlNetModelsAsync(url, ct);
            }
            catch (OperationCanceledException)
            {
                throw;
            }
            catch
            {
                return requested;
            }

            if (available == null)
            {
                return requested;
            }

            if (available.Contains(requested))
            {
                return requested;
            }

            if (requested.Equals(DepthControlModel, StringComparison.OrdinalIgnoreCase)
                && available.Contains(DepthControlModelAlt))
            {
                return DepthControlModelAlt;
            }

            return null;
        }

        /// <summary>
        /// Flux.2 Klein img2img repaint (the frozen guo_flux_edit_api
        /// template): upload, edit with a prompt, download the first PNG.
        /// Change amount is cfg + steps + wording (the graph has no denoise);
        /// the sampler comes from its KSamplerSelect node. The auto batch
        /// calls <see cref="SubmitFluxEditAsync"/> up front and polls with
        /// <see cref="WaitForPromptImagesAsync"/> instead of this helper.
        /// </summary>
        public static async Task<PngResult> ImageToImageFluxAsync(
            string url, byte[] png, string prompt, int seed, float cfg, int steps, string sampler,
            Action<string> report, CancellationToken ct, int timeoutMinutes = 20,
            byte[] depthPng = null, float depthStrength = DefaultDepthStrength,
            float depthStart = DefaultDepthStart, float depthEnd = DefaultDepthEnd,
            string depthControlModel = FluxDepthControlModel)
        {
            if (depthPng != null)
            {
                string resolved = await ResolveDepthModelAsync(url, depthControlModel, ct);
                if (resolved == null)
                {
                    depthPng = null;
                    report?.Invoke(DepthFallbackStatus);
                }
                else
                {
                    depthControlModel = resolved;
                }
            }

            string name = $"guo_flux_{DateTime.UtcNow:yyyyMMdd_HHmmss}.png";
            report?.Invoke("Uploading artwork...");
            string stored = await UploadImageAsync(url, png, name, ct);
            string depthStored = null;
            if (depthPng != null)
            {
                report?.Invoke("Uploading depth map...");
                depthStored = await UploadImageAsync(url, depthPng, $"guo_depth_{DateTime.UtcNow:yyyyMMdd_HHmmss}.png", ct);
            }

            report?.Invoke("Queueing Flux repaint...");
            string promptId = await QueueFluxEditAsync(
                url, LoadTemplate(FluxTemplatePath), stored, prompt, seed, cfg, steps, sampler, ct,
                depthStored, depthControlModel, depthStrength, depthStart, depthEnd, report);
            return await WaitForPromptImagesAsync(url, promptId, "Repainting with Flux (minutes)...",
                report, ct, timeoutMinutes, pollSeconds: 2,
                depthModelHint: depthStored != null ? depthControlModel : null);
        }

        /// <summary>
        /// Fills the frozen Flux edit template: LoadImage 76, prompt 163,
        /// seed on RandomNoise 160, CFGGuider 157, Flux2Scheduler 172,
        /// KSamplerSelect 156. Node ids are the converter's, stable for this
        /// template (see also the editor's ComfyUiProvider.Bind). The auto
        /// batch QueuePrompts one of these per graphic up front, then polls
        /// each prompt id with WaitForPromptImagesAsync.
        /// </summary>
        public static async Task<string> QueueFluxEditAsync(
            string url, string templateJson, string imageName, string prompt, int seed,
            float cfg, int steps, string sampler, CancellationToken ct,
            string depthImageName = null, string depthControlModel = FluxDepthControlModel,
            float depthStrength = DefaultDepthStrength,
            float depthStart = DefaultDepthStart,
            float depthEnd = DefaultDepthEnd,
            Action<string> report = null)
        {
            if (!string.IsNullOrEmpty(depthImageName))
            {
                string resolved = await ResolveDepthModelAsync(url, depthControlModel, ct);
                if (resolved == null)
                {
                    depthImageName = null;
                    report?.Invoke(DepthFallbackStatus);
                }
                else
                {
                    depthControlModel = resolved;
                }
            }

            using JsonDocument doc = JsonDocument.Parse(templateJson);
            var nodes = new Dictionary<string, object>();
            foreach (JsonProperty node in doc.RootElement.EnumerateObject())
            {
                var inputs = new Dictionary<string, object>();
                if (node.Value.TryGetProperty("inputs", out JsonElement ins))
                {
                    foreach (JsonProperty input in ins.EnumerateObject())
                    {
                        inputs[input.Name] = ReadNodeValue(input.Value);
                    }
                }

                nodes[node.Name] = Node(
                    node.Value.GetProperty("class_type").GetString(), inputs);
            }

            SetInput(nodes, "76", "image", imageName);
            SetInput(nodes, "163", "text", prompt);
            SetInput(nodes, "160", "noise_seed", (object)(long)seed);
            SetInput(nodes, "157", "cfg", (object)cfg);
            SetInput(nodes, "172", "steps", (object)steps);
            SetInput(nodes, "156", "sampler_name", sampler);
            if (!string.IsNullOrEmpty(depthImageName))
            {
                ApplyDepthControlNet(nodes, depthImageName, depthControlModel,
                    depthStrength, depthStart, depthEnd, flux: true);
            }

            return await QueueWorkflowAsync(url, nodes, ct);
        }

        /// <summary>
        /// Inpainting repaint (the frozen guo_inpaint_api template,
        /// DreamShaper + SetLatentNoiseMask, model agnostic): the mask image
        /// says what to regrow, the rest stays. Mask convention follows the
        /// node: white regrows.
        /// </summary>
        public static async Task<PngResult> ImageInpaintAsync(
            string url, byte[] png, byte[] maskPng, string prompt, string negative,
            string checkpoint, int seed, int steps, float cfg, float denoise,
            Action<string> report, CancellationToken ct, int timeoutMinutes = 30)
        {
            report?.Invoke("Uploading artwork...");
            string stored = await UploadImageAsync(url, png, $"guo_inpaint_{DateTime.UtcNow:yyyyMMdd_HHmmss}.png", ct);
            report?.Invoke("Uploading mask...");
            string maskStored = await UploadImageAsync(url, maskPng, $"guo_mask_{DateTime.UtcNow:yyyyMMdd_HHmmss}.png", ct);
            report?.Invoke("Queueing inpaint...");
            string promptId = await QueueInpaintAsync(
                url, LoadTemplate(InpaintTemplatePath), stored, maskStored, prompt, negative,
                checkpoint, seed, steps, cfg, denoise, ct);
            report?.Invoke("Inpainting (a minute or two)...");
            var deadline = DateTime.UtcNow.AddMinutes(timeoutMinutes);
            while (true)
            {
                ct.ThrowIfCancellationRequested();
                foreach (FileRef file in await TryFetchFilesAsync(
                    url, promptId, new[] { ".png", ".jpg", ".jpeg", ".webp" }, ct))
                {
                    byte[] bytes = await DownloadViewAsync(url, file, ct);
                    report?.Invoke($"Downloaded {file.FileName} ({bytes.Length / 1024} KiB).");
                    return new PngResult { Png = bytes, FileName = file.FileName };
                }

                if (DateTime.UtcNow > deadline)
                {
                    throw new TimeoutException($"no image from prompt {promptId} in {timeoutMinutes} min");
                }

                await Task.Delay(TimeSpan.FromSeconds(5), ct);
            }
        }

        /// <summary>
        /// Fills the frozen inpaint template: CheckpointLoaderSimple 1,
        /// LoadImage 2 (art) and 3 (mask), prompts 7-8, KSampler 9. The
        /// checkpoint stays a parameter; the sampler pair stays the
        /// DreamShaper euler/normal literals, set here rather than baked
        /// into the file. Node ids are the converter's, stable for this
        /// template (see also QueueFluxEditAsync).
        /// </summary>
        public static async Task<string> QueueInpaintAsync(
            string url, string templateJson, string imageName, string maskName, string prompt,
            string negative, string checkpoint, int seed, int steps, float cfg, float denoise,
            CancellationToken ct)
        {
            using JsonDocument doc = JsonDocument.Parse(templateJson);
            var nodes = new Dictionary<string, object>();
            foreach (JsonProperty node in doc.RootElement.EnumerateObject())
            {
                var inputs = new Dictionary<string, object>();
                if (node.Value.TryGetProperty("inputs", out JsonElement ins))
                {
                    foreach (JsonProperty input in ins.EnumerateObject())
                    {
                        inputs[input.Name] = ReadNodeValue(input.Value);
                    }
                }

                nodes[node.Name] = Node(
                    node.Value.GetProperty("class_type").GetString(), inputs);
            }

            SetInput(nodes, "1", "ckpt_name", checkpoint);
            SetInput(nodes, "2", "image", imageName);
            SetInput(nodes, "3", "image", maskName);
            SetInput(nodes, "7", "text", prompt);
            SetInput(nodes, "8", "text", negative);
            SetInput(nodes, "9", "seed", (object)seed);
            SetInput(nodes, "9", "steps", (object)steps);
            SetInput(nodes, "9", "cfg", (object)cfg);
            SetInput(nodes, "9", "sampler_name", "euler");
            SetInput(nodes, "9", "scheduler", "normal");
            SetInput(nodes, "9", "denoise", (object)denoise);
            return await QueueWorkflowAsync(url, nodes, ct);
        }

        private static Dictionary<string, object> Node(string classType, Dictionary<string, object> inputs)
        {
            return new Dictionary<string, object>
            {
                ["class_type"] = classType,
                ["inputs"] = inputs,
            };
        }

        private static async Task<string> QueueWorkflowAsync(
            string url, Dictionary<string, object> workflow, CancellationToken ct)
        {
            var body = new Dictionary<string, object>
            {
                ["prompt"] = workflow,
                ["client_id"] = "guo-client",
            };
            string json = JsonSerializer.Serialize(body);
            using var content = new StringContent(json, Encoding.UTF8, "application/json");
            using HttpResponseMessage res = await _http.PostAsync($"{url}/prompt", content, ct);
            res.EnsureSuccessStatusCode();
            using JsonDocument queued = JsonDocument.Parse(await res.Content.ReadAsStringAsync(ct));
            if (queued.RootElement.TryGetProperty("prompt_id", out JsonElement id))
            {
                return id.GetString();
            }

            throw new InvalidDataException("ComfyUI /prompt returned no prompt_id");
        }

        /// <summary>
        /// File refs come as {filename, subfolder, type} dicts (Save nodes)
        /// or as preview strings like "preview_splat_abc.ply [temp]"
        /// (PreviewGaussianSplat, the deterministic local copy of a
        /// SplatToFile3D result, which reports nothing itself).
        /// </summary>
        private static void FindFiles(JsonElement node, string[] extensions, List<FileRef> found)
        {
            foreach (JsonProperty prop in node.EnumerateObject())
            {
                if (prop.Value.ValueKind == JsonValueKind.Array)
                {
                    foreach (JsonElement item in prop.Value.EnumerateArray())
                    {
                        if (item.ValueKind == JsonValueKind.Object
                            && item.TryGetProperty("filename", out JsonElement fn))
                        {
                            string name = fn.GetString() ?? "";
                            if (!HasExtension(name, extensions))
                            {
                                continue;
                            }

                            string sub = item.TryGetProperty("subfolder", out JsonElement s) ? s.GetString() ?? "" : "";
                            string typ = item.TryGetProperty("type", out JsonElement t) ? t.GetString() ?? "output" : "output";
                            found.Add(new FileRef { FileName = name, Subfolder = sub, Type = typ });
                        }
                        else if (item.ValueKind == JsonValueKind.String)
                        {
                            FileRef parsed = ParseFileString(item.GetString(), extensions);
                            if (parsed != null)
                            {
                                found.Add(parsed);
                            }
                        }
                    }
                }
                else if (prop.Value.ValueKind == JsonValueKind.Object)
                {
                    FindFiles(prop.Value, extensions, found);
                }
                else if (prop.Value.ValueKind == JsonValueKind.String)
                {
                    FileRef parsed = ParseFileString(prop.Value.GetString(), extensions);
                    if (parsed != null)
                    {
                        found.Add(parsed);
                    }
                }
            }
        }

        private static bool HasExtension(string name, string[] extensions)
        {
            foreach (string ext in extensions)
            {
                if (name.EndsWith(ext, StringComparison.OrdinalIgnoreCase))
                {
                    return true;
                }
            }

            return false;
        }

        private static FileRef ParseFileString(string text, string[] extensions)
        {
            if (string.IsNullOrEmpty(text))
            {
                return null;
            }

            // "preview_splat_<hash>.ply [temp]" (or [output]/[input]).
            // The bracket is required: bare strings (prompts, log lines)
            // must never read as files.
            string name = text.Trim();
            int bracket = name.LastIndexOf('[');
            if (bracket < 0 || !name.EndsWith("]"))
            {
                return null;
            }

            string type = name.Substring(bracket + 1, name.Length - bracket - 2).Trim();
            name = name.Substring(0, bracket).Trim();

            if (!HasExtension(name, extensions) || name.IndexOfAny(new[] { '/', '\\' }) >= 0)
            {
                return null;
            }

            return new FileRef { FileName = name, Subfolder = "", Type = type };
        }

        private static void ValidateDepth(float strength, float start, float end)
        {
            if (strength < 0f || strength > 1f)
            {
                throw new ArgumentException("depth strength must be 0-1");
            }

            if (start < 0f || start > 1f || end < 0f || end > 1f || start > end)
            {
                throw new ArgumentException("depth start/end must be 0-1 with start <= end");
            }
        }

        /// <summary>
        /// Depth ControlNet injection (node 92 on DreamShaper, 202 on Flux).
        /// When off the caller never calls this, so the queued graph stays
        /// byte-identical to the frozen template.
        /// </summary>
        private static void ApplyDepthControlNet(Dictionary<string, object> nodes, string depthImageName, string controlModel, float strength, float start, float end, bool flux)
        {
            ValidateDepth(strength, start, end);
            if (flux)
            {
                nodes["200"] = Node("LoadImage", new Dictionary<string, object> { ["image"] = depthImageName });
                nodes["201"] = Node("ControlNetLoader", new Dictionary<string, object> { ["control_net_name"] = controlModel });
                nodes["202"] = Node("ControlNetApplyAdvanced", new Dictionary<string, object>
                {
                    ["positive"] = new List<object> { "163", (object)(long)0 },
                    ["negative"] = new List<object> { "164", (object)(long)0 },
                    ["control_net"] = new List<object> { "201", (object)(long)0 },
                    ["image"] = new List<object> { "200", (object)(long)0 },
                    ["strength"] = (object)strength,
                    ["start_percent"] = (object)start,
                    ["end_percent"] = (object)end,
                    ["vae"] = new List<object> { "165", (object)(long)0 },
                });
                SetLink(nodes, "170", "conditioning", "202", 0);
                SetLink(nodes, "168", "conditioning", "202", 1);
            }
            else
            {
                nodes["90"] = Node("LoadImage", new Dictionary<string, object> { ["image"] = depthImageName });
                nodes["91"] = Node("ControlNetLoader", new Dictionary<string, object> { ["control_net_name"] = controlModel });
                nodes["92"] = Node("ControlNetApplyAdvanced", new Dictionary<string, object>
                {
                    ["positive"] = new List<object> { "4", (object)(long)0 },
                    ["negative"] = new List<object> { "5", (object)(long)0 },
                    ["control_net"] = new List<object> { "91", (object)(long)0 },
                    ["image"] = new List<object> { "90", (object)(long)0 },
                    ["strength"] = (object)strength,
                    ["start_percent"] = (object)start,
                    ["end_percent"] = (object)end,
                });
                SetLink(nodes, "6", "positive", "92", 0);
                SetLink(nodes, "6", "negative", "92", 1);
            }
        }

        internal static string DepthMissingMessage(string model, string serverSays)
        {
            string names = model == FluxDepthControlModel
                ? model
                : $"{DepthControlModel} (or {DepthControlModelAlt})";
            return $"Depth ControlNet model missing: put {names} in ComfyUI/models/controlnet/ and retry (or untick Depth ControlNet). Server said: {serverSays}";
        }

        private static void SetInput(Dictionary<string, object> nodes, string id, string key, object value)
        {
            if (nodes.TryGetValue(id, out object node)
                && node is Dictionary<string, object> map
                && map.TryGetValue("inputs", out object ins)
                && ins is Dictionary<string, object> inputs)
            {
                inputs[key] = value;
            }
        }

        private static void SetLink(Dictionary<string, object> nodes, string id, string key, string fromId, int fromSlot)
        {
            SetInput(nodes, id, key, (object)new List<object> { fromId, (object)(long)fromSlot });
        }

        private static object ReadNodeValue(JsonElement value)
        {
            switch (value.ValueKind)
            {
                case JsonValueKind.String:
                    return value.GetString();
                case JsonValueKind.Number:
                    if (value.TryGetInt64(out long l))
                    {
                        return l;
                    }

                    return value.GetDouble();
                case JsonValueKind.True:
                    return true;
                case JsonValueKind.False:
                    return false;
                case JsonValueKind.Array:
                    {
                        var list = new List<object>();
                        foreach (JsonElement item in value.EnumerateArray())
                        {
                            list.Add(ReadNodeValue(item));
                        }

                        return list;
                    }
                default:
                    return value.GetRawText();
            }
        }
    }
}
