// SPDX-License-Identifier: BSD-2-Clause
using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Net.Http;
using System.Text;
using System.Text.Json;
using System.Threading;
using System.Threading.Tasks;

namespace GUO.Store;

/// <summary>No Godot dependency: the headless smoke runs the real installer.</summary>
internal sealed class StoreClient : IDisposable
{
    private readonly HttpClient _http = new(new HttpClientHandler { AllowAutoRedirect = false });
    private readonly string _root;
    private readonly int _profileVersion;
    private readonly Uri _base;
    public string Root => _root;
    public Func<string, string, bool> BackgroundRemoved { get; set; }
    public string LastUninstallMessage { get; private set; } = "Pack removed. Reopen Options to refresh backgrounds.";

    public StoreClient(string storeUrl, string installRoot, int profileVersion)
    {
        _base = new Uri(StoreAddress.Normalize(storeUrl), UriKind.Absolute);
        _root = Path.GetFullPath(installRoot);
        _profileVersion = profileVersion;
        _http.Timeout = TimeSpan.FromMinutes(5);
    }

    private Uri Address(string relative)
    {
        StorePack.SafePath(relative);
        StorePack.Require(!relative.Contains('%'), "Encoded URLs are not allowed");
        var uri = new Uri(_base, relative);
        StorePack.Require(uri.Scheme == _base.Scheme && uri.Authority == _base.Authority && uri.AbsolutePath.StartsWith(_base.AbsolutePath, StringComparison.Ordinal), "Asset URL escapes store");
        return uri;
    }

    private async Task Download(string relative, Stream output, long limit, CancellationToken ct)
    {
        using var response = await _http.GetAsync(Address(relative), HttpCompletionOption.ResponseHeadersRead, ct).ConfigureAwait(false);
        response.EnsureSuccessStatusCode();
        StorePack.Require(response.Content.Headers.ContentLength is not long size || size <= limit, "Download exceeds size limit");
        using var input = await response.Content.ReadAsStreamAsync(ct).ConfigureAwait(false);
        byte[] buffer = new byte[65536]; long count = 0; int read;
        while ((read = await input.ReadAsync(buffer.AsMemory(), ct).ConfigureAwait(false)) > 0)
        {
            count += read; StorePack.Require(count <= limit, "Download exceeds size limit");
            await output.WriteAsync(buffer.AsMemory(0, read), ct).ConfigureAwait(false);
        }
    }

    public async Task<IReadOnlyList<StoreEntry>> FetchIndex(CancellationToken ct = default)
    {
        using var data = new MemoryStream();
        await Download("index.json", data, 8 * 1024 * 1024, ct).ConfigureAwait(false);
        using var doc = JsonDocument.Parse(data.ToArray());
        StorePack.Require(doc.RootElement.GetProperty("schema").GetString() == "guo/store-index@1", "Unsupported store index");
        var result = new List<StoreEntry>();
        var seen = new HashSet<string>();
        foreach (var item in doc.RootElement.GetProperty("packs").EnumerateArray())
        {
            StorePack.Require(result.Count < 10000, "Index has too many packs");
            var manifest = StorePack.Parse(Encoding.UTF8.GetBytes(item.GetRawText()));
            string url = item.GetProperty("url").GetString();
            StorePack.Require(url == $"packs/{manifest.Id}/{manifest.Version}.zip", "Unexpected pack URL");
            Address(url);
            string digest = item.GetProperty("sha256").GetString(); StorePack.Digest(digest);
            long size = item.GetProperty("size").GetInt64();
            StorePack.Require(size > 0 && size <= StorePack.MaxZip && seen.Add(manifest.Id + "/" + manifest.Version), "Invalid or duplicate index entry");
            result.Add(new StoreEntry { Manifest = manifest, Url = url, Sha256 = digest, Size = size });
        }
        return result;
    }

    public async Task<byte[]> FetchPreview(StoreEntry entry, CancellationToken ct = default)
    {
        var manifest = entry.Manifest;
        using var data = new MemoryStream();
        await Download($"previews/{manifest.Id}/{manifest.Version}/{manifest.Preview}", data, 8 * 1024 * 1024, ct).ConfigureAwait(false);
        byte[] bytes = data.ToArray();
        StorePack.Require(Convert.ToHexString(System.Security.Cryptography.SHA256.HashData(bytes)).ToLowerInvariant()
            == manifest.Files[manifest.Preview], "Preview hash mismatch");
        return bytes;
    }

    // Refuse reparse points at every boundary, including parents of the root.
    internal static void NoLinks(string path)
    {
        for (var info = new DirectoryInfo(Path.GetFullPath(path)); info != null; info = info.Parent)
            if (info.Exists) StorePack.Require((info.Attributes & FileAttributes.ReparsePoint) == 0, "Store path contains a link");
        if (File.Exists(path)) StorePack.Require((File.GetAttributes(path) & FileAttributes.ReparsePoint) == 0, "Store file is a link");
    }

    private string Destination(string id, string version)
    {
        StorePack.Id(id); StorePack.Version(version);
        string destination = Path.Combine(_root, id, version);
        NoLinks(destination);
        return destination;
    }

    private FileStream Lock()
    {
        NoLinks(_root);
        Directory.CreateDirectory(_root);
        string path = Path.Combine(_root, ".store-lock"); NoLinks(path);
        return new FileStream(path, FileMode.OpenOrCreate, FileAccess.ReadWrite, FileShare.None);
    }

    public async Task<string> Install(StoreEntry entry, CancellationToken ct = default)
    {
        StorePack.Validate(entry.Manifest); StorePack.Digest(entry.Sha256);
        StorePack.Require(entry.Manifest.MinProfileVersion <= _profileVersion, "This pack requires a newer GUO profile version");
        StorePack.Require(entry.Size > 0 && entry.Size <= StorePack.MaxZip, "Invalid ZIP size");
        string destination = Destination(entry.Manifest.Id, entry.Manifest.Version);
        using var guard = Lock();
        if (Directory.Exists(destination))
        {
            var existing = ReadInstalled(destination, true);
            StorePack.Require(StorePack.Equivalent(existing, entry.Manifest), "Installed version differs; uninstall it first");
            return destination;
        }
        string stage = Path.Combine(_root, ".install-" + Guid.NewGuid().ToString("N"));
        string zip = stage + ".zip";
        try
        {
            using (var output = new FileStream(zip, FileMode.CreateNew, FileAccess.Write, FileShare.None))
                await Download(entry.Url, output, entry.Size, ct).ConfigureAwait(false);
            StorePack.Require(new FileInfo(zip).Length == entry.Size && StorePack.HashFile(zip) == entry.Sha256, "Downloaded ZIP hash/size mismatch");
            Directory.CreateDirectory(stage);
            var manifest = await Task.Run(() => StorePack.Extract(zip, stage), ct).ConfigureAwait(false);
            StorePack.Require(StorePack.Equivalent(manifest, entry.Manifest), "Index and pack manifest disagree");
            ct.ThrowIfCancellationRequested();
            NoLinks(destination);
            Directory.CreateDirectory(Path.GetDirectoryName(destination));
            Directory.Move(stage, destination);
            return destination;
        }
        finally
        {
            File.Delete(zip);
            if (Directory.Exists(stage)) DeleteTree(stage);
        }
    }

    private static StoreManifest ReadInstalled(string directory, bool hashes)
    {
        NoLinks(directory);
        string file = Path.Combine(directory, "manifest.json"); NoLinks(file);
        StorePack.Require(new FileInfo(file).Length <= StorePack.MaxManifest, "Installed manifest too large");
        var m = StorePack.Parse(File.ReadAllBytes(file));
        StorePack.Require(Path.GetFileName(directory) == m.Version && Path.GetFileName(Path.GetDirectoryName(directory)) == m.Id, "Installed manifest path mismatch");
        foreach (var (name, expected) in m.Files)
        {
            string path = Path.Combine(directory, name.Replace('/', Path.DirectorySeparatorChar)); NoLinks(path);
            StorePack.Require(File.Exists(path), "Installed payload missing");
            if (hashes) StorePack.Require(StorePack.HashFile(path) == expected, "Installed payload hash mismatch");
        }
        return m;
    }

    public IReadOnlyList<StoreManifest> Installed()
    {
        var result = new List<StoreManifest>(); NoLinks(_root);
        if (!Directory.Exists(_root)) return result;
        foreach (string id in Directory.EnumerateDirectories(_root))
        {
            if (Path.GetFileName(id).StartsWith('.')) continue;
            NoLinks(id);
            foreach (string version in Directory.EnumerateDirectories(id))
            {
                try { result.Add(ReadInstalled(version, false)); }
                catch (Exception e) when (e is IOException or JsonException or ArgumentException) { /* Broken packs never enter the picker. */ }
            }
        }
        return result;
    }

    /// <summary>Resolve an installed exact dependency closure without executing any content.</summary>
    public StoreVerifiedContent VerifyContent(string id, string version)
    {
        using var guard = Lock();
        var packs = new Dictionary<string, StoreVerifiedPack>(StringComparer.Ordinal);
        var visiting = new HashSet<string>(StringComparer.Ordinal);
        void Visit(string packId, string packVersion)
        {
            StorePack.Require(!visiting.Contains(packId), "Dependency cycle");
            if (packs.TryGetValue(packId, out var found))
            {
                StorePack.Require(found.Version == packVersion, "Dependency version conflict");
                return;
            }
            StorePack.Require(packs.Count + visiting.Count < 128, "Dependency closure too large");
            string directory = Destination(packId, packVersion);
            var m = ReadInstalled(directory, true);
            StorePack.Require(m.MinProfileVersion <= _profileVersion, "Dependency requires a newer profile");
            visiting.Add(packId);
            foreach (var dependency in m.Dependencies ?? new()) Visit(dependency.Key, dependency.Value);
            visiting.Remove(packId);
            packs.Add(packId, new StoreVerifiedPack(directory, m));
        }
        Visit(id, version);
        foreach (var pack in packs.Values)
            foreach (var component in pack.Manifest.Components ?? new())
                foreach (string reference in component.References ?? new())
                {
                    string[] parts = reference.Split(':');
                    StorePack.Require(packs.TryGetValue(parts[0], out var dependency)
                        && dependency.Manifest.Components != null && dependency.Manifest.Components.Any(c => c.Id == parts[1]), "Unresolved component reference");
                }
        return new StoreVerifiedContent(packs);
    }

    private static void DeleteTree(string directory)
    {
        NoLinks(directory);
        foreach (string child in Directory.EnumerateDirectories(directory)) DeleteTree(child);
        foreach (string file in Directory.EnumerateFiles(directory)) { NoLinks(file); File.Delete(file); }
        Directory.Delete(directory);
    }

    public void Uninstall(string id, string version)
    {
        string destination = Destination(id, version);
        using var guard = Lock();
        if (Directory.Exists(destination)) DeleteTree(destination);
        LastUninstallMessage = BackgroundRemoved?.Invoke(id, version) == true
            ? "Pack removed. Active background reset to built-in grey. Reopen Options to refresh backgrounds."
            : "Pack removed. Reopen Options to refresh backgrounds.";
    }

    public bool HasUpdate(StoreEntry entry) => entry.Manifest.MinProfileVersion <= _profileVersion &&
        Installed().Any(m => m.Id == entry.Manifest.Id && StorePack.Version(m.Version) < StorePack.Version(entry.Manifest.Version));

    public void Dispose() => _http.Dispose();
}
