// SPDX-License-Identifier: BSD-2-Clause
// A shard's content descriptor, guo/shard-content@1 (ADR-0026 section 4): the packs a shard
// runs and the catalogues they come from. No Godot dependency: the headless smoke runs it.
using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Net.Http;
using System.Text.Json;
using System.Threading;
using System.Threading.Tasks;

namespace GUO.Store;

internal sealed class StoreShardCatalogue
{
    public string Url { get; init; }
    /// <summary>The key the shard says signs this catalogue; null only for an unsigned catalogue on loopback or the LAN.</summary>
    public string Key { get; init; }
}

internal sealed class StoreShardContent
{
    public const string Schema = "guo/shard-content@1";
    public string Url { get; init; }
    public string ShardName { get; init; }
    public IReadOnlyList<StoreShardCatalogue> Catalogues { get; init; }
    public StoreContentLock Lock { get; init; }
    public bool ScriptsAllowed { get; init; }
    public string IdentityHash => Lock.IdentityHash;

    /// <summary>Fetches and checks a descriptor: HTTPS, or HTTP on loopback or the LAN; no redirects.</summary>
    public static async Task<StoreShardContent> Fetch(string url, CancellationToken ct = default)
    {
        StorePack.Require(Uri.TryCreate(url?.Trim(), UriKind.Absolute, out var uri) && StoreTrust.AllowedRemote(uri),
            "A shard's content address must be HTTPS, or HTTP on this computer or the LAN.");
        using var http = new HttpClient(new HttpClientHandler { AllowAutoRedirect = false }) { Timeout = TimeSpan.FromSeconds(30) };
        using var response = await http.GetAsync(uri, HttpCompletionOption.ResponseHeadersRead, ct).ConfigureAwait(false);
        response.EnsureSuccessStatusCode();
        StorePack.Require(response.Content.Headers.ContentLength is not long size || size <= StorePack.MaxManifest, "The shard's content descriptor is too large");
        using var input = await response.Content.ReadAsStreamAsync(ct).ConfigureAwait(false);
        using var bytes = new MemoryStream();
        StorePack.CopyLimited(input, bytes, StorePack.MaxManifest);
        return Parse(uri.AbsoluteUri, bytes.ToArray());
    }

    public static StoreShardContent Parse(string url, byte[] bytes)
    {
        using var doc = JsonDocument.Parse(bytes);
        var root = doc.RootElement;
        StorePack.UniqueJson(root);
        StorePack.Require(root.GetProperty("schema").GetString() == Schema, "Not a guo/shard-content@1 descriptor");
        string name = root.GetProperty("shard").GetProperty("name").GetString();
        StorePack.Require(!string.IsNullOrWhiteSpace(name) && name.Length <= 200 && !name.Any(char.IsControl), "Invalid shard name");
        var catalogues = new List<StoreShardCatalogue>();
        foreach (var c in root.GetProperty("catalogues").EnumerateArray())
        {
            string address = StoreAddress.Normalize(c.GetProperty("url").GetString());
            var catalogueUri = new Uri(address);
            StorePack.Require(StoreTrust.AllowedRemote(catalogueUri), "A shard's catalogue must be HTTPS, or HTTP on this computer or the LAN.");
            string key = c.TryGetProperty("key", out var k) && k.ValueKind == JsonValueKind.String ? k.GetString() : null;
            if (key != null) Ed25519.Decode(key, 32);
            else StorePack.Require(catalogueUri.Scheme == "http" && StoreTrust.LocalHost(catalogueUri.Host),
                "Only a catalogue on this computer or the LAN may be unsigned.");
            StorePack.Require(catalogues.Count < 16 && catalogues.All(x => x.Url != address), "Too many or duplicate catalogues");
            catalogues.Add(new StoreShardCatalogue { Url = address, Key = key });
        }
        StorePack.Require(catalogues.Count > 0, "The descriptor names no catalogue");
        var contentLock = JsonSerializer.Deserialize<StoreContentLock>(root.GetProperty("lock").GetRawText());
        StorePack.Require(contentLock != null && contentLock.Schema == "guo/content-lock@1", "Invalid content lock");
        StorePack.Id(contentLock.Pack); StorePack.Version(contentLock.Version); StorePack.Digest(contentLock.IdentityHash);
        StorePack.Require(contentLock.Bindings != null && contentLock.Bindings.Count <= 65536, "Invalid bindings");
        string scripts = root.TryGetProperty("scripts", out var s) ? s.GetString() : "allowed";
        StorePack.Require(scripts is "allowed" or "forbidden", "scripts must be allowed or forbidden");
        return new StoreShardContent { Url = url, ShardName = name, Catalogues = catalogues, Lock = contentLock, ScriptsAllowed = scripts == "allowed" };
    }

    /// <summary>What the player is asked to approve: the packs, the catalogues and their keys.</summary>
    public string Summary() =>
        $"{ShardName} uses {Lock.Pack} {Lock.Version} and what it needs, from "
        + string.Join(", ", Catalogues.Select(c => new Uri(c.Url).Host + (c.Key == null ? " (unsigned)" : " (key " + Ed25519.Fingerprint(Ed25519.Decode(c.Key, 32)) + ")")))
        + (ScriptsAllowed ? "" : ". Script packs are off on this shard");

    /// <summary>
    /// Installs <paramref name="pack"/> <paramref name="version"/> and what it needs from <paramref name="catalogues"/>,
    /// approving only the key each one names. <paramref name="anyKey"/> is the shard operator's deploy step
    /// (tools/shard_content): a catalogue named without a key is approved with whatever key it has, and its
    /// key lands in <paramref name="trust"/> for the descriptor. Returns the installer, for verification.
    /// </summary>
    public static async Task<StoreClient> Install(IReadOnlyList<StoreShardCatalogue> catalogues, string pack, string version, string storeRoot,
        StoreTrust trust, int profileVersion, bool anyKey, Action<string> progress = null, CancellationToken ct = default)
    {
        var all = new List<StoreEntry>();
        foreach (var catalogue in catalogues)
        {
            using var client = new StoreClient(catalogue.Url, storeRoot, profileVersion) { Trust = trust };
            progress?.Invoke("Reading " + new Uri(catalogue.Url).Host + "…");
            IReadOnlyList<StoreEntry> entries;
            try { entries = await client.FetchIndex(ct).ConfigureAwait(false); }
            catch (StoreTrustRequired pending) when (catalogue.Key != null ? pending.Pending.Key == catalogue.Key : anyKey && !pending.KeyChanged)
            {
                trust.Approve(pending.Pending);
                entries = await client.FetchIndex(ct).ConfigureAwait(false);
            }
            if (!anyKey || catalogue.Key != null)
                StorePack.Require(catalogue.Key == null ? !client.CatalogueSigned : client.CatalogueSigned,
                    catalogue.Key == null ? "The shard said this catalogue is unsigned, but it is signed." : "The shard's catalogue is not signed.");
            foreach (var entry in entries)
                if (!all.Any(e => e.Manifest.Id == entry.Manifest.Id && e.Manifest.Version == entry.Manifest.Version)) all.Add(entry);
        }
        var root = all.FirstOrDefault(e => e.Manifest.Id == pack && e.Manifest.Version == version);
        StorePack.Require(root != null, $"None of the shard's catalogues lists {pack} {version}.");
        var installer = new StoreClient(catalogues[0].Url, storeRoot, profileVersion) { Trust = trust };
        try
        {
            progress?.Invoke("Installing " + root.Manifest.Title + "…");
            await installer.InstallWithDependencies(root, all, ct).ConfigureAwait(false);
            return installer;
        }
        catch { installer.Dispose(); throw; }
    }

    /// <summary>
    /// The client components of a deployment that its lock gives no numeric slot. The client's mount
    /// (StoreRuntimeContent) needs one for each, so a lock with any of these cannot be played with.
    /// Server components, scripts, translations and wearables take none.
    /// </summary>
    public static List<string> Unbound(StoreContentLock contentLock, StoreVerifiedContent snapshot) =>
        snapshot.Packs.Values.SelectMany(p => (p.Manifest.Components ?? new())
            .Where(c => c.Target != "server" && c.Type is not ("script" or "translation" or "wearable"))
            .Select(c => p.Id + ":" + c.Id))
            .Where(identity => !contentLock.Bindings.ContainsKey(identity)).OrderBy(i => i, StringComparer.Ordinal).ToList();

    public static void RequireMountable(StoreContentLock contentLock, StoreVerifiedContent snapshot)
    {
        var unbound = Unbound(contentLock, snapshot);
        StorePack.Require(unbound.Count == 0, $"The lock gives {unbound.Count} client component(s) no numeric slot ("
            + string.Join(", ", unbound.Take(4)) + (unbound.Count > 4 ? ", ..." : "") + "), so the client could not mount it.");
    }

    /// <summary>
    /// Installs the lock's packs from the descriptor's catalogues and writes the lock under
    /// <paramref name="storeRoot"/>/.shard-content/. The player's yes to <see cref="Summary"/> is the
    /// approval of each catalogue key the descriptor names; a catalogue answering with another key is refused.
    /// Returns the lock's path, for ShardSession.
    /// </summary>
    public async Task<string> Prepare(string storeRoot, StoreTrust trust, int profileVersion, Action<string> progress = null, CancellationToken ct = default)
    {
        using (var installer = await Install(Catalogues, Lock.Pack, Lock.Version, storeRoot, trust, profileVersion, false, progress, ct).ConfigureAwait(false))
            RequireMountable(Lock, Lock.Verify(installer));
        string folder = Path.Combine(Path.GetFullPath(storeRoot), ".shard-content");
        StoreClient.NoLinks(folder);
        Directory.CreateDirectory(folder);
        string path = Path.Combine(folder, Lock.IdentityHash[..16] + ".json");
        string temporary = path + "." + Guid.NewGuid().ToString("N") + ".tmp";
        try
        {
            File.WriteAllBytes(temporary, JsonSerializer.SerializeToUtf8Bytes(Lock, new JsonSerializerOptions { WriteIndented = true }));
            File.Move(temporary, path, true);
        }
        finally { if (File.Exists(temporary)) File.Delete(temporary); }
        progress?.Invoke("Installed and verified.");
        return path;
    }
}
