// SPDX-License-Identifier: BSD-2-Clause
#if GUO_LABS || GUO_PAGING_TESTS
using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text.Json;

namespace GUO.Labs.Paging;

/// <summary>
/// One logical page of a local page pack: a stable identity mapped to one
/// native facet ID, with its three MUL files and their expected hashes.
/// </summary>
internal sealed class PageEntry
{
    internal string Id { get; init; }
    internal int Facet { get; init; }
    internal int Width { get; init; }
    internal int Height { get; init; }
    internal string Version { get; init; }
    internal string MapPath { get; init; }
    internal string StaidxPath { get; init; }
    internal string StaticsPath { get; init; }
    internal string MapSha256 { get; init; }
    internal string StaidxSha256 { get; init; }
    internal string StaticsSha256 { get; init; }
    internal IReadOnlyDictionary<string, string> Neighbors { get; init; }

    internal int BlockCount => (Width >> 3) * (Height >> 3);

    /// <summary>Content identity: a change to any of these invalidates a resident copy.</summary>
    internal string ContentKey => $"{Id}|{Facet}|{Width}x{Height}|{Version}|{MapSha256}|{StaidxSha256}|{StaticsSha256}";
}

/// <summary>
/// The validated page manifest (`kind: guo-page-manifest`, format 1). Parsing
/// rejects the whole manifest on the first invalid entry: a pack is either
/// usable as described or not used at all.
/// </summary>
internal sealed class PageCatalog
{
    internal const string Kind = "guo-page-manifest";
    internal const int Format = 1;
    // Proposed page range (ADR draft): 0-31 stay with stock/shard facets,
    // 127 and 255 stay reserved. Subject to a shard allocation check.
    internal const int FirstFacet = 32;
    internal const int LastFacet = 79;
    // Legacy world-item packets mask X to 15 bits and Y to 14 bits.
    internal const int MaxWidth = 0x8000;
    internal const int MaxHeight = 0x4000;
    internal const int MapBlockBytes = 196;   // header + 64 cells of (ushort, sbyte)
    internal const int StaidxEntryBytes = 12; // position, size, unknown
    internal const int StaticEntryBytes = 7;  // graphic, x, y, z, hue

    internal static readonly string[] Directions = { "north", "east", "south", "west" };

    private readonly Dictionary<int, PageEntry> _byFacet;
    private readonly Dictionary<string, PageEntry> _byId;

    private PageCatalog(string path, string root, List<PageEntry> pages)
    {
        ManifestPath = path;
        Root = root;
        Pages = pages;
        _byFacet = pages.ToDictionary(p => p.Facet);
        _byId = pages.ToDictionary(p => p.Id, StringComparer.Ordinal);
    }

    internal string ManifestPath { get; }
    internal string Root { get; }
    internal IReadOnlyList<PageEntry> Pages { get; }

    internal static bool IsPageFacet(int facet) => facet >= FirstFacet && facet <= LastFacet;

    internal PageEntry ByFacet(int facet) => _byFacet.TryGetValue(facet, out var page) ? page : null;
    internal PageEntry ById(string id) => id != null && _byId.TryGetValue(id, out var page) ? page : null;

    internal static PageCatalog Load(string manifestPath)
    {
        if (string.IsNullOrWhiteSpace(manifestPath))
            throw new PageManifestException("No page manifest path is set.");
        string full = Path.GetFullPath(manifestPath);
        if (!File.Exists(full))
            throw new PageManifestException($"Page manifest not found: {full}");
        return Parse(File.ReadAllText(full), full);
    }

    internal static PageCatalog Parse(string json, string manifestPath)
    {
        string root = Path.GetDirectoryName(Path.GetFullPath(manifestPath));
        JsonDocument doc;
        try
        {
            doc = JsonDocument.Parse(json);
        }
        catch (JsonException e)
        {
            throw new PageManifestException($"Page manifest is not valid JSON: {e.Message}");
        }

        using (doc)
        {
            var top = doc.RootElement;
            Require(top.ValueKind == JsonValueKind.Object, "Page manifest must be a JSON object.");
            Require(String(top, "kind", "manifest") == Kind, $"Page manifest kind must be '{Kind}'.");
            Require(Int(top, "format", "manifest") == Format, $"Page manifest format must be {Format}.");
            Require(top.TryGetProperty("pages", out var pagesElement) && pagesElement.ValueKind == JsonValueKind.Array,
                "Page manifest needs a 'pages' array.");

            var pages = new List<PageEntry>();
            var facets = new HashSet<int>();
            var ids = new HashSet<string>(StringComparer.Ordinal);
            var neighborIds = new List<(string page, string dir, string target)>();

            foreach (var e in pagesElement.EnumerateArray())
            {
                Require(e.ValueKind == JsonValueKind.Object, "Every page entry must be an object.");
                string id = String(e, "page", "page");
                string where = $"page '{id}'";
                Require(id.Length > 0 && id.Length <= 64 && id.All(c => char.IsLetterOrDigit(c) || c == '-' || c == '_'),
                    $"{where}: page id must be 1-64 letters, digits, '-' or '_'.");
                Require(ids.Add(id), $"{where}: duplicate page id.");

                int facet = Int(e, "facet", where);
                Require(IsPageFacet(facet),
                    $"{where}: facet {facet} is outside the page range {FirstFacet}-{LastFacet} (0-31, 127 and 255 are reserved).");
                Require(facets.Add(facet), $"{where}: facet {facet} is used by another page.");

                int width = Int(e, "width", where), height = Int(e, "height", where);
                Require(width > 0 && height > 0 && (width & 7) == 0 && (height & 7) == 0,
                    $"{where}: width and height must be positive multiples of 8 (got {width}x{height}).");
                Require(width <= MaxWidth && height <= MaxHeight,
                    $"{where}: {width}x{height} exceeds {MaxWidth}x{MaxHeight}, the legacy packet coordinate range.");

                string version = String(e, "version", where);
                Require(version.Length > 0 && version.Length <= 64, $"{where}: version must be 1-64 characters.");

                Require(e.TryGetProperty("sha256", out var sha) && sha.ValueKind == JsonValueKind.Object,
                    $"{where}: needs a 'sha256' object with map, staidx and statics.");

                var neighbors = new Dictionary<string, string>(StringComparer.Ordinal);
                if (e.TryGetProperty("neighbors", out var n) && n.ValueKind != JsonValueKind.Null)
                {
                    Require(n.ValueKind == JsonValueKind.Object, $"{where}: 'neighbors' must be an object.");
                    foreach (var p in n.EnumerateObject())
                    {
                        Require(Array.IndexOf(Directions, p.Name) >= 0,
                            $"{where}: neighbor direction '{p.Name}' is not north, east, south or west.");
                        if (p.Value.ValueKind == JsonValueKind.Null) continue;
                        Require(p.Value.ValueKind == JsonValueKind.String, $"{where}: neighbor '{p.Name}' must be a page id or null.");
                        neighbors[p.Name] = p.Value.GetString();
                        neighborIds.Add((id, p.Name, p.Value.GetString()));
                    }
                }

                pages.Add(new PageEntry
                {
                    Id = id,
                    Facet = facet,
                    Width = width,
                    Height = height,
                    Version = version,
                    MapPath = PackFile(root, String(e, "map", where), where),
                    StaidxPath = PackFile(root, String(e, "staidx", where), where),
                    StaticsPath = PackFile(root, String(e, "statics", where), where),
                    MapSha256 = Hash(sha, "map", where),
                    StaidxSha256 = Hash(sha, "staidx", where),
                    StaticsSha256 = Hash(sha, "statics", where),
                    Neighbors = neighbors,
                });
            }

            Require(pages.Count > 0, "Page manifest lists no pages.");
            foreach (var (page, dir, target) in neighborIds)
            {
                Require(ids.Contains(target), $"page '{page}': {dir} neighbor '{target}' is not in the manifest.");
                Require(target != page, $"page '{page}': a page cannot neighbor itself.");
            }

            return new PageCatalog(Path.GetFullPath(manifestPath), root, pages);
        }
    }

    private static string PackFile(string root, string relative, string where)
    {
        Require(relative.Length > 0 && !Path.IsPathRooted(relative),
            $"{where}: file '{relative}' must be relative to the manifest folder.");
        string full = Path.GetFullPath(Path.Combine(root, relative));
        string prefix = root.EndsWith(Path.DirectorySeparatorChar) ? root : root + Path.DirectorySeparatorChar;
        Require(full.StartsWith(prefix, StringComparison.OrdinalIgnoreCase),
            $"{where}: file '{relative}' leaves the manifest folder.");
        return full;
    }

    private static string Hash(JsonElement sha, string name, string where)
    {
        string value = String(sha, name, where + " sha256").ToLowerInvariant();
        Require(value.Length == 64 && value.All(Uri.IsHexDigit), $"{where}: sha256.{name} must be 64 hex digits.");
        return value;
    }

    private static string String(JsonElement e, string name, string where)
    {
        Require(e.TryGetProperty(name, out var v) && v.ValueKind == JsonValueKind.String, $"{where}: '{name}' must be a string.");
        return v.GetString();
    }

    private static int Int(JsonElement e, string name, string where)
    {
        Require(e.TryGetProperty(name, out var v) && v.ValueKind == JsonValueKind.Number && v.TryGetInt32(out _),
            $"{where}: '{name}' must be an integer.");
        return v.GetInt32();
    }

    private static void Require(bool condition, string message)
    {
        if (!condition) throw new PageManifestException(message);
    }
}

internal sealed class PageManifestException : Exception
{
    internal PageManifestException(string message) : base(message) { }
}
#endif
