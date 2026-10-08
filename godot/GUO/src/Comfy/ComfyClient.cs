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
                    url, promptId, new[] { ".mp3", ".wav", ".flac", ".ogg" }, ct))
                {
                    byte[] bytes = await DownloadViewAsync(url, file, ct);
                    report?.Invoke($"Downloaded {file.FileName} ({bytes.Length / 1024} KiB).");
                    return new AudioResult { Audio = bytes, FileName = file.FileName };
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
        }

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
            int x0 = art.Width, x1 = -1;
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
                    int x = i % art.Width;
                    if (x < x0)
                    {
                        x0 = x;
                    }

                    if (x > x1)
                    {
                        x1 = x;
                    }
                }
            }

            using Godot.Image image = Godot.Image.CreateFromData(
                art.Width, art.Height, false, Godot.Image.Format.Rgba8, rgba);
            return new ArtShot
            {
                Png = image.SavePngToBuffer(),
                Rgba = rgba,
                Width = art.Width,
                Height = art.Height,
                TightWidth = x1 >= x0 ? x1 - x0 + 1 : 0,
            };
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
        /// Classic img2img repaint (tools/comfy/img2img.py's graph, built
        /// inline): upload, denoise with a prompt through DreamShaper,
        /// download the first PNG. Same poll shape as the 3D flow.
        /// </summary>
        public static async Task<PngResult> ImageToImageAsync(
            string url, byte[] png, string prompt, int seed, float denoise,
            Action<string> report, CancellationToken ct, int timeoutMinutes = 20)
        {
            string name = $"guo_repaint_{DateTime.UtcNow:yyyyMMdd_HHmmss}.png";
            report?.Invoke("Uploading artwork...");
            string stored = await UploadImageAsync(url, png, name, ct);
            report?.Invoke("Queueing repaint...");
            var workflow = new Dictionary<string, object>
            {
                ["1"] = Node("CheckpointLoaderSimple", new Dictionary<string, object>
                {
                    ["ckpt_name"] = "DreamShaper_8_pruned.safetensors",
                }),
                ["2"] = Node("LoadImage", new Dictionary<string, object>
                {
                    ["image"] = stored,
                }),
                ["3"] = Node("VAEEncode", new Dictionary<string, object>
                {
                    ["pixels"] = new List<object> { "2", 0 },
                    ["vae"] = new List<object> { "1", 2 },
                }),
                ["4"] = Node("CLIPTextEncode", new Dictionary<string, object>
                {
                    ["text"] = prompt,
                    ["clip"] = new List<object> { "1", 1 },
                }),
                ["5"] = Node("CLIPTextEncode", new Dictionary<string, object>
                {
                    ["text"] = "blurry, watermark, text, deformed",
                    ["clip"] = new List<object> { "1", 1 },
                }),
                ["6"] = Node("KSampler", new Dictionary<string, object>
                {
                    ["model"] = new List<object> { "1", 0 },
                    ["positive"] = new List<object> { "4", 0 },
                    ["negative"] = new List<object> { "5", 0 },
                    ["latent_image"] = new List<object> { "3", 0 },
                    ["seed"] = seed,
                    ["steps"] = 20,
                    ["cfg"] = 7.0,
                    ["sampler_name"] = "euler",
                    ["scheduler"] = "normal",
                    ["denoise"] = denoise,
                }),
                ["7"] = Node("VAEDecode", new Dictionary<string, object>
                {
                    ["samples"] = new List<object> { "6", 0 },
                    ["vae"] = new List<object> { "1", 2 },
                }),
                ["8"] = Node("SaveImage", new Dictionary<string, object>
                {
                    ["images"] = new List<object> { "7", 0 },
                    ["filename_prefix"] = "GUO_repaint",
                }),
            };
            string promptId = await QueueWorkflowAsync(url, workflow, ct);
            report?.Invoke("Repainting (a minute or two)...");
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
        /// Inpainting repaint (DreamShaper + SetLatentNoiseMask, model
        /// agnostic): the mask image says what to regrow, the rest stays.
        /// Mask convention follows the node: white regrows.
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
            var workflow = new Dictionary<string, object>
            {
                ["1"] = Node("CheckpointLoaderSimple", new Dictionary<string, object>
                {
                    ["ckpt_name"] = checkpoint,
                }),
                ["2"] = Node("LoadImage", new Dictionary<string, object>
                {
                    ["image"] = stored,
                }),
                ["3"] = Node("LoadImage", new Dictionary<string, object>
                {
                    ["image"] = maskStored,
                }),
                ["4"] = Node("ImageToMask", new Dictionary<string, object>
                {
                    ["image"] = new List<object> { "3", 0 },
                    ["channel"] = "red",
                }),
                ["5"] = Node("VAEEncode", new Dictionary<string, object>
                {
                    ["pixels"] = new List<object> { "2", 0 },
                    ["vae"] = new List<object> { "1", 2 },
                }),
                ["6"] = Node("SetLatentNoiseMask", new Dictionary<string, object>
                {
                    ["samples"] = new List<object> { "5", 0 },
                    ["mask"] = new List<object> { "4", 0 },
                }),
                ["7"] = Node("CLIPTextEncode", new Dictionary<string, object>
                {
                    ["text"] = prompt,
                    ["clip"] = new List<object> { "1", 1 },
                }),
                ["8"] = Node("CLIPTextEncode", new Dictionary<string, object>
                {
                    ["text"] = negative,
                    ["clip"] = new List<object> { "1", 1 },
                }),
                ["9"] = Node("KSampler", new Dictionary<string, object>
                {
                    ["model"] = new List<object> { "1", 0 },
                    ["positive"] = new List<object> { "7", 0 },
                    ["negative"] = new List<object> { "8", 0 },
                    ["latent_image"] = new List<object> { "6", 0 },
                    ["seed"] = seed,
                    ["steps"] = steps,
                    ["cfg"] = cfg,
                    ["sampler_name"] = "euler",
                    ["scheduler"] = "normal",
                    ["denoise"] = denoise,
                }),
                ["10"] = Node("VAEDecode", new Dictionary<string, object>
                {
                    ["samples"] = new List<object> { "9", 0 },
                    ["vae"] = new List<object> { "1", 2 },
                }),
                ["11"] = Node("SaveImage", new Dictionary<string, object>
                {
                    ["images"] = new List<object> { "10", 0 },
                    ["filename_prefix"] = "GUO_inpaint",
                }),
            };
            string promptId = await QueueWorkflowAsync(url, workflow, ct);
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
