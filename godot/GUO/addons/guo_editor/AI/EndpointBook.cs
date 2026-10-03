#if TOOLS
namespace GUO.Editor;

using System;
using System.Collections.Generic;
using System.IO;
using System.Text.Json.Nodes;
using GUO.Input.Touch.Pregame.Accounts;

/// <summary>
/// The OpenAI-compatible endpoints the user added: name, URL, model, and the key sealed by the
/// operating system's store (DPAPI on Windows: the same <see cref="SecretStore"/> the pre-game
/// accounts use). The file holds ciphertext only and lives in the user's own configuration folder
/// (<c>%APPDATA%/GUO/ai_endpoints.json</c>), never in a project, a setting file or .godot.
/// Where the platform has no store, the key is not kept and is typed again.
/// </summary>
internal sealed class EndpointBook
{
    internal sealed class Entry
    {
        public string Name { get; set; } = "";
        public string Url { get; set; } = "";
        public string Model { get; set; } = "";
        public Secret Key { get; set; }
    }

    private readonly string _path;

    public List<Entry> Entries { get; private set; } = new();

    public EndpointBook(string path = null)
    {
        _path = path ?? DefaultPath;
        Load();
    }

    public static string DefaultPath =>
        Path.Combine(System.Environment.GetFolderPath(System.Environment.SpecialFolder.ApplicationData), "GUO", "ai_endpoints.json");

    private static string Binding(string name, string url) => $"ai:{url}:{name}";

    // Hand-written JSON on purpose: System.Text.Json's typed serializer caches metadata for our types
    // in the runtime, which keeps this assembly from unloading when the editor reloads it.
    private void Load()
    {
        Entries = new();
        try
        {
            if (File.Exists(_path) && JsonNode.Parse(File.ReadAllText(_path)) is JsonArray list)
            {
                foreach (JsonNode n in list)
                {
                    JsonNode k = n?["Key"];
                    Entries.Add(new Entry
                    {
                        Name = (string)n?["Name"] ?? "",
                        Url = (string)n?["Url"] ?? "",
                        Model = (string)n?["Model"] ?? "",
                        Key = k == null ? null : new Secret { Store = (string)k["store"] ?? SecretStore.None, Iv = (string)k["iv"], Blob = (string)k["blob"] },
                    });
                }
            }
        }
        catch (Exception)
        {
            Entries = new();
        }
    }

    private void Save()
    {
        Directory.CreateDirectory(Path.GetDirectoryName(_path)!);
        var list = new JsonArray();
        foreach (Entry e in Entries)
        {
            var o = new JsonObject { ["Name"] = e.Name, ["Url"] = e.Url, ["Model"] = e.Model };
            if (e.Key != null)
            {
                o["Key"] = new JsonObject { ["store"] = e.Key.Store, ["iv"] = e.Key.Iv, ["blob"] = e.Key.Blob };
            }

            list.Add(o);
        }

        File.WriteAllText(_path, list.ToJsonString(new System.Text.Json.JsonSerializerOptions { WriteIndented = true }));
    }

    /// <summary>Adds or replaces an endpoint. A blank <paramref name="key"/> keeps the stored one; null with a reason if the key could not be sealed.</summary>
    public bool Put(string name, string url, string model, string key, out string why)
    {
        why = null;
        Entry old = Entries.Find(e => e.Name == name);
        var entry = new Entry { Name = name, Url = url, Model = model, Key = old?.Key };
        if (!string.IsNullOrEmpty(key))
        {
            Secret sealedKey = SecretStore.Current.Protect(Binding(name, url), key, out why);
            if (sealedKey == null)
            {
                return false;
            }

            entry.Key = sealedKey;
        }

        Entries.RemoveAll(e => e.Name == name);
        Entries.Add(entry);
        Save();
        return true;
    }

    public void Remove(string name)
    {
        Entry e = Entries.Find(x => x.Name == name);
        if (e == null)
        {
            return;
        }

        if (e.Key != null)
        {
            SecretStore.Current.Forget(Binding(e.Name, e.Url), e.Key);
        }

        Entries.Remove(e);
        Save();
    }

    /// <summary>The key, opened for one request; null if there is none.</summary>
    public string KeyFor(Entry e) =>
        e?.Key == null ? null : SecretStore.Current.Unprotect(Binding(e.Name, e.Url), e.Key, out _);

    public OpenAiCompatProvider Provider(Entry e) => new(e.Name, e.Url, () => KeyFor(e)) { Model = e.Model };

    /// <summary>Whether this platform can keep a key at all.</summary>
    public static bool CanKeepKeys => SecretStore.Current.Available;
}
#endif
