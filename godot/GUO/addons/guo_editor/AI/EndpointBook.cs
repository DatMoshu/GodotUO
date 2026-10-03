#if TOOLS
namespace GUO.Editor;

using System;
using System.Collections.Generic;
using System.IO;
using System.Text.Json;
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

    private void Load()
    {
        try
        {
            if (File.Exists(_path))
            {
                Entries = JsonSerializer.Deserialize<List<Entry>>(File.ReadAllText(_path)) ?? new();
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
        File.WriteAllText(_path, JsonSerializer.Serialize(Entries, new JsonSerializerOptions { WriteIndented = true }));
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
