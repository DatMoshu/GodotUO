// SPDX-License-Identifier: BSD-2-Clause
using System.Reflection;
using System.Runtime.InteropServices;
using System.Security.Cryptography;
using System.Text.Json;
using ClassicUO.Assets;
using ClassicUO.IO;
using ClassicUO.Utility;

// Invoked only by run.py after its bounded container/RLE validation. No full
// installation load, settings lookup, loose-art override, GUI, or write access.
var identity = Assembly.GetExecutingAssembly().GetCustomAttributes<AssemblyMetadataAttribute>()
    .ToDictionary(x => x.Key, x => x.Value);
try
{
    if (string.IsNullOrEmpty(identity["OracleCommit"]) || string.IsNullOrEmpty(identity["OracleSourceHash"]))
        throw new InvalidOperationException("Unidentified build; run launchers/dev/fiddler_parity_doctor.bat --build.");
    if (args.Length == 1 && args[0] == "identity")
    {
        Console.WriteLine(JsonSerializer.Serialize(new { identity, runtime = Environment.Version.ToString() }));
        return 0;
    }
    if (args.Length != 4 && !(args.Length == 5 && args[1] == "texmap"))
        throw new ArgumentException("Expected stage, packaging, client version, comma-separated IDs, optional texture DEF path.");
    var stage = Path.GetFullPath(args[0]);
    if (!ClientVersionHelper.IsClientVersionValid(args[2], out var version))
        throw new ArgumentException("Invalid explicit client version.");
    var map = new UOFilesOverrideMap();
    // IsUOPInstallation checks MainMisc's existence. Only art is loaded; point
    // that probe at the explicitly selected archive, without fabricating files.
    map["mainmisc.uop"] = Path.Combine(stage, args[1] == "uop" ? "artLegacyMUL.uop" : "unused-oracle-probe");
    if (args[1] == "texmap" && args.Length == 5)
        map["texterr.def"] = args[4] == "-" ? Path.Combine(stage, "unused-oracle-texterr-probe") : Path.GetFullPath(args[4]);
    var files = new UOFileManager(version, stage, map);
    if (args[1] == "hue")
    {
        // The loader also reads radar colors. They do not participate in hue
        // decoding; explicitly alias this read-only probe to the same bounded
        // even-length file, avoiding a dependency on an unrelated install file.
        map["radarcol.mul"] = Path.Combine(stage, "hues.mul");
        files.Hues.Load();
        var hues = new List<object>();
        foreach (var text in args[3].Split(','))
        {
            int id = int.Parse(text);
            if (id < 1 || id > files.Hues.HuesCount) throw new InvalidDataException($"Missing hue {id}.");
            ref var group = ref files.Hues.HuesRange[(id - 1) / 8];
            ref var entry = ref group.Entries[(id - 1) % 8];
            byte[] raw = MemoryMarshal.AsBytes(MemoryMarshal.CreateReadOnlySpan(ref entry, 1)).ToArray();
            var colors = new ushort[32];
            for (int i = 0; i < 32; i++) colors[i] = entry.ColorTable[i];
            hues.Add(new { id, colors, start = entry.TableStart, end = entry.TableEnd,
                raw_base64 = Convert.ToBase64String(raw), header = group.Header });
        }
        Console.WriteLine(JsonSerializer.Serialize(new { identity, runtime = Environment.Version.ToString(), records = hues }));
        return 0;
    }
    if (args[1] == "texmap")
    {
        var indexPath = files.GetUOFilePath("texidx.mul");
        var mulPath = files.GetUOFilePath("texmaps.mul");
        long indexLength = new FileInfo(indexPath).Length, mulLength = new FileInfo(mulPath).Length;
        if (indexLength <= 0 || indexLength % 12 != 0 || indexLength > 0x4000 * 12 || mulLength > int.MaxValue)
            throw new InvalidDataException("Invalid bounded texture library.");
        byte[] rawIndex = File.ReadAllBytes(indexPath);
        for (int i = 0; i < rawIndex.Length; i += 12)
        {
            int at = BitConverter.ToInt32(rawIndex, i), length = BitConverter.ToInt32(rawIndex, i + 4);
            if (at >= 0 && length > 0 && ((length != 8192 && length != 32768) || (long)at + length > mulLength))
                throw new InvalidDataException("Invalid texture record range.");
        }
        var defPath = files.GetUOFilePath("TexTerr.def");
        if (File.Exists(defPath) && new FileInfo(defPath).Length > 2 * 1024 * 1024)
            throw new InvalidDataException("Texture DEF exceeds review budget.");
        // Track provenance with the upstream parser, in file order. A later
        // change to B must not retroactively change an earlier A { B } copy.
        int[] physical = Enumerable.Range(0, rawIndex.Length / 12).ToArray();
        if (File.Exists(defPath))
        {
            using var definitions = new DefReader(defPath);
            while (definitions.Next())
            {
                int id = definitions.ReadInt();
                if (id < 0 || id >= physical.Length) continue;
                var group = definitions.ReadGroup();
                if (group == null) continue;
                foreach (int other in group)
                    if (other >= 0 && other < physical.Length) physical[id] = physical[other];
            }
        }
        files.Texmaps.Load();
        using var textureFile = files.Texmaps.File;
        var textures = new List<object>();
        foreach (var text in args[3].Split(','))
        {
            int id = int.Parse(text);
            if (id < 0 || id >= physical.Length) throw new InvalidDataException("Texture ID outside existing rows.");
            int target = physical[id];
            ref var entry = ref textureFile.GetValidRefEntry(id);
            int at = BitConverter.ToInt32(rawIndex, target * 12), length = BitConverter.ToInt32(rawIndex, target * 12 + 4);
            if (entry.Length <= 0 || entry.Offset != at || entry.Length != length)
                throw new InvalidDataException("Texture mapping provenance differs from the upstream loader.");
            var raw = new byte[length];
            textureFile.Seek(entry.Offset, SeekOrigin.Begin); textureFile.Read(raw);
            var image = files.Texmaps.GetTexmap((uint)id);
            textures.Add(new { id, physical_id = target,
                affected_ids = Enumerable.Range(0, physical.Length).Where(x => physical[x] == target).ToArray(),
                width = image.Width, height = image.Height,
                rgba_sha256 = Convert.ToHexString(SHA256.HashData(MemoryMarshal.AsBytes(image.Pixels))).ToLowerInvariant(),
                raw_sha256 = Convert.ToHexString(SHA256.HashData(raw)).ToLowerInvariant() });
        }
        Console.WriteLine(JsonSerializer.Serialize(new { identity, runtime = Environment.Version.ToString(), records = textures }));
        return 0;
    }
    files.Arts.Load();
    using var archive = files.Arts.File;
    if ((archive is UOFileUop) != (args[1] == "uop")) throw new InvalidOperationException("Container selection differs.");
    var rows = new List<object>();
    foreach (var text in args[3].Split(','))
    {
        var id = int.Parse(text);
        ref var entry = ref archive.GetValidRefEntry(id + 0x4000);
        if (entry.Length <= 0) throw new InvalidDataException($"Missing static {id}.");
        archive.Seek(entry.Offset, SeekOrigin.Begin);
        var raw = new byte[entry.Length];
        archive.Read(raw);
        var image = files.Arts.GetArt((uint)(id + 0x4000));
        rows.Add(new { id, width = (int)image.Width, height = (int)image.Height,
            rgba = image.Pixels.ToArray(), raw_sha256 = Convert.ToHexString(SHA256.HashData(raw)).ToLowerInvariant() });
    }
    Console.WriteLine(JsonSerializer.Serialize(new { identity, runtime = Environment.Version.ToString(), records = rows }));
    return 0;
}
catch (Exception e)
{
    Console.Error.WriteLine($"Static-art oracle: {e.Message}");
    return 2;
}
