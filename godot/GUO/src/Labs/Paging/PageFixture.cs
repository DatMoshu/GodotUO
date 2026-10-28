// SPDX-License-Identifier: BSD-2-Clause
#if GUO_LABS || GUO_PAGING_TESTS
using System;
using System.Collections.Generic;
using System.IO;
using System.Security.Cryptography;
using System.Text.Json;

namespace GUO.Labs.Paging;

/// <summary>
/// Synthetic page packs for tests, the doctor and the probe: flat land, no
/// statics, a west-to-east row of pages. Never real map data.
/// </summary>
internal static class PageFixture
{
    internal const ushort Land = 0x0003; // a grass land tile ID; the pixels come from the user's own install

    internal static string Page(string id, int facet, int width = 64, int height = 64, string version = "v1",
        string map = null, string sha = null, string east = null, string west = null)
    {
        string hash = sha ?? new string('0', 64);
        var neighbors = new Dictionary<string, string> { ["north"] = null, ["east"] = east, ["south"] = null, ["west"] = west };
        return JsonSerializer.Serialize(new Dictionary<string, object>
        {
            ["page"] = id, ["facet"] = facet, ["width"] = width, ["height"] = height, ["version"] = version,
            ["map"] = map ?? $"map{facet}.mul", ["staidx"] = $"staidx{facet}.mul", ["statics"] = $"statics{facet}.mul",
            ["sha256"] = new Dictionary<string, string> { ["map"] = hash, ["staidx"] = hash, ["statics"] = hash },
            ["neighbors"] = neighbors,
        });
    }

    internal static string Manifest(params string[] pages) =>
        "{\"kind\":\"guo-page-manifest\",\"format\":1,\"pages\":[" + string.Join(",", pages) + "]}";

    internal static string WriteTwoPages(string dir, int width, int height)
    {
        return WritePages(dir, width, height, 32, 33);
    }

    internal static string WritePages(string dir, int width, int height, params int[] facets) =>
        WritePages(dir, width, height, new Dictionary<int, string>(), facets);

    /// <summary>A west-to-east row of pages; <paramref name="versions"/> revises chosen pages.</summary>
    internal static string WritePages(string dir, int width, int height, Dictionary<int, string> versions, params int[] facets) =>
        WriteGrid(dir, width, height, 1, versions, facets);

    /// <summary>
    /// Pages in <paramref name="rows"/> rows, row-major from the north-west,
    /// each the cardinal neighbour of the next one along its row and column.
    /// </summary>
    internal static string WriteGrid(string dir, int width, int height, int rows, Dictionary<int, string> versions, params int[] facets)
    {
        Directory.CreateDirectory(dir);
        int cols = facets.Length / rows;
        if (rows < 1 || cols * rows != facets.Length) throw new ArgumentException("facets do not fill the grid");
        var pages = new List<string>();
        for (int i = 0; i < facets.Length; i++)
        {
            int facet = facets[i];
            string version = versions.TryGetValue(facet, out var v) ? v : "v1";
            // A revised version carries different terrain height, so its hashes change too.
            sbyte z = (sbyte)(version == "v1" ? 0 : 5);
            int blocks = (width >> 3) * (height >> 3);
            var mapBytes = new byte[blocks * 196];
            for (int b = 0; b < blocks; b++)
                for (int c = 0; c < 64; c++)
                {
                    int o = b * 196 + 4 + c * 3;
                    BitConverter.GetBytes(Land).CopyTo(mapBytes, o);
                    mapBytes[o + 2] = (byte)z;
                }
            var idx = new byte[blocks * 12];
            for (int b = 0; b < blocks; b++)
            {
                BitConverter.GetBytes(0xFFFF_FFFFu).CopyTo(idx, b * 12);
                BitConverter.GetBytes(0u).CopyTo(idx, b * 12 + 4);
            }
            var statics = Array.Empty<byte>();
            string suffix = version == "v1" ? "" : "." + version;
            string mapName = $"map{facet}{suffix}.mul", idxName = $"staidx{facet}{suffix}.mul", staName = $"statics{facet}{suffix}.mul";
            File.WriteAllBytes(Path.Combine(dir, mapName), mapBytes);
            File.WriteAllBytes(Path.Combine(dir, idxName), idx);
            File.WriteAllBytes(Path.Combine(dir, staName), statics);

            int row = i / cols, col = i % cols;
            var neighbors = new Dictionary<string, string>
            {
                ["north"] = row > 0 ? $"p{facets[i - cols]}" : null,
                ["south"] = row + 1 < rows ? $"p{facets[i + cols]}" : null,
                ["west"] = col > 0 ? $"p{facets[i - 1]}" : null,
                ["east"] = col + 1 < cols ? $"p{facets[i + 1]}" : null,
            };
            pages.Add(JsonSerializer.Serialize(new Dictionary<string, object>
            {
                ["page"] = $"p{facet}", ["facet"] = facet, ["width"] = width, ["height"] = height, ["version"] = version,
                ["map"] = mapName, ["staidx"] = idxName, ["statics"] = staName,
                ["sha256"] = new Dictionary<string, string>
                {
                    ["map"] = Sha(mapBytes), ["staidx"] = Sha(idx), ["statics"] = Sha(statics),
                },
                ["neighbors"] = neighbors,
            }));
        }
        string name = versions.Count == 0 ? "pages.json" : "pages.revised.json";
        string path = Path.Combine(dir, name);
        File.WriteAllText(path, Manifest(pages.ToArray()));
        return path;
    }

    static string Sha(byte[] bytes) => Convert.ToHexString(SHA256.HashData(bytes)).ToLowerInvariant();
}
#endif
