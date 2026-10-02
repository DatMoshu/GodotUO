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
    // Process-local lifecycle signal; consumers filter by id/version and reverify before use.
    public static event Action<string, string, string> PackChanged;
    private static void Changed(string operation, string id, string version)
    {
        var handlers = PackChanged;
        if (handlers == null) return;
        foreach (Action<string, string, string> handler in handlers.GetInvocationList())
            try { handler(operation, id, version); }
            catch (Exception e) { System.Diagnostics.Trace.TraceError("Store change listener: " + e.Message); }
    }
    private readonly HttpClient _http = new(new HttpClientHandler { AllowAutoRedirect = false });
    // Hash-pinned downloads from a signed index may follow a mirror's redirect (GitHub release
    // assets redirect to their storage host); .NET never follows HTTPS to plain HTTP.
    private readonly HttpClient _pinned = new(new HttpClientHandler { AllowAutoRedirect = true, MaxAutomaticRedirections = 5 });
    private readonly string _root;
    private readonly int _profileVersion;
    private readonly Uri _base;
    public string Root => _root;
    public Func<string, string, bool> BackgroundRemoved { get; set; }
    public string LastUninstallMessage { get; private set; } = "Pack removed. Reopen Options to refresh backgrounds.";
    /// <summary>Which catalogue keys are approved. Without one, a signed (v2) index is refused.</summary>
    public StoreTrust Trust { get; set; }
    /// <summary>The catalogue the last <see cref="FetchIndex"/> read: its title, and whether it was signed.</summary>
    public string CatalogueTitle { get; private set; }
    public bool CatalogueSigned { get; private set; }
    public string BaseUrl => _base.AbsoluteUri;

    public StoreClient(string storeUrl, string installRoot, int profileVersion)
    {
        _base = new Uri(StoreAddress.Normalize(storeUrl), UriKind.Absolute);
        _root = Path.GetFullPath(installRoot);
        _profileVersion = profileVersion;
        _http.Timeout = TimeSpan.FromMinutes(5);
        _pinned.Timeout = TimeSpan.FromMinutes(30);
    }

    private Uri Address(string relative)
    {
        StorePack.SafePath(relative);
        StorePack.Require(!relative.Contains('%'), "Encoded URLs are not allowed");
        var uri = new Uri(_base, relative);
        StorePack.Require(uri.Scheme == _base.Scheme && uri.Authority == _base.Authority && uri.AbsolutePath.StartsWith(_base.AbsolutePath, StringComparison.Ordinal), "Asset URL escapes store");
        return uri;
    }

    private Task Download(string relative, Stream output, long limit, CancellationToken ct) => Download(_http, Address(relative), output, limit, ct);

    /// <summary>A URL from a signed index: relative ones stay under the catalogue, absolute ones are HTTPS
    /// (or plain HTTP on loopback or the LAN).</summary>
    private Uri Resolve(string url)
    {
        StorePack.Require(!string.IsNullOrEmpty(url) && url.Length <= 2048 && !url.Any(c => c <= ' '), "Invalid URL in index");
        if (!url.Contains("://", StringComparison.Ordinal)) return Address(url);
        var uri = new Uri(url, UriKind.Absolute);
        StorePack.Require(StoreTrust.AllowedRemote(uri), "Pack URLs must be HTTPS, or HTTP on this computer or the LAN: " + url);
        return uri;
    }

    private static async Task Download(HttpClient http, Uri uri, Stream output, long limit, CancellationToken ct)
    {
        using var response = await http.GetAsync(uri, HttpCompletionOption.ResponseHeadersRead, ct).ConfigureAwait(false);
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
        byte[] raw = data.ToArray();
        using var doc = JsonDocument.Parse(raw);
        string schema = doc.RootElement.GetProperty("schema").GetString();
        if (schema == "guo/store-index@2")
        {
            using var signature = new MemoryStream();
            await Download("index.json.sig", signature, 1024, ct).ConfigureAwait(false);
            return ReadSigned(doc.RootElement, raw, Encoding.ASCII.GetString(signature.ToArray()));
        }
        StorePack.Require(schema == "guo/store-index@1", "Unsupported store index");
        CatalogueTitle = _base.Host; CatalogueSigned = false;
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
            result.Add(new StoreEntry
            {
                Manifest = manifest, Url = url, Sha256 = digest, Size = size, Urls = new[] { Address(url) },
                PreviewUri = Address($"previews/{manifest.Id}/{manifest.Version}/{manifest.Preview}"),
                CatalogueTitle = _base.Host, CatalogueUrl = _base.AbsoluteUri, Signed = false,
            });
        }
        return result;
    }

    /// <summary>guo/store-index@2 (ADR-0026): the signature over the exact bytes, the key's approval, the
    /// expiry, then every entry. Nothing is listed unless all of it holds.</summary>
    private IReadOnlyList<StoreEntry> ReadSigned(JsonElement root, byte[] raw, string signatureText)
    {
        byte[] key = Ed25519.Decode(root.GetProperty("key").GetString(), 32);
        byte[] signature = Ed25519.Decode(signatureText, 64);
        StorePack.Require(Ed25519.Verify(key, raw, signature), "The catalogue's signature does not verify. It may have been tampered with, or is mid-update: try again.");
        var catalogue = root.GetProperty("catalogue");
        string id = catalogue.GetProperty("id").GetString(); StorePack.Id(id);
        string title = catalogue.GetProperty("title").GetString();
        StorePack.Require(!string.IsNullOrWhiteSpace(title) && title.Length <= 200 && !title.Any(char.IsControl), "Invalid catalogue title");
        long sequence = root.GetProperty("sequence").GetInt64();
        if (root.TryGetProperty("expires", out var expires))
            StorePack.Require(DateTime.ParseExact(expires.GetString(), "yyyy-MM-ddTHH:mm:ssZ", System.Globalization.CultureInfo.InvariantCulture,
                System.Globalization.DateTimeStyles.AdjustToUniversal | System.Globalization.DateTimeStyles.AssumeUniversal) >= DateTime.UtcNow,
                "This catalogue's index has expired; its owner has not refreshed it.");
        StorePack.Require(Trust != null, "No catalogue trust list; signed catalogues cannot be checked.");
        Trust.Evaluate(_base.AbsoluteUri, id, title, key, sequence);
        CatalogueTitle = title; CatalogueSigned = true;
        var result = new List<StoreEntry>();
        var seen = new HashSet<string>();
        foreach (var item in root.GetProperty("packs").EnumerateArray())
        {
            StorePack.Require(result.Count < 10000, "Index has too many packs");
            var manifest = StorePack.Parse(Encoding.UTF8.GetBytes(item.GetProperty("manifest").GetRawText()));
            string digest = item.GetProperty("sha256").GetString(); StorePack.Digest(digest);
            long size = item.GetProperty("size").GetInt64();
            StorePack.Require(size > 0 && size <= StorePack.MaxZip && seen.Add(manifest.Id + "/" + manifest.Version), "Invalid or duplicate index entry");
            var urls = item.GetProperty("urls").EnumerateArray().Select(u => Resolve(u.GetString())).ToArray();
            StorePack.Require(urls.Length is > 0 and <= 16, "An index entry needs one to sixteen URLs");
            string provenance = item.TryGetProperty("provenance", out var p) ? p.GetString() ?? "" : "";
            StorePack.Require(provenance.Length <= 500 && !provenance.Any(char.IsControl), "Invalid provenance");
            result.Add(new StoreEntry
            {
                Manifest = manifest, Sha256 = digest, Size = size, Urls = urls,
                PreviewUri = Resolve(item.GetProperty("preview_url").GetString()),
                CatalogueTitle = title, CatalogueUrl = _base.AbsoluteUri, Signed = true, Provenance = provenance,
            });
        }
        return result;
    }

    public async Task<byte[]> FetchPreview(StoreEntry entry, CancellationToken ct = default)
    {
        var manifest = entry.Manifest;
        using var data = new MemoryStream();
        await Download(entry.Signed ? _pinned : _http, entry.PreviewUri ?? Address($"previews/{manifest.Id}/{manifest.Version}/{manifest.Preview}"),
            data, 8 * 1024 * 1024, ct).ConfigureAwait(false);
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
            // Each URL in turn until one serves exactly the listed bytes; a mirror that is down
            // or serves something else costs only a retry.
            var errors = new List<string>();
            foreach (var uri in entry.Urls ?? new[] { Address(entry.Url) })
            {
                try
                {
                    using (var output = new FileStream(zip, FileMode.Create, FileAccess.Write, FileShare.None))
                        await Download(entry.Signed ? _pinned : _http, uri, output, entry.Size, ct).ConfigureAwait(false);
                    if (new FileInfo(zip).Length == entry.Size && StorePack.HashFile(zip) == entry.Sha256) { errors = null; break; }
                    errors.Add(uri.Host + ": hash/size mismatch");
                }
                catch (Exception e) when (e is HttpRequestException or IOException or InvalidDataException
                    || e is TaskCanceledException && !ct.IsCancellationRequested)
                {
                    errors.Add(uri.Host + ": " + e.Message);
                }
            }
            StorePack.Require(errors == null, "Downloaded ZIP hash/size mismatch (" + string.Join("; ", errors ?? new List<string>()) + ")");
            Directory.CreateDirectory(stage);
            var manifest = await Task.Run(() => StorePack.Extract(zip, stage), ct).ConfigureAwait(false);
            StorePack.Require(StorePack.Equivalent(manifest, entry.Manifest), "Index and pack manifest disagree");
            ct.ThrowIfCancellationRequested();
            NoLinks(destination);
            Directory.CreateDirectory(Path.GetDirectoryName(destination));
            Directory.Move(stage, destination);
            guard.Dispose(); // Reconciliation may open its own verification lock.
            Changed("install", entry.Manifest.Id, entry.Manifest.Version);
            return destination;
        }
        finally
        {
            File.Delete(zip);
            if (Directory.Exists(stage)) DeleteTree(stage);
        }
    }

    public async Task<string> InstallWithDependencies(StoreEntry root, IReadOnlyList<StoreEntry> catalogue, CancellationToken ct = default)
    {
        var selected = new Dictionary<string, StoreEntry>(StringComparer.Ordinal);
        var visiting = new HashSet<string>(StringComparer.Ordinal);
        var ordered = new List<StoreEntry>();
        void Plan(StoreEntry entry)
        {
            StorePack.Validate(entry.Manifest);
            string id = entry.Manifest.Id;
            StorePack.Require(!visiting.Contains(id), "Dependency cycle");
            if (selected.TryGetValue(id, out var previous))
            {
                StorePack.Require(previous.Manifest.Version == entry.Manifest.Version, "Dependency version conflict");
                return;
            }
            StorePack.Require(selected.Count < 128 && entry.Manifest.MinProfileVersion <= _profileVersion, "Dependency closure too large or incompatible");
            selected.Add(id, entry); visiting.Add(id);
            foreach (var dependency in entry.Manifest.Dependencies ?? new())
            {
                var matches = catalogue.Where(e => e.Manifest.Id == dependency.Key && e.Manifest.Version == dependency.Value).ToArray();
                StorePack.Require(matches.Length == 1, "Required dependency unavailable: " + dependency.Key + "@" + dependency.Value);
                Plan(matches[0]);
            }
            visiting.Remove(id); ordered.Add(entry);
        }
        Plan(root); // Reject invalid plans before downloading or installing anything.
        string result = null;
        foreach (var entry in ordered) result = await Install(entry, ct).ConfigureAwait(false);
        if (root.Manifest.Schema == "guo/store-pack@2") VerifyContent(root.Manifest.Id, root.Manifest.Version);
        return result;
    }

    private static StoreManifest ReadInstalled(string directory, bool hashes)
    {
        NoLinks(directory);
        string file = Path.Combine(directory, "manifest.json"); NoLinks(file);
        StorePack.Require(new FileInfo(file).Length <= StorePack.MaxManifest, "Installed manifest too large");
        var m = StorePack.Parse(File.ReadAllBytes(file));
        StorePack.Require(Path.GetFileName(directory) == m.Version && Path.GetFileName(Path.GetDirectoryName(directory)) == m.Id, "Installed manifest path mismatch");
        long total = 0;
        foreach (var (name, expected) in m.Files)
        {
            string path = Path.Combine(directory, name.Replace('/', Path.DirectorySeparatorChar)); NoLinks(path);
            StorePack.Require(File.Exists(path), "Installed payload missing");
            long size = new FileInfo(path).Length; total += size;
            StorePack.Require(size <= StorePack.MaxFile && total <= StorePack.MaxTotal, "Installed payload exceeds limits");
            if (hashes) StorePack.Require(StorePack.HashFile(path) == expected, "Installed payload hash mismatch");
        }
        return m;
    }

    /// <summary>Read a declared installed file, checking its snapshot metadata and hash again.
    /// The store lock prevents install/remove racing a script preview or personal-copy import.</summary>
    public byte[] ReadVerifiedPayload(StoreManifest expected, string name, int limit)
    {
        StorePack.Validate(expected);
        StorePack.SafePath(name);
        using var guard = Lock();
        string directory = Destination(expected.Id, expected.Version);
        var manifest = ReadInstalled(directory, false);
        StorePack.Require(StorePack.Equivalent(expected, manifest), "Installed pack changed; reopen the library");
        StorePack.Require(manifest.Files.TryGetValue(name, out string digest), "Undeclared pack file");
        string path = Path.Combine(directory, name.Replace('/', Path.DirectorySeparatorChar));
        NoLinks(path);
        using var input = File.OpenRead(path);
        StorePack.Require(input.Length <= limit, "Pack file exceeds read limit");
        using var bytes = new MemoryStream();
        StorePack.CopyLimited(input, bytes, limit);
        byte[] result = bytes.ToArray();
        StorePack.Require(Convert.ToHexString(System.Security.Cryptography.SHA256.HashData(result)).ToLowerInvariant() == digest,
            "Installed payload hash mismatch: " + name);
        return result;
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
        return VerifyContentLocked(id, version);
    }

    internal IDisposable AcquireContentLock() => Lock();

    // Caller holds AcquireContentLock, including during the resulting pointer mutation.
    internal StoreVerifiedContent VerifyContentLocked(string id, string version)
    {
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
        string activePath = Path.Combine(_root, ".active-content.json");
        if (File.Exists(activePath))
        {
            var selected = StoreContentLock.Read(activePath);
            var closure = selected.VerifySnapshot(VerifyContentLocked(selected.Pack, selected.Version));
            StorePack.Require(!closure.Packs.TryGetValue(id, out var pack) || pack.Version != version,
                "This pack is selected for startup. Select original assets or another deployment before uninstalling it.");
        }
        if (Directory.Exists(destination)) DeleteTree(destination);
        guard.Dispose();
        Changed("uninstall", id, version);
        LastUninstallMessage = BackgroundRemoved?.Invoke(id, version) == true
            ? "Pack removed. Active background reset to built-in grey. Reopen Options to refresh backgrounds."
            : "Pack removed. Reopen Options to refresh backgrounds.";
    }

    public bool HasUpdate(StoreEntry entry) => entry.Manifest.MinProfileVersion <= _profileVersion &&
        Installed().Any(m => m.Id == entry.Manifest.Id && StorePack.Version(m.Version) < StorePack.Version(entry.Manifest.Version));

    public void Dispose() { _http.Dispose(); _pinned.Dispose(); }
}
