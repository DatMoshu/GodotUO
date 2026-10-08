#if TOOLS
namespace GUO.Editor;

using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Net.Http;
using System.Text.Json.Nodes;
using System.Threading;
using System.Threading.Tasks;
using GUO.Input.Touch.Pregame.Accounts;

/// <summary>The kinds of third-party service the editor can be pointed at.</summary>
public enum ServiceKind
{
    /// <summary>An OpenAI-compatible chat endpoint (kept in the <see cref="EndpointBook"/>).</summary>
    OpenAi,
    ComfyUi,
    RetroDiffusion,
    Ollama,
}

/// <summary>One service as the rest of the editor sees it: never the key itself.</summary>
public sealed record ServiceInfo(ServiceKind Kind, string Name, string Url, string Model, bool AllowClientArt, bool HasKey);

/// <summary>
/// Every third-party service endpoint and key in one place (the AI dock's Services tab, ADR-0028).
/// Providers read it instead of keeping their own settings: <c>AiHub.Services.Find(ServiceKind.ComfyUi)</c>
/// gives the URL, <see cref="KeyFor"/> opens the key for one request. Keys go into the operating
/// system's store (the pre-game accounts' <see cref="SecretStore"/>) and the file holds ciphertext
/// only, in the user's own configuration folder (<c>%APPDATA%/GUO/ai_services.json</c>), never in a
/// project, a setting file or .godot. OpenAI-compatible endpoints stay in the
/// <see cref="EndpointBook"/> (its file, its keys); this class presents them in the same list, so
/// there is one registry to read.
/// <para>
/// <see cref="TestAsync"/> only ever asks a free, read-only endpoint (a model list, the server's
/// status, a credit balance); it never starts a generation, so it never spends anything.
/// </para>
/// </summary>
public sealed class ServiceBook
{
    public const string ComfyUiDefaultUrl = "http://127.0.0.1:8188";
    public const string RetroDiffusionDefaultUrl = "https://api.retrodiffusion.ai";

    private sealed class Entry
    {
        public ServiceKind Kind;
        public string Name = "";
        public string Url = "";
        public string Model = "";
        public bool AllowClientArt;
        public Secret Key;
    }

    private readonly EndpointBook _endpoints;
    private readonly string _path;
    private List<Entry> _others = new();

    internal ServiceBook(EndpointBook endpoints, string path = null)
    {
        _endpoints = endpoints;
        _path = path ?? DefaultPath;
        Load();
    }

    /// <summary>A view over the user's real files (a fresh read each time, no static state to outlive a reload).</summary>
    internal static ServiceBook Open() => new(new EndpointBook());

    public static string DefaultPath =>
        Path.Combine(System.Environment.GetFolderPath(System.Environment.SpecialFolder.ApplicationData), "GUO", "ai_services.json");

    public static string DefaultUrl(ServiceKind kind) => kind switch
    {
        ServiceKind.ComfyUi => ComfyUiDefaultUrl,
        ServiceKind.RetroDiffusion => RetroDiffusionDefaultUrl,
        ServiceKind.Ollama => OllamaProvider.DefaultUrl,
        _ => "",
    };

    public static string KindLabel(ServiceKind kind) => kind switch
    {
        ServiceKind.OpenAi => "OpenAI-compatible",
        ServiceKind.ComfyUi => "ComfyUI",
        ServiceKind.RetroDiffusion => "Retro Diffusion",
        _ => "Ollama",
    };

    private static string Binding(Entry e) => $"svc:{e.Kind}:{e.Url}:{e.Name}";

    /// <summary>Everything registered, OpenAI-compatible endpoints first.</summary>
    public IReadOnlyList<ServiceInfo> List()
    {
        var all = new List<ServiceInfo>();
        foreach (EndpointBook.Entry e in _endpoints.Entries)
        {
            all.Add(new ServiceInfo(ServiceKind.OpenAi, e.Name, e.Url, e.Model, e.AllowClientArt, e.Key != null));
        }

        foreach (Entry e in _others.OrderBy(x => x.Kind).ThenBy(x => x.Name, StringComparer.OrdinalIgnoreCase))
        {
            all.Add(new ServiceInfo(e.Kind, e.Name, e.Url, e.Model, e.AllowClientArt, e.Key != null));
        }

        return all;
    }

    /// <summary>The first service of a kind (or the one with that name), or null.</summary>
    public ServiceInfo Find(ServiceKind kind, string name = null) =>
        List().FirstOrDefault(s => s.Kind == kind && (name == null || s.Name == name));

    /// <summary>The key, opened for one request; null if there is none.</summary>
    public string KeyFor(ServiceInfo s)
    {
        if (!AiFeatures.Enabled) return "";
        if (s == null)
        {
            return null;
        }

        if (s.Kind == ServiceKind.OpenAi)
        {
            return _endpoints.KeyFor(_endpoints.Entries.Find(e => e.Name == s.Name));
        }

        Entry e = _others.Find(x => x.Kind == s.Kind && x.Name == s.Name);
        return e?.Key == null ? null : SecretStore.Current.Unprotect(Binding(e), e.Key, out _);
    }

    /// <summary>Adds or replaces a service. A blank <paramref name="key"/> keeps the stored one.</summary>
    public bool Put(ServiceKind kind, string name, string url, string model, string key, bool allowClientArt, out string why)
    {
        why = null;
        name = (name ?? "").Trim();
        url = (url ?? "").Trim().TrimEnd('/');
        if (name.Length == 0 || !url.StartsWith("http", StringComparison.OrdinalIgnoreCase))
        {
            why = "a service needs a name and an http(s) URL";
            return false;
        }

        if (kind == ServiceKind.OpenAi)
        {
            return _endpoints.Put(name, url, model ?? "", key, out why, allowClientArt);
        }

        Entry old = _others.Find(x => x.Kind == kind && x.Name == name);
        var entry = new Entry { Kind = kind, Name = name, Url = url, Model = model ?? "", AllowClientArt = allowClientArt, Key = old?.Key };
        if (string.IsNullOrEmpty(key) && old?.Key != null && old.Url != url)
        {
            key = SecretStore.Current.Unprotect(Binding(old), old.Key, out _);
        }

        if (!string.IsNullOrEmpty(key))
        {
            Secret sealedKey = SecretStore.Current.Protect(Binding(entry), key, out why);
            if (sealedKey == null)
            {
                return false;
            }

            entry.Key = sealedKey;
        }

        _others.RemoveAll(x => x.Kind == kind && x.Name == name);
        _others.Add(entry);
        Save();
        return true;
    }

    public void Remove(ServiceKind kind, string name)
    {
        if (kind == ServiceKind.OpenAi)
        {
            _endpoints.Remove(name);
            return;
        }

        Entry e = _others.Find(x => x.Kind == kind && x.Name == name);
        if (e == null)
        {
            return;
        }

        if (e.Key != null)
        {
            SecretStore.Current.Forget(Binding(e), e.Key);
        }

        _others.Remove(e);
        Save();
    }

    // Hand-written JSON on purpose (see EndpointBook): the typed serializer would pin the assembly.
    private void Load()
    {
        _others = new();
        try
        {
            if (File.Exists(_path) && JsonNode.Parse(File.ReadAllText(_path)) is JsonArray list)
            {
                foreach (JsonNode n in list)
                {
                    if (!Enum.TryParse((string)n?["Kind"], out ServiceKind kind) || kind == ServiceKind.OpenAi)
                    {
                        continue;
                    }

                    JsonNode k = n["Key"];
                    _others.Add(new Entry
                    {
                        Kind = kind,
                        Name = (string)n["Name"] ?? "",
                        Url = (string)n["Url"] ?? "",
                        Model = (string)n["Model"] ?? "",
                        AllowClientArt = (bool?)n["AllowClientArt"] == true,
                        Key = k == null ? null : new Secret { Store = (string)k["store"] ?? SecretStore.None, Iv = (string)k["iv"], Blob = (string)k["blob"] },
                    });
                }
            }
        }
        catch (Exception)
        {
            _others = new();
        }
    }

    private void Save()
    {
        Directory.CreateDirectory(Path.GetDirectoryName(_path)!);
        var list = new JsonArray();
        foreach (Entry e in _others)
        {
            var o = new JsonObject
            {
                ["Kind"] = e.Kind.ToString(), ["Name"] = e.Name, ["Url"] = e.Url, ["Model"] = e.Model, ["AllowClientArt"] = e.AllowClientArt,
            };
            if (e.Key != null)
            {
                o["Key"] = new JsonObject { ["store"] = e.Key.Store, ["iv"] = e.Key.Iv, ["blob"] = e.Key.Blob };
            }

            list.Add(o);
        }

        File.WriteAllText(_path, list.ToJsonString(new System.Text.Json.JsonSerializerOptions { WriteIndented = true }));
    }

    // --- the free test --------------------------------------------------------------------------

    /// <summary>
    /// Asks the service something free and read-only: the model list (OpenAI-compatible, Ollama), the
    /// server's status (ComfyUI), the credit balance (Retro Diffusion). Never a generation. Returns
    /// whether it answered, and one line for the status bar (never the key).
    /// </summary>
    public async Task<(bool Ok, string Detail)> TestAsync(ServiceInfo s, CancellationToken ct = default)
    {
        if (!AiFeatures.Enabled) return (false, AiFeatures.DisabledMessage);
        using var aiLife = AiFeatures.Link(ct);
        ct = aiLife.Token;

        if (s == null)
        {
            return (false, "no such service");
        }

        string url = s.Url.TrimEnd('/');
        string key = KeyFor(s);
        using var http = new HttpClient { Timeout = TimeSpan.FromSeconds(8) };
        try
        {
            string path = s.Kind switch
            {
                ServiceKind.OpenAi => "/models",
                ServiceKind.Ollama => "/api/tags",
                ServiceKind.ComfyUi => "/system_stats",
                _ => "/v1/inferences/credits",
            };
            using var req = new HttpRequestMessage(HttpMethod.Get, url + path);
            if (!string.IsNullOrEmpty(key))
            {
                if (s.Kind == ServiceKind.RetroDiffusion)
                {
                    req.Headers.Add("X-RD-Token", key);
                }
                else
                {
                    req.Headers.Authorization = new System.Net.Http.Headers.AuthenticationHeaderValue("Bearer", key);
                }
            }
            else if (s.Kind == ServiceKind.RetroDiffusion)
            {
                return (false, "Retro Diffusion needs a key to test");
            }

            using HttpResponseMessage resp = await http.SendAsync(req, ct).ConfigureAwait(false);
            string body = await resp.Content.ReadAsStringAsync(ct).ConfigureAwait(false);
            if (!resp.IsSuccessStatusCode)
            {
                return (false, $"HTTP {(int)resp.StatusCode}");
            }

            JsonNode j = null;
            try
            {
                j = JsonNode.Parse(body);
            }
            catch (Exception)
            {
            }

            switch (s.Kind)
            {
                case ServiceKind.OpenAi:
                    return (true, $"{(j?["data"] as JsonArray)?.Count ?? 0} model(s)");
                case ServiceKind.Ollama:
                    return (true, $"{(j?["models"] as JsonArray)?.Count ?? 0} model(s)");
                case ServiceKind.ComfyUi:
                    string v = (string)j?["system"]?["comfyui_version"];
                    return (true, v != null ? $"ComfyUI {v}" : "ComfyUI answered");
                default:
                    return (true, "Retro Diffusion answered (credit balance only, nothing generated)");
            }
        }
        catch (Exception ex) when (ex is HttpRequestException or TaskCanceledException or OperationCanceledException)
        {
            return (false, ex is HttpRequestException ? "could not connect" : "no answer within 8 s");
        }
    }
}
#endif
