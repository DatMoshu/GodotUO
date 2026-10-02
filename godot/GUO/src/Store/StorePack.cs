// SPDX-License-Identifier: BSD-2-Clause
// GUO addition: portable pack contract shared by the runtime and headless smoke.
using System;
using System.Collections.Generic;
using System.IO;
using System.IO.Compression;
using System.Linq;
using System.Security.Cryptography;
using System.Text;
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
    [JsonPropertyName("target")] public string Target { get; set; }
    [JsonPropertyName("dependencies")] public Dictionary<string, string> Dependencies { get; set; }
    [JsonPropertyName("components")] public List<StoreComponent> Components { get; set; }
}

internal sealed class StoreEntry
{
    public StoreManifest Manifest { get; init; }
    /// <summary>The v1 index's relative URL; null for a v2 entry, which has only <see cref="Urls"/>.</summary>
    public string Url { get; init; }
    public string Sha256 { get; init; }
    public long Size { get; init; }
    /// <summary>Where the ZIP can be fetched, tried in order; the hash and size pin it wherever it comes from.</summary>
    public IReadOnlyList<Uri> Urls { get; init; }
    public Uri PreviewUri { get; init; }
    /// <summary>The catalogue that listed it, and whether that catalogue's index was signed (ADR-0026).</summary>
    public string CatalogueTitle { get; init; }
    public string CatalogueUrl { get; init; }
    public bool Signed { get; init; }
    public string Provenance { get; init; }
}

internal static class StorePack
{
    public const long MaxZip = 512L * 1024 * 1024;
    public const long MaxFile = 256L * 1024 * 1024;
    public const long MaxTotal = 1024L * 1024 * 1024;
    public const int MaxManifest = 1024 * 1024;
    public const int MaxScriptBytes = 262144, MaxScriptChars = 65536;
    // A screensaver is played by the client from profile version 11 on.
    public const int ScreensaverMinProfile = 11;
    // Windows reserves COM1-9 and LPT1-9, and the superscript digits too (COM\u00b9 is a device).
    private static readonly HashSet<string> Devices = new(StringComparer.OrdinalIgnoreCase)
    { "con", "prn", "aux", "nul", "com1", "com2", "com3", "com4", "com5", "com6", "com7", "com8", "com9", "lpt1", "lpt2", "lpt3", "lpt4", "lpt5", "lpt6", "lpt7", "lpt8", "lpt9",
      "com\u00b9", "com\u00b2", "com\u00b3", "lpt\u00b9", "lpt\u00b2", "lpt\u00b3" };
    private static readonly HashSet<string> Extensions = new(StringComparer.OrdinalIgnoreCase)
    { ".png", ".jpg", ".jpeg", ".webp", ".ogv", ".ogg", ".wav", ".json", ".txt", ".gdshader", ".razor" };
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
            Require(!part.StartsWith('.'), "A name must have a stem, not only an extension");
            Require(!part.StartsWith("cliloc", StringComparison.OrdinalIgnoreCase) && !Regex.IsMatch(part, @"\.(mul|uop|idx|def)(\.|$)", RegexOptions.IgnoreCase), "UO client data is forbidden");
        }
    }

    /// <summary>
    /// The one case rule both validators apply (tools/asset_store/pack.py
    /// fold_keys is the same): Turkish dotted and dotless i are i, then each
    /// character folds by its simple one-to-one lower and upper mapping. Two
    /// names that share a key are one name.
    /// </summary>
    public static IEnumerable<string> FoldKeys(string name)
    {
        string n = name.Replace('ı', 'i').Replace('İ', 'i');
        return new[] { n.ToLowerInvariant(), n.ToUpperInvariant() };
    }

    private static bool AddFolded(HashSet<string> seen, string name)
    {
        var keys = FoldKeys(name).ToArray();
        if (keys.Any(seen.Contains)) return false;
        foreach (var k in keys) seen.Add(k);
        return true;
    }

    internal static void UniqueJson(JsonElement element)
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
        Require(!(bytes.Length >= 3 && bytes[0] == 0xEF && bytes[1] == 0xBB && bytes[2] == 0xBF), "Manifest has a byte order mark");
        StoreManifest m;
        // Everything the JSON layer throws is turned into the one exception the
        // installer's callers handle: a malformed manifest is a refused pack.
        try
        {
            using var doc = JsonDocument.Parse(bytes);
            UniqueJson(doc.RootElement);
            if (doc.RootElement.ValueKind == JsonValueKind.Object && doc.RootElement.TryGetProperty("schema", out var schema)
                && schema.ValueKind == JsonValueKind.String && schema.GetString() == "guo/store-pack@2")
            {
                var allowed = new HashSet<string>("schema id version kind target title author licence min_profile_version preview files dependencies components url sha256 size preview_url".Split(' '), StringComparer.Ordinal);
                Require(doc.RootElement.EnumerateObject().All(p => allowed.Contains(p.Name)), "Unknown content manifest field");
            }
            Require(doc.RootElement.ValueKind == JsonValueKind.Object && doc.RootElement.TryGetProperty("min_profile_version", out var minimum)
                && minimum.ValueKind == JsonValueKind.Number && minimum.TryGetInt32(out int min) && min >= 0, "Missing/invalid profile version");
            m = JsonSerializer.Deserialize<StoreManifest>(bytes);
        }
        catch (JsonException e)
        {
            throw new InvalidDataException("Manifest is not valid JSON: " + e.Message);
        }
        catch (InvalidOperationException e)
        {
            throw new InvalidDataException("Manifest has a value of the wrong type: " + e.Message);
        }
        Validate(m);
        return m;
    }

    public static void Validate(StoreManifest m)
    {
        Require(m != null && m.Schema is "guo/store-pack@1" or "guo/store-pack@2", "Unsupported pack schema");
        Id(m.Id); Version(m.Version);
        Require(m.Schema == "guo/store-pack@2" ? m.Kind == "content" : m.Kind is "background" or "theme" or "sound" or "profile-preset" or "screensaver" or "postfx" or "razor-script", "Unsupported pack kind");
        Require(m.Licence != null && Licences.Contains(m.Licence), "Licence is not allowed");
        Require(!string.IsNullOrWhiteSpace(m.Title) && m.Title.Length <= 200 && !string.IsNullOrWhiteSpace(m.Author) && m.Author.Length <= 200, "Invalid title/author");
        Require(!m.Title.Any(c => c < 32 || c == 127) && !m.Author.Any(c => c < 32 || c == 127), "Control characters in title/author");
        Require(m.MinProfileVersion >= 0, "Invalid profile version");
        Require(m.Files != null && m.Files.Count is > 0 and <= 1024, "Invalid files map");
        var names = new HashSet<string>(StringComparer.Ordinal);
        foreach (var (name, hash) in m.Files)
        {
            SafePath(name); Digest(hash);
            Require(!name.Equals("manifest.json", StringComparison.OrdinalIgnoreCase) && Extensions.Contains(Path.GetExtension(name)) && AddFolded(names, name), "Invalid or duplicate payload");
        }
        foreach (string name in m.Files.Keys)
        {
            string[] parts = name.Split('/');
            for (int i = 1; i < parts.Length; i++) Require(!FoldKeys(string.Join('/', parts.Take(i))).Any(names.Contains), "File/directory collision");
        }
        Require(m.Preview != null && m.Files.ContainsKey(m.Preview) && Images.Contains(Path.GetExtension(m.Preview)), "Preview must name a declared image");
        Require(m.Licence == "CC0-1.0" || m.Files.ContainsKey("LICENSE.txt"), "Attribution requires LICENSE.txt");
        bool scripts = m.Files.Keys.Any(IsScript);
        if (m.Schema == "guo/store-pack@2")
        {
            StoreContent.Validate(m);
            var entries = new HashSet<string>(m.Components.Where(c => c.Type == "script").Select(c => c.Entry), StringComparer.Ordinal);
            Require(entries.All(IsScript) && m.Files.Keys.Where(IsScript).All(entries.Contains),
                "Every Razor payload must be a declared script component entry");
        }
        else Require(m.Kind == "razor-script" ? scripts : !scripts,
            "Razor scripts require their own kind and at least one .razor file");
        // Screen-effect packs (ADR-0023): presets and their shaders; shader code
        // is accepted only in this kind.
        if (m.Kind == "postfx")
        {
            Require(m.Files.Keys.Any(p => Path.GetExtension(p).Equals(".json", StringComparison.OrdinalIgnoreCase)),
                "A postfx pack has at least one preset (.json)");
        }
        else
        {
            Require(!m.Files.Keys.Any(p => Path.GetExtension(p).Equals(".gdshader", StringComparison.OrdinalIgnoreCase)),
                "Shader files are only allowed in a postfx pack");
        }
        if (m.Kind == "screensaver")
        {
            Require(m.Files.Keys.Count(p => Path.GetExtension(p).Equals(".ogv", StringComparison.OrdinalIgnoreCase)) == 1, "A screensaver has exactly one .ogv loop");
            Require(m.MinProfileVersion >= ScreensaverMinProfile, "A screensaver needs min_profile_version 11 or later");
        }
        if (m.Schema == "guo/store-pack@2") StoreContent.Validate(m);
    }

    /// <summary>The loop a screensaver pack plays.</summary>
    public static string ScreensaverLoop(StoreManifest m) =>
        m.Files.Keys.First(p => Path.GetExtension(p).Equals(".ogv", StringComparison.OrdinalIgnoreCase));

    public static bool IsScript(string path) => Path.GetExtension(path).Equals(".razor", StringComparison.OrdinalIgnoreCase);

    public static string ScriptText(byte[] bytes)
    {
        Require(bytes.Length <= MaxScriptBytes, "Script exceeds byte limit");
        string text;
        int start = bytes.Length >= 3 && bytes[0] == 0xEF && bytes[1] == 0xBB && bytes[2] == 0xBF ? 3 : 0;
        try { text = new UTF8Encoding(false, true).GetString(bytes, start, bytes.Length - start); }
        catch (DecoderFallbackException) { throw new InvalidDataException("Script is not valid UTF-8"); }
        Require(!string.IsNullOrWhiteSpace(text) && text.Length <= MaxScriptChars, "Script is empty or exceeds character limit");
        Require(!text.Any(c => c < 32 && c != '\t' && c != '\r' && c != '\n' || c == 127), "Script contains control characters");
        return text;
    }

    public static string Hash(Stream input) => Convert.ToHexString(SHA256.HashData(input)).ToLowerInvariant();
    public static string HashFile(string path) { using var input = File.OpenRead(path); return Hash(input); }

    public static StoreManifest Extract(string zipPath, string staging)
    {
        Require(new FileInfo(zipPath).Length <= MaxZip, "ZIP too large");
        // A non-ASCII entry name must carry the ZIP's UTF-8 flag (pack.py checks
        // the same). .NET 8 does not expose the flag, so read it here.
        var flags = EntryNameFlags(zipPath);
        using var zip = ZipFile.OpenRead(zipPath);
        Require(flags.Count == zip.Entries.Count, "ZIP central directory does not match its entries");
        for (int i = 0; i < flags.Count; i++)
            Require(flags[i].Ascii || flags[i].Utf8, "A non-ASCII entry name without the UTF-8 flag");
        Require(zip.Entries.Count is > 1 and <= 1025, "Invalid ZIP entry count");
        var names = new HashSet<string>(StringComparer.Ordinal);
        long total = 0;
        foreach (var entry in zip.Entries)
        {
            SafePath(entry.FullName);
            int type = (entry.ExternalAttributes >> 16) & 0xf000;
            Require(type is 0 or 0x8000 && !entry.FullName.EndsWith('/') && AddFolded(names, entry.FullName), "Duplicate or non-file ZIP entry");
            Require(!entry.IsEncrypted, "Encrypted ZIP entry");
            Require(entry.Length <= (entry.FullName == "manifest.json" ? MaxManifest : MaxFile), "ZIP entry too large");
            if (IsScript(entry.FullName)) Require(entry.Length <= MaxScriptBytes, "Script exceeds byte limit");
            total = checked(total + entry.Length);
            Require(total <= MaxTotal + MaxManifest, "Expanded ZIP too large");
        }
        var manifestEntry = zip.GetEntry("manifest.json");
        Require(manifestEntry != null, "Root manifest missing");
        byte[] raw;
        using (var source = manifestEntry.Open())
        using (var memory = new MemoryStream()) { CopyLimited(source, memory, MaxManifest); raw = memory.ToArray(); }
        var m = Parse(raw);
        var undeclared = zip.Entries.Where(e => e.FullName != "manifest.json" && !m.Files.ContainsKey(e.FullName)).Select(e => e.FullName).ToList();
        Require(zip.Entries.Count == m.Files.Count + 1 && undeclared.Count == 0,
            "Undeclared or missing payload" + (undeclared.Count > 0 ? ": " + string.Join(", ", undeclared) : $" ({zip.Entries.Count - 1} entries, {m.Files.Count} declared)"));
        foreach (var (name, expected) in m.Files)
        {
            string target = Path.Combine(staging, name.Replace('/', Path.DirectorySeparatorChar));
            Directory.CreateDirectory(Path.GetDirectoryName(target));
            using (var source = zip.GetEntry(name).Open())
            using (var output = new FileStream(target, FileMode.CreateNew)) CopyLimited(source, output, IsScript(name) ? MaxScriptBytes : MaxFile);
            Require(HashFile(target) == expected, "Payload hash mismatch: " + name);
            if (IsScript(name)) ScriptText(File.ReadAllBytes(target));
        }
        File.WriteAllBytes(Path.Combine(staging, "manifest.json"), raw);
        return m;
    }

    /// <summary>
    /// For each central-directory entry, in order: whether its raw name is
    /// ASCII and whether it carries the UTF-8 flag (general purpose bit 11).
    /// Bounded: packs are at most 512 MB with at most 1025 entries, so no ZIP64.
    /// </summary>
    public static List<(bool Ascii, bool Utf8)> EntryNameFlags(string zipPath)
    {
        byte[] tail;
        long length;
        using (var f = File.OpenRead(zipPath))
        {
            length = f.Length;
            int take = (int)Math.Min(length, 22 + 65535);
            tail = new byte[take];
            f.Seek(length - take, SeekOrigin.Begin);
            f.ReadExactly(tail);
        }

        int eocd = -1;
        for (int i = tail.Length - 22; i >= 0; i--)
            if (BitConverter.ToUInt32(tail, i) == 0x06054B50) { eocd = i; break; }
        Require(eocd >= 0, "ZIP end record missing");
        int count = BitConverter.ToUInt16(tail, eocd + 10);
        uint size = BitConverter.ToUInt32(tail, eocd + 12), offset = BitConverter.ToUInt32(tail, eocd + 16);
        Require(count != 0xFFFF && size != 0xFFFFFFFF && offset != 0xFFFFFFFF && offset + (long)size <= length, "ZIP64 or damaged central directory");
        byte[] cd = new byte[size];
        using (var f = File.OpenRead(zipPath)) { f.Seek(offset, SeekOrigin.Begin); f.ReadExactly(cd); }
        var result = new List<(bool, bool)>();
        for (int at = 0, n = 0; n < count; n++)
        {
            Require(at + 46 <= cd.Length && BitConverter.ToUInt32(cd, at) == 0x02014B50, "Damaged ZIP central directory");
            int flags = BitConverter.ToUInt16(cd, at + 8);
            int nameLength = BitConverter.ToUInt16(cd, at + 28), extra = BitConverter.ToUInt16(cd, at + 30), comment = BitConverter.ToUInt16(cd, at + 32);
            Require(at + 46 + nameLength <= cd.Length, "Damaged ZIP central directory");
            bool ascii = true;
            for (int k = 0; k < nameLength; k++) ascii &= cd[at + 46 + k] < 0x80;
            result.Add((ascii, (flags & 0x800) != 0));
            at += 46 + nameLength + extra + comment;
        }

        return result;
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
        a.Schema == b.Schema && a.Id == b.Id && a.Version == b.Version && a.Kind == b.Kind && a.Title == b.Title && a.Author == b.Author && a.Licence == b.Licence && a.MinProfileVersion == b.MinProfileVersion && a.Preview == b.Preview && a.Files.Count == b.Files.Count && a.Files.All(p => b.Files.TryGetValue(p.Key, out var hash) && p.Value == hash) && StoreContent.Equivalent(a, b);
}
