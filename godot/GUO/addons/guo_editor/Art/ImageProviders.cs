#if TOOLS
namespace GUO.Editor;

using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Net.Http;
using System.Net.WebSockets;
using System.Text;
using System.Text.Json.Nodes;
using System.Threading;
using System.Threading.Tasks;

/// <summary>What an image service is asked for. <see cref="InputPng"/> is an asset bound as the input (img2img), or null.</summary>
public sealed class ImageRequest
{
    public string Prompt = "";
    public byte[] InputPng;

    /// <summary>The input's own provenance string, e.g. "client:static:0x0E75"; empty when there is no input.</summary>
    public string InputName = "";
    public bool InputIsClientArt;
    public string WorkflowPath = "";
    public long Seed = -1;
    public int Width;
    public int Height;
}

/// <summary>What kind of file an artifact is, by extension (GUO audio/3D jobs).</summary>
public enum ArtifactKind
{
    Image,
    Audio,
    Model,
}

/// <summary>One non-image file a workflow produced: audio (mp3/wav) or 3D (ply/glb/obj/fbx).</summary>
public sealed class ArtifactFile
{
    public string FileName = "";
    public byte[] Bytes = Array.Empty<byte>();
    public ArtifactKind Kind;

    /// <summary>Classifies by extension; unknown extensions come back as images only when they decode.</summary>
    public static ArtifactKind Classify(string fileName)
    {
        string ext = Path.GetExtension(fileName ?? "").ToLowerInvariant();
        return ext switch
        {
            ".mp3" or ".wav" or ".ogg" or ".flac" => ArtifactKind.Audio,
            ".ply" or ".glb" or ".gltf" or ".obj" or ".fbx" or ".stl" or ".splat" or ".spz" => ArtifactKind.Model,
            _ => ArtifactKind.Image,
        };
    }
}

/// <summary>What a service gave back: PNG files and how they were made (for the provenance record).</summary>
public sealed class ImageResult
{
    public List<byte[]> Pngs = new();

    /// <summary>Every file ref in the run's outputs: images (also in <see cref="Pngs"/>), audio, 3D.</summary>
    public List<ArtifactFile> Files = new();
    public string Model = "";
    public string Workflow = "";
    public long Seed = -1;
    public string Error;
}

/// <summary>
/// An image service (ADR-0029): ComfyUI, Retro Diffusion, others later. Implementations touch no Godot
/// type, because they run on worker threads; the Art dock marshals progress and results.
/// </summary>
public interface IImageProvider
{
    string Id { get; }
    string Name { get; }

    /// <summary>True for a service that needs a key from the operating system's store.</summary>
    bool NeedsKey { get; }

    Task<ImageResult> RunAsync(ImageRequest request, Action<double> progress, CancellationToken ct);
}

/// <summary>
/// ComfyUI over its HTTP API: the workflow (API-format JSON) is bound to the request, queued with
/// <c>POST /prompt</c>, followed on the websocket for progress, and its images fetched from <c>/view</c>.
/// </summary>
public sealed class ComfyUiProvider : IImageProvider, IDisposable
{
    private readonly HttpClient _http = new() { Timeout = TimeSpan.FromSeconds(30) };

    public string BaseUrl { get; set; }
    public TimeSpan RunTimeout { get; set; } = TimeSpan.FromMinutes(10);

    public string Id => "comfyui";
    public string Name => "ComfyUI";
    public bool NeedsKey => false;

    public ComfyUiProvider(string baseUrl)
    {
        BaseUrl = baseUrl.TrimEnd('/');
    }

    public void Dispose() => _http.Dispose();

    /// <summary>The API-format workflow files in a folder: what the picker lists.</summary>
    public static List<string> Workflows(string folder) =>
        Directory.Exists(folder) ? Directory.GetFiles(folder, "*.json").OrderBy(f => f, StringComparer.OrdinalIgnoreCase).ToList() : new List<string>();

    /// <summary>
    /// Binds a request into an API-format workflow. Three ways, in order: <c>{{prompt}}</c>, <c>{{input}}</c> and
    /// <c>{{seed}}</c> placeholders in any string input; the positive CLIPTextEncode a sampler points at; a
    /// LoadImage node for the input; the samplers' seeds; an EmptyLatentImage's size.
    /// Returns what was bound, for the log.
    /// </summary>
    public static List<string> Bind(JsonObject workflow, ImageRequest req, string uploadedName)
    {
        var bound = new List<string>();
        bool promptDone = false, inputDone = false;
        foreach (var (_, node) in workflow.ToList())
        {
            if (node?["inputs"] is not JsonObject inputs)
            {
                continue;
            }

            foreach (var (key, value) in inputs.ToList())
            {
                if (value is JsonValue v && v.TryGetValue(out string s))
                {
                    string t = s.Replace("{{prompt}}", req.Prompt).Replace("{{input}}", uploadedName ?? "").Replace("{{seed}}", req.Seed.ToString());
                    if (t != s)
                    {
                        inputs[key] = t;
                        bound.Add($"placeholder in {key}");
                        promptDone |= s.Contains("{{prompt}}");
                        inputDone |= s.Contains("{{input}}");
                    }
                }
            }
        }

        foreach (var (id, node) in workflow.ToList())
        {
            string cls = (string)node?["class_type"] ?? "";
            if (node?["inputs"] is not JsonObject inputs)
            {
                continue;
            }

            if (cls.StartsWith("KSampler"))
            {
                string seedKey = inputs.ContainsKey("noise_seed") ? "noise_seed" : "seed";
                if (req.Seed >= 0 && inputs.ContainsKey(seedKey) && inputs[seedKey] is not JsonArray)
                {
                    inputs[seedKey] = req.Seed;
                    bound.Add($"seed on node {id}");
                }

                if (!promptDone && req.Prompt.Length > 0 && inputs["positive"] is JsonArray link && link.Count > 0
                    && workflow[(string)link[0]]?["inputs"] is JsonObject enc && enc.ContainsKey("text"))
                {
                    enc["text"] = req.Prompt;
                    promptDone = true;
                    bound.Add($"prompt on node {(string)link[0]}");
                }
            }
            else if (cls == "LoadImage" && uploadedName != null && !inputDone)
            {
                inputs["image"] = uploadedName;
                inputDone = true;
                bound.Add($"input image on node {id}");
            }
            else if (cls == "EmptyLatentImage" && req.Width > 0 && req.Height > 0)
            {
                inputs["width"] = req.Width;
                inputs["height"] = req.Height;
                bound.Add($"size on node {id}");
            }
        }

        return bound;
    }

    public async Task<ImageResult> RunAsync(ImageRequest req, Action<double> progress, CancellationToken ct)
    {
        var result = new ImageResult { Workflow = Path.GetFileName(req.WorkflowPath), Seed = req.Seed };
        try
        {
            JsonObject workflow = JsonNode.Parse(await File.ReadAllTextAsync(req.WorkflowPath, ct).ConfigureAwait(false)) as JsonObject;
            if (workflow == null || workflow.Any(kv => kv.Value?["class_type"] == null))
            {
                result.Error = "that file is not an API-format ComfyUI workflow (use 'Save (API Format)' in ComfyUI)";
                return result;
            }

            if (req.Seed < 0)
            {
                req.Seed = Random.Shared.NextInt64(0, 1L << 40);
                result.Seed = req.Seed;
            }

            string uploaded = null;
            if (req.InputPng != null)
            {
                uploaded = await UploadAsync(req.InputName.Replace(':', '_') + ".png", req.InputPng, ct).ConfigureAwait(false);
            }

            Bind(workflow, req, uploaded);
            string clientId = Guid.NewGuid().ToString("N");
            using var ws = new ClientWebSocket();
            Task listener = Task.CompletedTask;
            try
            {
                await ws.ConnectAsync(new Uri(BaseUrl.Replace("http://", "ws://").Replace("https://", "wss://") + "/ws?clientId=" + clientId), ct).ConfigureAwait(false);
                listener = Task.Run(() => ListenAsync(ws, progress, ct), ct);
            }
            catch (Exception)
            {
                // Progress is a nicety; the history poll below finishes the job without it.
            }

            var body = new JsonObject { ["prompt"] = workflow, ["client_id"] = clientId };
            using HttpResponseMessage queued = await _http.PostAsync($"{BaseUrl}/prompt", new StringContent(body.ToJsonString(), Encoding.UTF8, "application/json"), ct).ConfigureAwait(false);
            string text = await queued.Content.ReadAsStringAsync(ct).ConfigureAwait(false);
            if (!queued.IsSuccessStatusCode)
            {
                result.Error = $"ComfyUI refused the workflow ({(int)queued.StatusCode}): {Trim(text)}";
                return result;
            }

            string promptId = (string)JsonNode.Parse(text)?["prompt_id"];
            DateTime until = DateTime.UtcNow + RunTimeout;
            JsonNode outputs = null;
            while (DateTime.UtcNow < until)
            {
                ct.ThrowIfCancellationRequested();
                string hist = await _http.GetStringAsync($"{BaseUrl}/history/{promptId}", ct).ConfigureAwait(false);
                if (JsonNode.Parse(hist)?[promptId] is JsonNode entry && entry["outputs"] is JsonObject o && o.Count > 0)
                {
                    outputs = o;
                    break;
                }

                await Task.Delay(250, ct).ConfigureAwait(false);
            }

            if (outputs == null)
            {
                result.Error = "ComfyUI did not finish in time";
                return result;
            }

            // Every file ref in every output, whatever the node type: images land in
            // Pngs (as before); audio (mp3/wav) and 3D (ply/glb/obj/fbx) land in Files.
            foreach (var (_, node) in (JsonObject)outputs)
            {
                if (node is not JsonObject out_)
                {
                    continue;
                }

                foreach (var (_, list) in out_)
                {
                    if (list is not JsonArray items)
                    {
                        continue;
                    }

                    foreach (JsonNode item in items)
                    {
                        if (item?["filename"] is not JsonNode)
                        {
                            continue;
                        }

                        string name = (string)item["filename"] ?? "";
                        string url = $"{BaseUrl}/view?filename={Uri.EscapeDataString(name)}&subfolder={Uri.EscapeDataString((string)item["subfolder"] ?? "")}&type={Uri.EscapeDataString((string)item["type"] ?? "output")}";
                        byte[] bytes = await _http.GetByteArrayAsync(url, ct).ConfigureAwait(false);
                        var file = new ArtifactFile { FileName = name, Bytes = bytes, Kind = ArtifactFile.Classify(name) };
                        result.Files.Add(file);
                        if (file.Kind == ArtifactKind.Image)
                        {
                            result.Pngs.Add(bytes);
                        }
                    }
                }
            }

            progress?.Invoke(1.0);
            if (ws.State == WebSocketState.Open)
            {
                try
                {
                    await ws.CloseOutputAsync(WebSocketCloseStatus.NormalClosure, "", CancellationToken.None).ConfigureAwait(false);
                }
                catch (Exception)
                {
                }
            }

            try
            {
                await listener.WaitAsync(TimeSpan.FromSeconds(1)).ConfigureAwait(false);
            }
            catch (Exception)
            {
            }

            if (result.Files.Count == 0)
            {
                result.Error = "the workflow finished but produced no file";
            }
        }
        catch (OperationCanceledException)
        {
            result.Error = "cancelled";
        }
        catch (Exception ex)
        {
            result.Error = $"{ex.GetType().Name}: {ex.Message}";
        }

        return result;
    }

    private static async Task ListenAsync(ClientWebSocket ws, Action<double> progress, CancellationToken ct)
    {
        var buf = new byte[16384];
        try
        {
            while (ws.State == WebSocketState.Open && !ct.IsCancellationRequested)
            {
                var sb = new StringBuilder();
                WebSocketReceiveResult r;
                do
                {
                    r = await ws.ReceiveAsync(buf, ct).ConfigureAwait(false);
                    if (r.MessageType == WebSocketMessageType.Close)
                    {
                        return;
                    }

                    if (r.MessageType == WebSocketMessageType.Text)
                    {
                        sb.Append(Encoding.UTF8.GetString(buf, 0, r.Count));
                    }
                }
                while (!r.EndOfMessage);

                if (sb.Length > 0 && JsonNode.Parse(sb.ToString()) is JsonNode m && (string)m["type"] == "progress"
                    && m["data"]?["max"] is JsonNode max && (double)max > 0)
                {
                    progress?.Invoke((double)m["data"]["value"] / (double)max);
                }
            }
        }
        catch (Exception)
        {
        }
    }

    private async Task<string> UploadAsync(string name, byte[] png, CancellationToken ct)
    {
        using var form = new MultipartFormDataContent();
        var file = new ByteArrayContent(png);
        file.Headers.ContentType = new System.Net.Http.Headers.MediaTypeHeaderValue("image/png");
        form.Add(file, "image", name);
        form.Add(new StringContent("true"), "overwrite");
        using HttpResponseMessage r = await _http.PostAsync($"{BaseUrl}/upload/image", form, ct).ConfigureAwait(false);
        string text = await r.Content.ReadAsStringAsync(ct).ConfigureAwait(false);
        r.EnsureSuccessStatusCode();
        return (string)JsonNode.Parse(text)?["name"] ?? name;
    }

    private static string Trim(string s) => s.Length > 300 ? s[..300] : s;
}

/// <summary>
/// Retro Diffusion over its HTTP API (<c>POST /v1/inferences</c>, key in <c>X-RD-Token</c>). The key comes
/// from the operating system's store through the AI dock's endpoint book, never a file. This is a stub in
/// the sense that it has only ever run against a local stub server: no paid call is made by any test.
/// </summary>
public sealed class RetroDiffusionProvider : IImageProvider, IDisposable
{
    public const string DefaultUrl = "https://api.retrodiffusion.ai";
    public const string EndpointName = "Retro Diffusion";

    private readonly HttpClient _http = new() { Timeout = TimeSpan.FromSeconds(120) };
    private readonly Func<string> _key;

    public string BaseUrl { get; set; }
    public string Style { get; set; } = "rd_fast__default";

    public string Id => "retrodiffusion";
    public string Name => "Retro Diffusion";
    public bool NeedsKey => true;

    public RetroDiffusionProvider(string baseUrl, Func<string> key)
    {
        BaseUrl = baseUrl.TrimEnd('/');
        _key = key;
    }

    public void Dispose() => _http.Dispose();

    public async Task<ImageResult> RunAsync(ImageRequest req, Action<double> progress, CancellationToken ct)
    {
        var result = new ImageResult { Model = Style, Workflow = "inferences", Seed = req.Seed };
        string key = _key?.Invoke();
        if (string.IsNullOrEmpty(key))
        {
            result.Error = $"no key for Retro Diffusion: add an endpoint named '{EndpointName}' in the AI dock (the key is kept in the operating system's store)";
            return result;
        }

        try
        {
            var body = new JsonObject
            {
                ["prompt"] = req.Prompt,
                ["width"] = req.Width > 0 ? req.Width : 64,
                ["height"] = req.Height > 0 ? req.Height : 64,
                ["num_images"] = 1,
                ["prompt_style"] = Style,
            };
            if (req.Seed >= 0)
            {
                body["seed"] = req.Seed;
            }

            if (req.InputPng != null)
            {
                body["input_image"] = Convert.ToBase64String(req.InputPng);
            }

            using var msg = new HttpRequestMessage(HttpMethod.Post, $"{BaseUrl}/v1/inferences")
            {
                Content = new StringContent(body.ToJsonString(), Encoding.UTF8, "application/json"),
            };
            msg.Headers.Add("X-RD-Token", key);
            progress?.Invoke(0.1);
            using HttpResponseMessage r = await _http.SendAsync(msg, ct).ConfigureAwait(false);
            string text = await r.Content.ReadAsStringAsync(ct).ConfigureAwait(false);
            if (!r.IsSuccessStatusCode)
            {
                result.Error = $"Retro Diffusion answered {(int)r.StatusCode}";
                return result;
            }

            JsonNode j = JsonNode.Parse(text);
            if (j?["base64_images"] is JsonArray images)
            {
                foreach (JsonNode b in images)
                {
                    result.Pngs.Add(Convert.FromBase64String((string)b));
                }
            }

            result.Model = (string)j?["model"] ?? Style;
            progress?.Invoke(1.0);
            if (result.Pngs.Count == 0)
            {
                result.Error = "Retro Diffusion returned no image";
            }
        }
        catch (OperationCanceledException)
        {
            result.Error = "cancelled";
        }
        catch (Exception ex)
        {
            result.Error = $"{ex.GetType().Name}: {ex.Message}";
        }

        return result;
    }
}
#endif
