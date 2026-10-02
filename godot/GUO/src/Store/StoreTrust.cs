// SPDX-License-Identifier: BSD-2-Clause
// Which catalogue keys this installation trusts (ADR-0026 section 3). No Godot
// dependency: the headless smoke runs the same code.
using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Net;
using System.Text.Json;
using System.Text.Json.Serialization;

namespace GUO.Store;

internal sealed class StoreCatalogueRecord
{
    [JsonPropertyName("url")] public string Url { get; set; }
    [JsonPropertyName("id")] public string Id { get; set; }
    [JsonPropertyName("title")] public string Title { get; set; }
    [JsonPropertyName("key")] public string Key { get; set; }
    [JsonPropertyName("sequence")] public long Sequence { get; set; }
    [JsonIgnore] public bool Official { get; set; }
}

/// <summary>A signed catalogue whose key this installation has not approved, or whose key changed.
/// The Store window shows the fingerprint and calls <see cref="StoreTrust.Approve"/> on a yes.</summary>
internal sealed class StoreTrustRequired : Exception
{
    public StoreCatalogueRecord Pending { get; }
    public bool KeyChanged { get; }
    public string Fingerprint { get; }
    public StoreTrustRequired(StoreCatalogueRecord pending, bool keyChanged, string fingerprint)
        : base(keyChanged ? $"The catalogue at {pending.Url} is now signed with a different key ({fingerprint}). Approve it only if its owner announced a new key."
                          : $"New catalogue \"{pending.Title}\" at {pending.Url}, key {fingerprint}. Approve it to list its packs.")
    { Pending = pending; KeyChanged = keyChanged; Fingerprint = fingerprint; }
}

internal sealed class StoreTrust
{
    /// <summary>The official GUO catalogue (ADR-0026): reviewed in the open, signed in its repository's CI.</summary>
    public const string OfficialUrl = "https://datmoshu.github.io/GodotUO-packs/";
    public const string OfficialId = "guo-official";
    public const string OfficialTitle = "GUO packs";
    /// <summary>The official catalogue's public keys. A new client release is how a key is rotated.
    /// Empty until the catalogue repository's key is made; the official catalogue then needs approval like any other.</summary>
    public static readonly string[] OfficialKeys = Array.Empty<string>();

    private readonly string _path;
    private readonly object _gate = new();
    public StoreTrust(string path) => _path = Path.GetFullPath(path);

    private List<StoreCatalogueRecord> Load()
    {
        if (!File.Exists(_path)) return new List<StoreCatalogueRecord>();
        StoreClient.NoLinks(_path);
        StorePack.Require(new FileInfo(_path).Length <= 1024 * 1024, "Catalogue list too large");
        return JsonSerializer.Deserialize<List<StoreCatalogueRecord>>(File.ReadAllBytes(_path)) ?? new List<StoreCatalogueRecord>();
    }

    private void Save(List<StoreCatalogueRecord> records)
    {
        StoreClient.NoLinks(_path);
        Directory.CreateDirectory(Path.GetDirectoryName(_path));
        string temporary = _path + "." + Guid.NewGuid().ToString("N") + ".tmp";
        try
        {
            File.WriteAllBytes(temporary, JsonSerializer.SerializeToUtf8Bytes(records, new JsonSerializerOptions { WriteIndented = true }));
            File.Move(temporary, _path, true);
        }
        finally { if (File.Exists(temporary)) File.Delete(temporary); }
    }

    /// <summary>The catalogues the Store lists from: the official one first, then the ones the player added.</summary>
    public IReadOnlyList<StoreCatalogueRecord> Catalogues()
    {
        lock (_gate)
        {
            var stored = Load();
            var official = stored.FirstOrDefault(r => r.Url == OfficialUrl)
                ?? new StoreCatalogueRecord { Url = OfficialUrl, Id = OfficialId, Title = OfficialTitle };
            official.Official = true;
            return new[] { official }.Concat(stored.Where(r => r.Url != OfficialUrl)).ToList();
        }
    }

    /// <summary>Adds a catalogue address with no key yet; its first fetch asks for approval.</summary>
    public string Add(string url)
    {
        url = StoreAddress.Normalize(url);
        lock (_gate)
        {
            var records = Load();
            if (url != OfficialUrl && records.All(r => r.Url != url))
            {
                StorePack.Require(records.Count < 64, "Too many catalogues");
                records.Add(new StoreCatalogueRecord { Url = url, Title = new Uri(url).Host });
                Save(records);
            }
        }
        return url;
    }

    public void Remove(string url)
    {
        url = StoreAddress.Normalize(url);
        StorePack.Require(url != OfficialUrl, "The official catalogue cannot be removed.");
        lock (_gate)
        {
            var records = Load();
            if (records.RemoveAll(r => r.Url == url) > 0) Save(records);
        }
    }

    /// <summary>Called with a signature that already verified against <paramref name="key"/>: accepts a pinned
    /// key, refuses an older sequence, and throws <see cref="StoreTrustRequired"/> for a key not yet approved.</summary>
    public void Evaluate(string url, string id, string title, byte[] key, long sequence)
    {
        url = StoreAddress.Normalize(url);
        string text = Ed25519.Prefix + Convert.ToBase64String(key);
        lock (_gate)
        {
            var records = Load();
            var record = records.FirstOrDefault(r => r.Url == url);
            bool official = url == OfficialUrl && OfficialKeys.Contains(text, StringComparer.Ordinal);
            if (!official)
            {
                if (record?.Key == null)
                    throw new StoreTrustRequired(new StoreCatalogueRecord { Url = url, Id = id, Title = title, Key = text, Sequence = sequence }, false, Ed25519.Fingerprint(key));
                if (record.Key != text)
                    throw new StoreTrustRequired(new StoreCatalogueRecord { Url = url, Id = id, Title = title, Key = text, Sequence = sequence }, true, Ed25519.Fingerprint(key));
                StorePack.Require(record.Id == id, "The catalogue at this address changed its identity.");
            }
            StorePack.Require(record == null || sequence >= record.Sequence, "This catalogue's index is older than one already seen (a rollback). Try again later.");
            if (record == null)
            {
                records.Add(new StoreCatalogueRecord { Url = url, Id = id, Title = title, Key = text, Sequence = sequence });
                Save(records);
            }
            else if (record.Sequence != sequence || record.Title != title)
            {
                record.Sequence = sequence; record.Title = title;
                Save(records);
            }
        }
    }

    /// <summary>The player's yes to a <see cref="StoreTrustRequired"/>: pins that key for that address.</summary>
    public void Approve(StoreCatalogueRecord pending)
    {
        string url = StoreAddress.Normalize(pending.Url);
        lock (_gate)
        {
            var records = Load();
            var record = records.FirstOrDefault(r => r.Url == url);
            if (record == null) records.Add(record = new StoreCatalogueRecord { Url = url });
            record.Id = pending.Id; record.Title = pending.Title; record.Key = pending.Key;
            record.Sequence = Math.Max(record.Key == pending.Key ? record.Sequence : 0, pending.Sequence);
            Save(records);
        }
    }

    /// <summary>HTTPS anywhere; plain HTTP only to loopback or a private LAN address (ADR-0026 section 2).</summary>
    public static bool AllowedRemote(Uri uri)
    {
        if (uri == null || !uri.IsAbsoluteUri || !string.IsNullOrEmpty(uri.UserInfo) || !string.IsNullOrEmpty(uri.Fragment)) return false;
        if (uri.Scheme == "https") return !string.IsNullOrEmpty(uri.Host);
        return uri.Scheme == "http" && LocalHost(uri.Host);
    }

    public static bool LocalHost(string host)
    {
        host = host.Trim('[', ']');
        if (host == "localhost" || host.EndsWith(".localhost", StringComparison.OrdinalIgnoreCase)
            || host.EndsWith(".lan", StringComparison.OrdinalIgnoreCase) || host.EndsWith(".local", StringComparison.OrdinalIgnoreCase)) return true;
        if (!IPAddress.TryParse(host, out var address)) return false;
        if (IPAddress.IsLoopback(address)) return true;
        if (address.IsIPv6LinkLocal || address.IsIPv6UniqueLocal) return true;
        byte[] b = address.GetAddressBytes();
        return b.Length == 4 && (b[0] == 10 || (b[0] == 172 && b[1] >= 16 && b[1] <= 31) || (b[0] == 192 && b[1] == 168) || (b[0] == 169 && b[1] == 254));
    }
}
