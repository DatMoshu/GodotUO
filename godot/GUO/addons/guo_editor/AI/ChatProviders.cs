#if TOOLS
namespace GUO.Editor;

using System;
using System.Collections.Generic;
using System.IO;
using System.Net.Http;
using System.Net.Http.Headers;
using System.Text;
using System.Text.Json.Nodes;
using System.Threading;
using System.Threading.Tasks;

/// <summary>One turn of a conversation.</summary>
public sealed record ChatMessage(string Role, string Text);

/// <summary>
/// A chat backend that streams its answer (ADR-0028). Implementations touch no
/// Godot type: they run on worker threads, and the dock marshals the chunks.
/// </summary>
public interface IChatProvider
{
    /// <summary>A stable key (the picker's value).</summary>
    string Id { get; }

    /// <summary>What the picker shows.</summary>
    string Name { get; }

    /// <summary>The model the next turn uses (empty for providers with no model list).</summary>
    string Model { get; set; }

    /// <summary>The models on offer; may be empty.</summary>
    Task<IReadOnlyList<string>> ListModelsAsync(CancellationToken ct);

    /// <summary>
    /// Sends the conversation and streams the answer: <paramref name="onChunk"/> gets each piece of
    /// text and whether it is the model's reasoning rather than its answer. Returns the stop reason.
    /// </summary>
    Task<string> StreamAsync(IReadOnlyList<ChatMessage> history, Action<string, bool> onChunk, CancellationToken ct);
}

/// <summary>
/// Shared HTTP plumbing: one client per provider, no overall timeout (an answer streams for
/// minutes) but an idle timeout, so a server that goes quiet cannot hang the editor.
/// </summary>
public abstract class HttpChatProvider : IChatProvider, IDisposable
{
    protected readonly HttpClient Http = new() { Timeout = Timeout.InfiniteTimeSpan };

    public abstract string Id { get; }
    public abstract string Name { get; }
    public string Model { get; set; } = "";

    /// <summary>How long a connection may be silent before the turn is abandoned (a cold model load can take a while).</summary>
    public TimeSpan IdleTimeout { get; set; } = TimeSpan.FromSeconds(120);

    /// <summary>How long listing models may take.</summary>
    public TimeSpan ListTimeout { get; set; } = TimeSpan.FromSeconds(8);

    public abstract Task<IReadOnlyList<string>> ListModelsAsync(CancellationToken ct);
    public abstract Task<string> StreamAsync(IReadOnlyList<ChatMessage> history, Action<string, bool> onChunk, CancellationToken ct);

    protected static JsonArray Messages(IReadOnlyList<ChatMessage> history)
    {
        var a = new JsonArray();
        foreach (ChatMessage m in history)
        {
            a.Add(new JsonObject { ["role"] = m.Role, ["content"] = m.Text });
        }

        return a;
    }

    /// <summary>Reads lines from a response, abandoning it if none arrives within the idle timeout.</summary>
    protected async Task ReadLinesAsync(HttpRequestMessage req, Func<string, bool> onLine, CancellationToken ct)
    {
        using var idle = CancellationTokenSource.CreateLinkedTokenSource(ct);
        idle.CancelAfter(IdleTimeout);
        try
        {
            using HttpResponseMessage resp = await Http.SendAsync(req, HttpCompletionOption.ResponseHeadersRead, idle.Token).ConfigureAwait(false);
            if (!resp.IsSuccessStatusCode)
            {
                string body = await resp.Content.ReadAsStringAsync(idle.Token).ConfigureAwait(false);
                throw new IOException($"HTTP {(int)resp.StatusCode}: {Short(body)}");
            }

            using Stream s = await resp.Content.ReadAsStreamAsync(idle.Token).ConfigureAwait(false);
            using var reader = new StreamReader(s, Encoding.UTF8);
            while (true)
            {
                idle.CancelAfter(IdleTimeout);
                string line = await reader.ReadLineAsync(idle.Token).ConfigureAwait(false);
                if (line == null || !onLine(line))
                {
                    break;
                }
            }
        }
        catch (OperationCanceledException) when (!ct.IsCancellationRequested)
        {
            throw new TimeoutException($"no data for {IdleTimeout.TotalSeconds:0} s");
        }
    }

    protected async Task<string> GetStringAsync(string url, CancellationToken ct, Action<HttpRequestMessage> decorate = null)
    {
        using var timer = CancellationTokenSource.CreateLinkedTokenSource(ct);
        timer.CancelAfter(ListTimeout);
        try
        {
            using var req = new HttpRequestMessage(HttpMethod.Get, url);
            decorate?.Invoke(req);
            using HttpResponseMessage resp = await Http.SendAsync(req, timer.Token).ConfigureAwait(false);
            string body = await resp.Content.ReadAsStringAsync(timer.Token).ConfigureAwait(false);
            if (!resp.IsSuccessStatusCode)
            {
                throw new IOException($"HTTP {(int)resp.StatusCode}: {Short(body)}");
            }

            return body;
        }
        catch (OperationCanceledException) when (!ct.IsCancellationRequested)
        {
            throw new TimeoutException($"{url}: no answer within {ListTimeout.TotalSeconds:0} s");
        }
    }

    protected static string Short(string s) => s.Length > 300 ? s[..300] + "..." : s;

    public void Dispose() => Http.Dispose();
}

/// <summary>
/// Ollama on loopback (default http://127.0.0.1:11434): <c>/api/tags</c> lists the models,
/// <c>/api/chat</c> streams one JSON object per line. Local and free; nothing leaves the machine.
/// </summary>
public sealed class OllamaProvider : HttpChatProvider
{
    public const string DefaultUrl = "http://127.0.0.1:11434";

    private readonly string _base;

    /// <summary>Ask a thinking model (qwen3 and the like) to show its reasoning first. Off: it answers directly.</summary>
    public bool Think { get; set; }

    public OllamaProvider(string baseUrl = null)
    {
        _base = (baseUrl ?? DefaultUrl).TrimEnd('/');
    }

    public override string Id => "ollama";
    public override string Name => "Ollama (local)";
    public string BaseUrl => _base;

    public override async Task<IReadOnlyList<string>> ListModelsAsync(CancellationToken ct)
    {
        JsonNode tags = JsonNode.Parse(await GetStringAsync(_base + "/api/tags", ct).ConfigureAwait(false));
        var names = new List<string>();
        if (tags?["models"] is JsonArray models)
        {
            foreach (JsonNode m in models)
            {
                string n = (string)m?["name"];
                if (!string.IsNullOrEmpty(n))
                {
                    names.Add(n);
                }
            }
        }

        return names;
    }

    public override async Task<string> StreamAsync(IReadOnlyList<ChatMessage> history, Action<string, bool> onChunk, CancellationToken ct)
    {
        var body = new JsonObject { ["model"] = Model, ["messages"] = Messages(history), ["stream"] = true, ["think"] = Think };
        using var req = new HttpRequestMessage(HttpMethod.Post, _base + "/api/chat")
        {
            Content = new StringContent(body.ToJsonString(), Encoding.UTF8, "application/json"),
        };
        string stop = "stream ended";
        await ReadLinesAsync(req, line =>
        {
            if (line.Length == 0)
            {
                return true;
            }

            JsonNode j = JsonNode.Parse(line);
            if ((string)j?["error"] is string err)
            {
                throw new IOException(err);
            }

            string think = (string)j?["message"]?["thinking"];
            if (!string.IsNullOrEmpty(think))
            {
                onChunk(think, true);
            }

            string text = (string)j?["message"]?["content"];
            if (!string.IsNullOrEmpty(text))
            {
                onChunk(text, false);
            }

            if ((bool?)j?["done"] == true)
            {
                stop = (string)j["done_reason"] ?? "stop";
                return false;
            }

            return true;
        }, ct).ConfigureAwait(false);
        return stop;
    }
}

/// <summary>
/// Any OpenAI-compatible endpoint the user added (<c>/models</c>, <c>/chat/completions</c> with
/// server-sent events). The key, if the endpoint wants one, comes from the operating system's
/// store through <see cref="EndpointBook"/>; this class only holds it in memory for the request.
/// </summary>
public sealed class OpenAiCompatProvider : HttpChatProvider
{
    private readonly string _base;
    private readonly Func<string> _key;
    private readonly string _name;

    public OpenAiCompatProvider(string name, string baseUrl, Func<string> key)
    {
        _name = name;
        _base = baseUrl.TrimEnd('/');
        _key = key;
    }

    public override string Id => "openai:" + _name;
    public override string Name => _name;
    public string BaseUrl => _base;

    private void Auth(HttpRequestMessage req)
    {
        string key = _key?.Invoke();
        if (!string.IsNullOrEmpty(key))
        {
            req.Headers.Authorization = new AuthenticationHeaderValue("Bearer", key);
        }
    }

    public override async Task<IReadOnlyList<string>> ListModelsAsync(CancellationToken ct)
    {
        JsonNode j = JsonNode.Parse(await GetStringAsync(_base + "/models", ct, Auth).ConfigureAwait(false));
        var names = new List<string>();
        if (j?["data"] is JsonArray data)
        {
            foreach (JsonNode m in data)
            {
                string id = (string)m?["id"];
                if (!string.IsNullOrEmpty(id))
                {
                    names.Add(id);
                }
            }
        }

        names.Sort(StringComparer.OrdinalIgnoreCase);
        return names;
    }

    public override async Task<string> StreamAsync(IReadOnlyList<ChatMessage> history, Action<string, bool> onChunk, CancellationToken ct)
    {
        var body = new JsonObject { ["model"] = Model, ["messages"] = Messages(history), ["stream"] = true };
        using var req = new HttpRequestMessage(HttpMethod.Post, _base + "/chat/completions")
        {
            Content = new StringContent(body.ToJsonString(), Encoding.UTF8, "application/json"),
        };
        Auth(req);
        string stop = "stream ended";
        await ReadLinesAsync(req, line =>
        {
            if (!line.StartsWith("data:", StringComparison.Ordinal))
            {
                return true;
            }

            string data = line[5..].Trim();
            if (data == "[DONE]")
            {
                stop = "stop";
                return false;
            }

            JsonNode choice = JsonNode.Parse(data)?["choices"]?[0];
            string text = (string)choice?["delta"]?["content"];
            if (!string.IsNullOrEmpty(text))
            {
                onChunk(text, false);
            }

            string reason = (string)choice?["finish_reason"];
            if (!string.IsNullOrEmpty(reason))
            {
                stop = reason;
            }

            return true;
        }, ct).ConfigureAwait(false);
        return stop;
    }
}
#endif
