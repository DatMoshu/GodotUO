#if TOOLS
namespace GUO.Editor;

using System;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.Linq;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;
using GUO.Assets;
using GUO.IO;

/// <summary>One map block (8x8 cells) as a world project stores it: all 64 land cells and every static.</summary>
public sealed class WorldBlock
{
    public int Facet;
    public int Bx, By;

    /// <summary>Land cells, index y * 8 + x.</summary>
    public readonly ushort[] LandId = new ushort[64];
    public readonly sbyte[] LandZ = new sbyte[64];

    public readonly List<WorldStatic> Statics = new();

    public string Key => $"{Bx}_{By}";
}

public struct WorldStatic
{
    public ushort Id;
    public byte X, Y;
    public sbyte Z;
    public ushort Hue;
}

/// <summary>
/// A world project (docs/editor_plan.md §4.3, ADR-0011): a folder of ours
/// holding whole replaced map blocks, laid over the read-only client install
/// the way the client's own mapdif/stadif are.
/// </summary>
/// <remarks>
/// <para>
/// On disk (docs/data_formats.md §9): <c>project.json</c>, and one
/// <c>blocks/&lt;facet&gt;/&lt;bx&gt;_&lt;by&gt;.json</c> per replaced block. JSON so a
/// change reviews as a diff: land is eight rows of eight <c>id:z</c> cells.
/// </para>
/// <para>
/// Applying a project to a loaded map writes the blocks, in the client's own
/// binary block layout, to a scratch file under the project's <c>.cache</c>
/// and repoints those blocks' <see cref="IndexMap"/> entries at it, the way
/// UltimaLive and the verdata patch layer repoint theirs. <c>Chunk.Load</c>
/// reads through whatever reader the entry holds, so no ported file changes.
/// Nothing is ever written to the install: this class has no path into
/// <c>UO_CLIENT_DATA</c> except reading the fingerprint.
/// </para>
/// </remarks>
public sealed class WorldProject : IDisposable
{
    public const int Format = 1;
    private const int MapBlockSize = 4 + 64 * 3;
    private const int StaticSize = 7;

    private readonly Dictionary<int, UOFileMul> _overlayFiles = new();
    private readonly Dictionary<(int facet, int block), IndexMap> _originals = new();
    private int _generation;

    public string Root { get; }
    public string Name { get; private set; }
    public string BaseFingerprint { get; private set; }

    private WorldProject(string root)
    {
        Root = root;
    }

    /// <summary>
    /// Opens the project at <paramref name="root"/>, creating it if there is
    /// none. The base install's fingerprint is recorded at creation, so a
    /// project made against one install is noticed on another.
    /// </summary>
    public static WorldProject OpenOrCreate(string root, string clientData, string clientVersion)
    {
        var p = new WorldProject(Path.GetFullPath(root));
        string file = Path.Combine(p.Root, "project.json");
        if (File.Exists(file))
        {
            JsonNode j = JsonNode.Parse(File.ReadAllText(file));
            p.Name = (string)j["name"];
            p.BaseFingerprint = (string)j["base"]?["fingerprint"];
            return p;
        }

        Directory.CreateDirectory(p.Root);
        p.Name = Path.GetFileName(p.Root.TrimEnd('/', '\\'));
        p.BaseFingerprint = Fingerprint(clientData);
        var json = new JsonObject
        {
            ["format"] = Format,
            ["name"] = p.Name,
            ["created"] = DateTime.UtcNow.ToString("o", CultureInfo.InvariantCulture),
            ["base"] = new JsonObject
            {
                ["client_version"] = clientVersion,
                ["fingerprint"] = p.BaseFingerprint,
            },
        };
        File.WriteAllText(file, json.ToJsonString(new JsonSerializerOptions { WriteIndented = true }) + "\n");
        return p;
    }

    /// <summary>
    /// Names and sizes of the install's map and statics files, hashed. Cheap,
    /// and enough to tell one install from another; not a content hash.
    /// </summary>
    public static string Fingerprint(string clientData)
    {
        var sb = new StringBuilder();
        foreach (string f in Directory.GetFiles(clientData)
                     .Where(f =>
                     {
                         string n = Path.GetFileName(f).ToLowerInvariant();
                         return n.StartsWith("map") || n.StartsWith("statics") || n.StartsWith("staidx");
                     })
                     .OrderBy(f => f, StringComparer.OrdinalIgnoreCase))
        {
            sb.Append(Path.GetFileName(f).ToLowerInvariant()).Append(':').Append(new FileInfo(f).Length).Append('\n');
        }

        return Convert.ToHexString(SHA1.HashData(Encoding.UTF8.GetBytes(sb.ToString()))).ToLowerInvariant();
    }

    private string BlockDir(int facet) => Path.Combine(Root, "blocks", facet.ToString(CultureInfo.InvariantCulture));

    /// <summary>Every block the project replaces on a facet.</summary>
    public List<WorldBlock> Blocks(int facet)
    {
        var list = new List<WorldBlock>();
        string dir = BlockDir(facet);
        if (!Directory.Exists(dir))
        {
            return list;
        }

        foreach (string f in Directory.GetFiles(dir, "*.json").OrderBy(f => f, StringComparer.Ordinal))
        {
            list.Add(ReadBlock(f));
        }

        return list;
    }

    public static WorldBlock ReadBlock(string path)
    {
        JsonNode j = JsonNode.Parse(File.ReadAllText(path));
        var b = new WorldBlock
        {
            Facet = (int)j["facet"],
            Bx = (int)j["block"][0],
            By = (int)j["block"][1],
        };

        JsonArray rows = j["land"].AsArray();
        for (int y = 0; y < 8; y++)
        {
            string[] cells = ((string)rows[y]).Split(' ', StringSplitOptions.RemoveEmptyEntries);
            for (int x = 0; x < 8; x++)
            {
                string[] c = cells[x].Split(':');
                b.LandId[y * 8 + x] = ushort.Parse(c[0], NumberStyles.HexNumber, CultureInfo.InvariantCulture);
                b.LandZ[y * 8 + x] = sbyte.Parse(c[1], CultureInfo.InvariantCulture);
            }
        }

        foreach (JsonNode s in j["statics"].AsArray())
        {
            b.Statics.Add(new WorldStatic
            {
                Id = Hex((string)s["id"]),
                X = (byte)(int)s["x"],
                Y = (byte)(int)s["y"],
                Z = (sbyte)(int)s["z"],
                Hue = Hex((string)s["hue"]),
            });
        }

        return b;
    }

    private static ushort Hex(string s) =>
        ushort.Parse(s.StartsWith("0x", StringComparison.OrdinalIgnoreCase) ? s[2..] : s, NumberStyles.HexNumber, CultureInfo.InvariantCulture);

    /// <summary>Saves a block. Statics are written sorted by cell, then z, so a diff shows only what changed.</summary>
    public string WriteBlock(WorldBlock b)
    {
        // Written by hand rather than by the serializer so that one land row
        // and one static are one line each: a change to a block reviews as a
        // diff of exactly the rows and statics that changed.
        var ci = CultureInfo.InvariantCulture;
        var sb = new StringBuilder();
        sb.Append("{\n");
        sb.Append($"  \"format\": {Format},\n");
        sb.Append($"  \"facet\": {b.Facet},\n");
        sb.Append($"  \"block\": [{b.Bx}, {b.By}],\n");
        sb.Append("  \"land\": [\n");
        for (int y = 0; y < 8; y++)
        {
            sb.Append("    \"");
            for (int x = 0; x < 8; x++)
            {
                if (x > 0)
                {
                    sb.Append(' ');
                }

                sb.Append(b.LandId[y * 8 + x].ToString("X4", ci)).Append(':').Append(b.LandZ[y * 8 + x].ToString(ci));
            }

            sb.Append(y < 7 ? "\",\n" : "\"\n");
        }

        sb.Append("  ],\n");
        sb.Append("  \"statics\": [");
        List<WorldStatic> statics = b.Statics.OrderBy(s => s.Y).ThenBy(s => s.X).ThenBy(s => s.Z).ThenBy(s => s.Id).ToList();
        for (int i = 0; i < statics.Count; i++)
        {
            WorldStatic s = statics[i];
            sb.Append(i == 0 ? "\n" : ",\n");
            sb.Append($"    {{\"id\": \"0x{s.Id:X4}\", \"x\": {s.X}, \"y\": {s.Y}, \"z\": {s.Z.ToString(ci)}, \"hue\": \"0x{s.Hue:X4}\"}}");
        }

        sb.Append(statics.Count > 0 ? "\n  ]\n" : "]\n");
        sb.Append("}\n");

        Directory.CreateDirectory(BlockDir(b.Facet));
        string path = Path.Combine(BlockDir(b.Facet), $"{b.Key}.json");
        File.WriteAllText(path, sb.ToString());
        return path;
    }

    /// <summary>
    /// Reads a block as the map currently has it (the install, its patches,
    /// and any overlay already applied), through the same reader Chunk.Load uses.
    /// </summary>
    public static WorldBlock Capture(MapLoader maps, int facet, int bx, int by)
    {
        if (maps.BlockData[facet] == null)
        {
            maps.LoadMap(facet);
        }

        ref IndexMap im = ref maps.GetIndex(facet, bx, by);
        if (!im.IsValid() || im.MapFile == null)
        {
            return null;
        }

        var b = new WorldBlock { Facet = facet, Bx = bx, By = by };
        im.MapFile.Seek((long)im.MapAddress, SeekOrigin.Begin);
        MapBlock block = im.MapFile.Read<MapBlock>();
        for (int i = 0; i < 64; i++)
        {
            b.LandId[i] = block.Cells[i].TileID;
            b.LandZ[i] = block.Cells[i].Z;
        }

        if (im.StaticFile != null && im.StaticCount > 0)
        {
            im.StaticFile.Seek((long)im.StaticAddress, SeekOrigin.Begin);
            for (uint c = 0; c < im.StaticCount; c++)
            {
                StaticsBlock st = im.StaticFile.Read<StaticsBlock>();
                if (st.Color == 0 || st.Color == 0xFFFF)
                {
                    continue;
                }

                b.Statics.Add(new WorldStatic { Id = st.Color, X = st.X, Y = st.Y, Z = st.Z, Hue = st.Hue });
            }
        }

        return b;
    }

    /// <summary>
    /// Lays every block of a facet over the loaded map. Returns the block
    /// numbers changed, so the caller can reload those chunks. Blocks the
    /// project no longer has go back to the install's.
    /// </summary>
    public List<int> Apply(MapLoader maps, int facet)
    {
        if (maps.BlockData[facet] == null)
        {
            maps.LoadMap(facet);
        }

        int height = maps.MapBlocksSize[facet, 1];
        List<WorldBlock> blocks = Blocks(facet);
        var changed = new List<int>();

        // The scratch file: each block's map block then its statics, in the
        // client's own layout. A new name each time, as the old one stays
        // mapped until its reader is disposed below.
        string cache = Path.Combine(Root, ".cache");
        Directory.CreateDirectory(cache);
        string path = Path.Combine(cache, $"overlay_map{facet}_{++_generation}.bin");
        var offsets = new Dictionary<int, (long map, long statics, int count)>();
        using (var w = new BinaryWriter(File.Create(path)))
        {
            foreach (WorldBlock b in blocks)
            {
                int number = b.Bx * height + b.By;
                long mapAt = w.BaseStream.Position;
                w.Write(0u);
                for (int i = 0; i < 64; i++)
                {
                    w.Write(b.LandId[i]);
                    w.Write(b.LandZ[i]);
                }

                long staticsAt = w.BaseStream.Position;
                foreach (WorldStatic s in b.Statics)
                {
                    w.Write(s.Id);
                    w.Write(s.X);
                    w.Write(s.Y);
                    w.Write(s.Z);
                    w.Write(s.Hue);
                }

                offsets[number] = (mapAt, staticsAt, b.Statics.Count);
            }

            // A reader cannot map an empty file.
            if (w.BaseStream.Length == 0)
            {
                w.Write(0u);
            }
        }

        var reader = new UOFileMul(path);

        // Restore blocks the project dropped, before the old reader goes.
        foreach (var ((f, number), original) in _originals.ToList())
        {
            if (f == facet && !offsets.ContainsKey(number))
            {
                maps.BlockData[facet][number] = original;
                _originals.Remove((f, number));
                changed.Add(number);
            }
        }

        foreach (var (number, at) in offsets)
        {
            ref IndexMap im = ref maps.BlockData[facet][number];
            if (!_originals.ContainsKey((facet, number)))
            {
                _originals[(facet, number)] = im;
            }

            im.MapFile = reader;
            im.MapAddress = (ulong)at.map;
            im.StaticFile = reader;
            im.StaticAddress = (ulong)at.statics;
            im.StaticCount = (uint)at.count;
            changed.Add(number);
        }

        if (_overlayFiles.TryGetValue(facet, out UOFileMul old))
        {
            string oldPath = old.FilePath;
            old.Dispose();
            TryDelete(oldPath);
        }

        _overlayFiles[facet] = reader;
        return changed;
    }

    private static void TryDelete(string path)
    {
        try
        {
            File.Delete(path);
        }
        catch (IOException)
        {
        }
        catch (UnauthorizedAccessException)
        {
        }
    }

    /// <summary>Puts the install's blocks back and closes the scratch files.</summary>
    public void Dispose()
    {
        foreach (UOFileMul f in _overlayFiles.Values)
        {
            string p = f.FilePath;
            f.Dispose();
            TryDelete(p);
        }

        _overlayFiles.Clear();
    }

    /// <summary>
    /// Restores every overridden entry on a loaded map (before the scratch
    /// readers close) and returns the block numbers restored.
    /// </summary>
    public List<int> Restore(MapLoader maps, int facet)
    {
        var restored = new List<int>();
        foreach (var ((f, number), original) in _originals.ToList())
        {
            if (f == facet && maps.BlockData[facet] != null)
            {
                maps.BlockData[facet][number] = original;
                _originals.Remove((f, number));
                restored.Add(number);
            }
        }

        return restored;
    }
}
#endif
