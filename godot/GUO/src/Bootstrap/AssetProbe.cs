// SPDX-License-Identifier: BSD-2-Clause

using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.Text.Json;
using Godot;
using GUO.Assets;

namespace GUO.Host;

/// <summary>
/// Decode chosen art, gumps and hues through the running client's own
/// loaders, write them to disk, and quit.
/// </summary>
/// <remarks>
/// The client half of the editor's asset round trip (ADR-0020). A world
/// project's export is a patch set (verdata.mul, hues.mul) that a client
/// reads through upstream's <c>files_override</c>; this reads the art back
/// from the file manager the client built at start (settings.json, then the
/// override map, then <c>UOFileManager.Load</c> with its verdata pass), so
/// what it writes is what the client would draw. It needs no shard and no
/// window: the loading screen is enough. tools/editor_asset_roundtrip
/// compares the files with the project's.
///
/// Ids: <c>--asset-probe-ids land:0x0244,static:0x0E75,gump:0x0064,hue:33</c>.
/// </remarks>
internal static class AssetProbe
{
    public static bool Passed { get; private set; }

    public static async System.Threading.Tasks.Task Run(Node host, string outDir, string ids)
    {
        for (int i = 0; i < 1200 && Client.Game?.UO?.FileManager == null; i++)
        {
            await host.ToSignal(host.GetTree(), SceneTree.SignalName.ProcessFrame);
        }

        UOFileManager files = Client.Game?.UO?.FileManager;
        if (files == null)
        {
            GD.PrintErr("[GUO] asset probe: the client never loaded its files.");
            return;
        }

        Directory.CreateDirectory(outDir);
        var report = new Dictionary<string, object>
        {
            ["verdata_patches"] = files.Verdata.Patches.Length,
            ["verdata_path"] = files.GetUOFilePath("verdata.mul"),
            ["hues_path"] = files.GetUOFilePath("hues.mul"),
        };

        int written = 0, asked = 0;
        foreach (string item in (ids ?? "").Split(',', System.StringSplitOptions.RemoveEmptyEntries))
        {
            string[] kv = item.Split(':');
            if (kv.Length != 2)
            {
                continue;
            }

            asked++;
            string kind = kv[0].Trim().ToLowerInvariant();
            string v = kv[1].Trim();
            int id = v.StartsWith("0x", System.StringComparison.OrdinalIgnoreCase)
                ? int.Parse(v[2..], NumberStyles.HexNumber, CultureInfo.InvariantCulture)
                : int.Parse(v, CultureInfo.InvariantCulture);

            if (kind == "hue")
            {
                int h = id - 1;
                var colours = new List<string>();
                for (int c = 0; c < 32; c++)
                {
                    colours.Add(files.Hues.HuesRange[h >> 3].Entries[h & 7].ColorTable[c].ToString("X4", CultureInfo.InvariantCulture));
                }

                report[$"hue_{id}"] = colours;
                written++;
                continue;
            }

            uint[] pixels;
            int w, hgt;
            if (kind == "gump")
            {
                GumpInfo g = files.Gumps.GetGump((uint)id);
                pixels = g.Pixels.ToArray();
                w = g.Width;
                hgt = g.Height;
            }
            else
            {
                ArtInfo a = files.Arts.GetArt(kind == "land" ? (uint)id : ArtLoader.MAX_LAND_DATA_INDEX_COUNT + (uint)id);
                pixels = a.Pixels.ToArray();
                w = a.Width;
                hgt = a.Height;
            }

            if (w <= 0 || hgt <= 0 || pixels.Length < w * hgt)
            {
                GD.PrintErr($"[GUO] asset probe: {kind} 0x{id:X4} decoded to nothing");
                continue;
            }

            // Loader pixels are R | G<<8 | B<<16 with 0 transparent, as the
            // editor's EditorData.FromPixels reads them.
            var rgba = new byte[w * hgt * 4];
            for (int p = 0; p < w * hgt; p++)
            {
                uint px = pixels[p];
                rgba[p * 4] = (byte)px;
                rgba[p * 4 + 1] = (byte)(px >> 8);
                rgba[p * 4 + 2] = (byte)(px >> 16);
                rgba[p * 4 + 3] = px == 0 ? (byte)0 : (byte)255;
            }

            string path = Path.Combine(outDir, $"{kind}_0x{id:X4}.png");
            Image.CreateFromData(w, hgt, false, Image.Format.Rgba8, rgba).SavePng(path);
            report[$"{kind}_0x{id:X4}"] = path;
            written++;
        }

        File.WriteAllText(Path.Combine(outDir, "asset_probe.json"),
            JsonSerializer.Serialize(report, new JsonSerializerOptions { WriteIndented = true }));
        GD.Print($"[GUO] asset probe: {written} of {asked} written to {outDir}");
        Passed = asked > 0 && written == asked;
    }
}
