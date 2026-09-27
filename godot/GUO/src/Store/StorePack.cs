// SPDX-License-Identifier: BSD-2-Clause
// GUO addition: portable pack contract shared by the runtime and headless smoke.
using System;
using System.Collections.Generic;
using System.IO;
using System.IO.Compression;
using System.Linq;
using System.Security.Cryptography;
using System.Text.Json;
using System.Text.Json.Serialization;
using System.Text.RegularExpressions;

namespace GUO.Store;

internal sealed class StoreManifest
{
    [JsonPropertyName("schema")] public string Schema { get; set; }
    [JsonPropertyName("id")] public string Id { get; set; }
    [JsonPropertyName("version")] public string Version { get; set; }
    [JsonPropertyName("kind")] public string Kind { get; set; }
    [JsonPropertyName("title")] public string Title { get; set; }
    [JsonPropertyName("author")] public string Author { get; set; }
    [JsonPropertyName("licence")] public string Licence { get; set; }
    [JsonPropertyName("min_profile_version")] public int MinProfileVersion { get; set; }
    [JsonPropertyName("preview")] public string Preview { get; set; }
    [JsonPropertyName("files")] public Dictionary<string, string> Files { get; set; }
}

internal sealed class StoreEntry
{
    public StoreManifest Manifest { get; init; }
    public string Url { get; init; }
    public string Sha256 { get; init; }
    public long Size { get; init; }
}

internal static class StorePack
{
    public const long MaxZip = 512L * 1024 * 1024;
    public const long MaxFile = 256L * 1024 * 1024;
    public const long MaxTotal = 1024L * 1024 * 1024;
    public const int MaxManifest = 1024 * 1024;
    private static readonly HashSet<string> Devices = new(StringComparer.OrdinalIgnoreCase)
    { "con", "prn", "aux", "nul", "com1", "com2", "com3", "com4", "com5", "com6", "com7", "com8", "com9", "lpt1", "lpt2", "lpt3", "lpt4", "lpt5", "lpt6", "lpt7", "lpt8", "lpt9" };
    private static readonly HashSet<string> Extensions = new(StringComparer.OrdinalIgnoreCase)
    { ".png", ".jpg", ".jpeg", ".webp", ".ogv", ".ogg", ".wav", ".json", ".txt" };
    private static readonly HashSet<string> Images = new(StringComparer.OrdinalIgnoreCase) { ".png", ".jpg", ".jpeg", ".webp" };
    private static readonly HashSet<string> Licences = new(StringComparer.Ordinal)
    { "CC0-1.0", "CC-BY-4.0", "CC-BY-SA-4.0", "MIT", "BSD-2-Clause", "BSD-3-Clause", "Apache-2.0" };

    public static void Require(bool condition, string message)
    {
        if (!condition) throw new InvalidDataException(message);
    }

    public static void Id(string id) => Require(id != null && Regex.IsMatch(id, "\\A[a-z0-9][a-z0-9-]{0,63}\\z") && !Devices.Contains(id), "Invalid pack id");

    public static Version Version(string value)
    {
        Require(value != null && Regex.IsMatch(value, "\\A(0|[1-9][0-9]*)\\.(0|[1-9][0-9]*)\\.(0|[1-9][0-9]*)\\z"), "Invalid version");
        Require(System.Version.TryParse(value, out var version), "Version component too large");
        return version;
    }

    public static void Digest(string value) => Require(value != null && Regex.IsMatch(value, "\\A[0-9a-f]{64}\\z"), "Invalid SHA-256");

    public static void SafePath(string path)
    {
        Require(!string.IsNullOrEmpty(path) && path.Length <= 240, "Invalid payload path");
        Require(!path.Any(c => c < 32 || "\\:<>\"|?*".Contains(c)), "Unsafe payload path");
        foreach (string part in path.Split('/'))
        {
            Require(part.Length > 0 && part.Length <= 100 && part != "." && part != ".." && !part.EndsWith('.') && !part.EndsWith(' ') && !Devices.Contains(part.Split('.')[0]), "Unsafe path component");
            Require(!part.StartsWith("cliloc", StringComparison.OrdinalIgnoreCase) && !Regex.IsMatch(part, @"\.(mul|uop|idx|def)(\.|$)", RegexOptions.IgnoreCase), "UO client data is forbidden");
        }
    }

    private static void UniqueJson(JsonElement element)
    {
        if (element.ValueKind == JsonValueKind.Object)
        {
            var names = new HashSet<string>(StringComparer.Ordinal);
            foreach (var property in element.EnumerateObject())
            {
                Require(names.Add(property.Name), "Duplicate JSON key");
                UniqueJson(property.Value);
            }
        }
        else if (element.ValueKind == JsonValueKind.Array)
            foreach (var child in element.EnumerateArray()) UniqueJson(child);
    }

    public static StoreManifest Parse(byte[] bytes)
    {
        Require(bytes.Length <= MaxManifest, "Manifest too large");
        using var doc = JsonDocument.Parse(bytes);
        UniqueJson(doc.RootElement);
        Require(doc.RootElement.ValueKind == JsonValueKind.Object && doc.RootElement.TryGetProperty("min_profile_version", out var minimum) && minimum.TryGetInt32(out int min) && min >= 0, "Missing/invalid profile version");
        var m = JsonSerializer.Deserialize<StoreManifest>(bytes);
        Validate(m);
        return m;
    }

    public static void Validate(StoreManifest m)
    {
        Require(m != null && m.Schema == "guo/store-pack@1", "Unsupported pack schema");
        Id(m.Id); Version(m.Version);
        Require(m.Kind is "background" or "theme" or "sound" or "profile-preset", "Unsupported pack kind; art overrides are disabled");
        Require(m.Licence != null && Licences.Contains(m.Licence), "Licence is not allowed");
        Require(!string.IsNullOrWhiteSpace(m.Title) && m.Title.Length <= 200 && !string.IsNullOrWhiteSpace(m.Author) && m.Author.Length <= 200, "Invalid title/author");
        Require(m.MinProfileVersion >= 0, "Invalid profile version");
        Require(m.Files != null && m.Files.Count is > 0 and <= 1024, "Invalid files map");
        var names = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        foreach (var (name, hash) in m.Files)
        {
            SafePath(name); Digest(hash);
            Require(!name.Equals("manifest.json", StringComparison.OrdinalIgnoreCase) && Extensions.Contains(Path.GetExtension(name)) && names.Add(name), "Invalid or duplicate payload");
        }
        foreach (string name in names)
        {
            string[] parts = name.Split('/');
            for (int i = 1; i < parts.Length; i++) Require(!names.Contains(string.Join('/', parts.Take(i))), "File/directory collision");
        }
        Require(m.Preview != null && m.Files.ContainsKey(m.Preview) && Images.Contains(Path.GetExtension(m.Preview)), "Preview must name a declared image");
        Require(m.Licence == "CC0-1.0" || m.Files.ContainsKey("LICENSE.txt"), "Attribution requires LICENSE.txt");
    }

    public static string Hash(Stream input) => Convert.ToHexString(SHA256.HashData(input)).ToLowerInvariant();
    public static string HashFile(string path) { using var input = File.OpenRead(path); return Hash(input); }

    public static StoreManifest Extract(string zipPath, string staging)
    {
        Require(new FileInfo(zipPath).Length <= MaxZip, "ZIP too large");
        using var zip = ZipFile.OpenRead(zipPath);
        Require(zip.Entries.Count is > 1 and <= 1025, "Invalid ZIP entry count");
        var names = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        long total = 0;
        foreach (var entry in zip.Entries)
        {
            SafePath(entry.FullName);
            int type = (entry.ExternalAttributes >> 16) & 0xf000;
            Require(type is 0 or 0x8000 && !entry.FullName.EndsWith('/') && names.Add(entry.FullName), "Duplicate or non-file ZIP entry");
            Require(entry.Length <= (entry.FullName == "manifest.json" ? MaxManifest : MaxFile), "ZIP entry too large");
            total = checked(total + entry.Length);
            Require(total <= MaxTotal + MaxManifest, "Expanded ZIP too large");
        }
        var manifestEntry = zip.GetEntry("manifest.json");
        Require(manifestEntry != null, "Root manifest missing");
        byte[] raw;
        using (var source = manifestEntry.Open())
        using (var memory = new MemoryStream()) { CopyLimited(source, memory, MaxManifest); raw = memory.ToArray(); }
        var m = Parse(raw);
        Require(zip.Entries.Count == m.Files.Count + 1 && zip.Entries.All(e => e.FullName == "manifest.json" || m.Files.ContainsKey(e.FullName)), "Undeclared or missing payload");
        foreach (var (name, expected) in m.Files)
        {
            string target = Path.Combine(staging, name.Replace('/', Path.DirectorySeparatorChar));
            Directory.CreateDirectory(Path.GetDirectoryName(target));
            using (var source = zip.GetEntry(name).Open())
            using (var output = new FileStream(target, FileMode.CreateNew)) CopyLimited(source, output, MaxFile);
            Require(HashFile(target) == expected, "Payload hash mismatch: " + name);
        }
        File.WriteAllBytes(Path.Combine(staging, "manifest.json"), raw);
        return m;
    }

    public static void CopyLimited(Stream source, Stream destination, long limit)
    {
        byte[] buffer = new byte[65536]; long count = 0; int read;
        while ((read = source.Read(buffer)) != 0)
        {
            count += read; Require(count <= limit, "Content exceeds size limit");
            destination.Write(buffer, 0, read);
        }
    }

    public static bool Equivalent(StoreManifest a, StoreManifest b) =>
        a.Schema == b.Schema && a.Id == b.Id && a.Version == b.Version && a.Kind == b.Kind && a.Title == b.Title && a.Author == b.Author && a.Licence == b.Licence && a.MinProfileVersion == b.MinProfileVersion && a.Preview == b.Preview && a.Files.Count == b.Files.Count && a.Files.All(p => b.Files.TryGetValue(p.Key, out var hash) && p.Value == hash);
}
