// SPDX-License-Identifier: BSD-2-Clause

using System.ComponentModel;
using System.Globalization;
using System.Text;
using System.Text.Json;
using ModelContextProtocol;
using ModelContextProtocol.Server;

namespace GUO.AssetMcp;

/// <summary>
/// The guoasset tools. Every one of them only reads the client install;
/// renders are written under build\guoasset\out unless a folder is given.
/// They derive from proprietary client data and must never be committed.
/// </summary>
[McpServerToolType]
public static class AssetTools
{
    private const string ClientPathHelp =
        "UO client folder. Omit to use UO_CLIENT_DATA from the environment or launchers\\_shared\\config(.local).bat.";

    [McpServerTool(Name = "locate", ReadOnly = true), Description(
        "Reports which client install and client version this server reads, and where it writes renders. "
        + "Use as a connectivity check.")]
    public static string Locate([Description(ClientPathHelp)] string? clientPath = null) => Tool(() =>
        ClientData.With(clientPath, d =>
            $"client:  {d.Path}\nversion: {d.VersionText}\nrepo:    {Settings.RepoRoot ?? "(not found)"}\nout:     {Settings.DefaultOutDir("")}"));

    [McpServerTool(Name = "get_image", ReadOnly = true), Description(
        "Renders one PNG from the client data, decoded by upstream ClassicUO's loaders. Three modes: "
        + "`tile` + `kind` for a single land, static or gump; `multi` for an isometric composite of a multi; "
        + "`ids` + `kind` for an atlas of several ids. Returns the PNG path. Pixels are exact, never filtered.")]
    public static string GetImage(
        [Description("(single) Tile or gump id, decimal or 0x hex. Pair with `kind`.")] string? tile = null,
        [Description("(multi) Multi id, decimal or 0x hex.")] string? multi = null,
        [Description("(atlas) Comma-separated ids, decimal or 0x hex. Pair with `kind`.")] string? ids = null,
        [Description("'land', 'static' or 'gump'. Required with `tile` or `ids`.")] string? kind = null,
        [Description("Output folder. Default build\\guoasset\\out\\images.")] string? outDir = null,
        [Description("Base name for an atlas. Default derived from kind and time.")] string? name = null,
        [Description(ClientPathHelp)] string? clientPath = null) => Tool(() =>
    {
        outDir ??= Settings.DefaultOutDir("images");
        return ClientData.With(clientPath, d =>
        {
            if (!string.IsNullOrWhiteSpace(multi))
            {
                int id = ParseId(multi);
                var parts = d.Multi(id)
                    .Select(c => (x: (int)c.X, y: (int)c.Y, z: (int)c.Z, px: d.Get(Kind.Static, c.ID)))
                    .Where(c => !c.px.IsEmpty)
                    .ToList();
                if (parts.Count == 0)
                    throw new InvalidOperationException($"multi 0x{id:X4} has no drawable parts");
                var path = Path.Combine(outDir, $"multi_0x{id:X4}.png");
                var (w, h) = Imaging.MultiIso(parts, path);
                return Result(path, $"multi 0x{id:X4}: {parts.Count} parts, {w}x{h}");
            }

            var k = ParseKind(kind);
            if (!string.IsNullOrWhiteSpace(tile))
            {
                int id = ParseId(tile);
                var px = d.Get(k, id);
                if (px.IsEmpty)
                    throw new InvalidOperationException($"{Name(k)} 0x{id:X4} is empty");
                var path = Path.Combine(outDir, $"{Name(k)}_0x{id:X4}.png");
                Imaging.WritePng(path, px);
                return Result(path, $"{Name(k)} 0x{id:X4}: {px.Width}x{px.Height}");
            }

            if (!string.IsNullOrWhiteSpace(ids))
            {
                var atlas = Imaging.Atlas(Load(d, k, ParseIds(ids)), outDir,
                    name ?? $"query_{Name(k)}_{DateTime.Now:yyyyMMdd-HHmmss}");
                return Result(atlas.PngPath, $"{atlas.Count} {Name(k)} images, {atlas.Width}x{atlas.Height}", atlas.JsonPath);
            }

            throw new ArgumentException("Give one of: `tile` + `kind`, `multi`, or `ids` + `kind`.");
        });
    });

    [McpServerTool(Name = "atlas_art", ReadOnly = true), Description(
        "Packs a range or list of land, static or gump images into one PNG sheet plus a JSON manifest "
        + "(image, size, frames[index, frame, center]). Useful to check a whole tile family at once.")]
    public static string AtlasArt(
        [Description("'land', 'static' or 'gump'.")] string kind,
        [Description("Comma-separated ids. Use this or from/to.")] string? ids = null,
        [Description("Range start, inclusive.")] string? from = null,
        [Description("Range end, inclusive.")] string? to = null,
        [Description("Output folder. Default build\\guoasset\\out\\atlas.")] string? outDir = null,
        [Description("Base name. Default derived from kind and time.")] string? name = null,
        [Description(ClientPathHelp)] string? clientPath = null) => Tool(() =>
    {
        var k = ParseKind(kind);
        outDir ??= Settings.DefaultOutDir("atlas");
        return ClientData.With(clientPath, d =>
        {
            var list = !string.IsNullOrWhiteSpace(ids) ? ParseIds(ids) : Range(d, k, from, to);
            if (list.Count == 0)
                throw new ArgumentException("Select ids with `ids`, or with `from` and `to`.");
            var atlas = Imaging.Atlas(Load(d, k, list), outDir, name ?? $"atlas_{Name(k)}_{DateTime.Now:yyyyMMdd-HHmmss}");
            return Result(atlas.PngPath, $"{atlas.Count} {Name(k)} images, {atlas.Width}x{atlas.Height}", atlas.JsonPath);
        });
    });

    [McpServerTool(Name = "export_art", ReadOnly = true), Description(
        "Writes one PNG per non-empty land, static or gump id in [from, to] to a folder, named "
        + "Land_0xNNNN.png, Static_0xNNNN.png or Gump_0xNNNN.png. Empty ids are skipped.")]
    public static string ExportArt(
        [Description("'land', 'static' or 'gump'.")] string kind,
        [Description("Range start, inclusive. Default 0.")] string? from = null,
        [Description("Range end, inclusive. Default the last id.")] string? to = null,
        [Description("Output folder. Default build\\guoasset\\out\\<kind>.")] string? outDir = null,
        [Description(ClientPathHelp)] string? clientPath = null) => Tool(() =>
    {
        var k = ParseKind(kind);
        outDir ??= Settings.DefaultOutDir(Name(k));
        return ClientData.With(clientPath, d =>
        {
            var prefix = char.ToUpperInvariant(Name(k)[0]) + Name(k)[1..];
            int written = 0, empty = 0;
            var list = Range(d, k, from ?? "0", to);
            foreach (var id in list)
            {
                var px = d.Get(k, id);
                if (px.IsEmpty) { empty++; continue; }
                Imaging.WritePng(Path.Combine(outDir, $"{prefix}_0x{id:X4}.png"), px);
                written++;
            }
            return $"{written} written, {empty} empty, ids 0x{list[0]:X4}..0x{list[^1]:X4}\nfolder: {Path.GetFullPath(outDir)}";
        });
    });

    [McpServerTool(Name = "list_multis", ReadOnly = true), Description(
        "Lists every populated multi (house, boat, structure) as JSON: [{ id, parts, bounds }]. "
        + "Slow: it reads every multi entry.")]
    public static string ListMultis(
        [Description("Optional file to write the JSON to instead of returning it.")] string? outFile = null,
        [Description(ClientPathHelp)] string? clientPath = null) => Tool(() =>
        ClientData.With(clientPath, d =>
        {
            var rows = new List<object>();
            for (int id = 0; id < d.MaxMultiId; id++)
            {
                var parts = d.Multi(id);
                if (parts.Count == 0)
                    continue;
                rows.Add(new
                {
                    id = $"0x{id:X4}",
                    parts = parts.Count,
                    bounds = new
                    {
                        minX = parts.Min(p => p.X), minY = parts.Min(p => p.Y),
                        maxX = parts.Max(p => p.X), maxY = parts.Max(p => p.Y),
                    },
                });
            }
            return Emit(JsonSerializer.Serialize(rows, new JsonSerializerOptions { WriteIndented = true }), outFile,
                $"{rows.Count} multis");
        }));

    [McpServerTool(Name = "multi_info", ReadOnly = true), Description(
        "Returns one multi as JSON: id, bounds and every part { itemId, x, y, z, visible }, "
        + "in the order the multi file lists them.")]
    public static string MultiInfo(
        [Description("Multi id, decimal or 0x hex.")] string id,
        [Description("Optional file to write the JSON to instead of returning it.")] string? outFile = null,
        [Description(ClientPathHelp)] string? clientPath = null) => Tool(() =>
        ClientData.With(clientPath, d =>
        {
            int mid = ParseId(id);
            var parts = d.Multi(mid);
            if (parts.Count == 0)
                throw new InvalidOperationException($"multi 0x{mid:X4} is empty");
            var doc = new
            {
                id = $"0x{mid:X4}",
                bounds = new
                {
                    minX = parts.Min(p => p.X), minY = parts.Min(p => p.Y), minZ = parts.Min(p => p.Z),
                    maxX = parts.Max(p => p.X), maxY = parts.Max(p => p.Y), maxZ = parts.Max(p => p.Z),
                },
                parts = parts.Select(p => new { itemId = $"0x{p.ID:X4}", x = p.X, y = p.Y, z = p.Z, visible = p.IsVisible }),
            };
            return Emit(JsonSerializer.Serialize(doc, new JsonSerializerOptions { WriteIndented = true }), outFile,
                $"multi 0x{mid:X4}: {parts.Count} parts");
        }));

    [McpServerTool(Name = "search_tiles", ReadOnly = true), Description(
        "Finds tiles whose tiledata name contains a keyword, case-insensitive. Returns JSON "
        + "{ keyword, count, matches: [{ kind, id, name }] }. Pair the ids with get_image or atlas_art.")]
    public static string SearchTiles(
        [Description("Text to look for in tile names.")] string keyword,
        [Description("'land', 'static' or 'both'. Default 'both'.")] string? kind = null,
        [Description("Most matches to return. Default 200.")] int limit = 200,
        [Description(ClientPathHelp)] string? clientPath = null) => Tool(() =>
        ClientData.With(clientPath, d =>
        {
            kind = string.IsNullOrWhiteSpace(kind) ? "both" : kind.ToLowerInvariant();
            var matches = d.TileNames()
                .Where(t => kind == "both" || Name(t.kind) == kind)
                .Where(t => t.name.Contains(keyword, StringComparison.OrdinalIgnoreCase))
                .Take(limit > 0 ? limit : 200)
                .Select(t => new { kind = Name(t.kind), id = $"0x{t.id:X4}", t.name })
                .ToList();
            return JsonSerializer.Serialize(new { keyword, count = matches.Count, matches },
                new JsonSerializerOptions { WriteIndented = true });
        }));

    // --- helpers ---------------------------------------------------------------

    /// <summary>
    /// The MCP SDK reports an ordinary exception as "An error occurred", with
    /// no message. Rethrown as McpException, the caller learns what was wrong.
    /// </summary>
    private static string Tool(Func<string> body)
    {
        try
        {
            return body();
        }
        catch (Exception ex) when (ex is not McpException)
        {
            throw new McpException(ex.Message, ex);
        }
    }

    private static List<(int, Pixels)> Load(ClientData d, Kind k, IEnumerable<int> ids) =>
        ids.Select(id => (id, d.Get(k, id))).Where(t => !t.Item2.IsEmpty).ToList();

    private static List<int> Range(ClientData d, Kind k, string? from, string? to)
    {
        if (string.IsNullOrWhiteSpace(from) && string.IsNullOrWhiteSpace(to))
            return [];
        int max = d.MaxId(k);
        int a = Math.Clamp(string.IsNullOrWhiteSpace(from) ? 0 : ParseId(from), 0, max - 1);
        int b = Math.Clamp(string.IsNullOrWhiteSpace(to) ? max - 1 : ParseId(to), a, max - 1);
        return Enumerable.Range(a, b - a + 1).ToList();
    }

    private static Kind ParseKind(string? kind) => kind?.Trim().ToLowerInvariant() switch
    {
        "land" => Kind.Land,
        "static" => Kind.Static,
        "gump" => Kind.Gump,
        _ => throw new ArgumentException("`kind` must be 'land', 'static' or 'gump'."),
    };

    private static string Name(Kind k) => k.ToString().ToLowerInvariant();

    private static int ParseId(string raw)
    {
        raw = raw.Trim();
        return raw.StartsWith("0x", StringComparison.OrdinalIgnoreCase)
            ? int.Parse(raw.AsSpan(2), NumberStyles.HexNumber, CultureInfo.InvariantCulture)
            : int.Parse(raw, CultureInfo.InvariantCulture);
    }

    private static List<int> ParseIds(string raw) =>
        raw.Split(',', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries).Select(ParseId).ToList();

    private static string Result(string png, string detail, string? json = null)
    {
        var full = Path.GetFullPath(png);
        var sb = new StringBuilder()
            .AppendLine(detail)
            .AppendLine($"png:  {full}")
            .AppendLine($"open: file:///{full.Replace('\\', '/')}");
        if (json is not null)
            sb.AppendLine($"json: {Path.GetFullPath(json)}");
        return sb.ToString();
    }

    private static string Emit(string json, string? outFile, string summary)
    {
        if (string.IsNullOrWhiteSpace(outFile))
            return json;
        var full = Path.GetFullPath(outFile);
        Directory.CreateDirectory(Path.GetDirectoryName(full)!);
        File.WriteAllText(full, json);
        return $"{summary} -> {full}";
    }
}
