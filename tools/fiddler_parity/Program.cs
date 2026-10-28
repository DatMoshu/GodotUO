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
    if (args.Length != 4 && !(args.Length == 5 && args[1] is "texmap" or "sound-mul" or "sound-uop"))
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
    if (args[1] is "sound-mul" or "sound-uop")
    {
        if (args.Length != 5) throw new ArgumentException("Sound oracle requires an explicit DEF path or -.");
        map["mainmisc.uop"] = Path.Combine(stage, args[1] == "sound-uop" ? "soundLegacyMUL.uop" : "unused-oracle-probe");
        map["sound.def"] = args[4] == "-" ? Path.Combine(stage, "unused-oracle-sound-def") : Path.GetFullPath(args[4]);
        map["music/digital/config.txt"] = map["music/config.txt"] = Path.Combine(stage, "unused-oracle-music");
    }
    var files = new UOFileManager(version, stage, map);
    if (args[1] is "sound-mul" or "sound-uop")
    {
        // Python bounds-checks the complete native library before invocation.
        // This reader resolves DEFs with the pinned parser and checks its
        // provenance against the actual SoundsLoader before reading PCM.
        bool uop = args[1] == "sound-uop";
        using UOFile native = uop
            ? new UOFileUop(files.GetUOFilePath("soundLegacyMUL.uop"), "build/soundlegacymul/{0:D8}.dat")
            : new UOFileMul(files.GetUOFilePath("sound.mul"), files.GetUOFilePath("soundidx.mul"));
        if (native.Length > 512 * 1024 * 1024) throw new InvalidDataException("Sound library exceeds 512 MiB.");
        native.FillEntries();
        if (native.Entries.Length > 0xFFFF + 0x4000) throw new InvalidDataException("Sound index exceeds its bounded ID range.");
        var mapped = native.Entries.ToArray();
        int[] physical = Enumerable.Range(0, mapped.Length).Select(i => mapped[i].Length > 0 ? i : -1).ToArray();
        foreach (var entry in mapped)
            if (entry.Length > 0 && (entry.Length < 42 || entry.Length > 64 * 1024 * 1024 + 40 || entry.Length % 2 != 0
                || entry.Offset < 0 || entry.Offset + entry.Length > native.Length || entry.CompressionFlag != CompressionType.None))
                throw new InvalidDataException("Invalid or compressed sound record.");
        string def = files.GetUOFilePath("sound.def");
        if (File.Exists(def))
        {
            if (new FileInfo(def).Length > 2 * 1024 * 1024) throw new InvalidDataException("Sound DEF exceeds 2 MiB.");
            using var reader = new DefReader(def);
            while (reader.Next())
            {
                int id = reader.ReadInt();
                if (id < 0 || id >= 0xFFFF || id >= mapped.Length || mapped[id].Length > 0) continue;
                int[] group = reader.ReadGroup();
                if (group == null) continue;
                foreach (int other in group)
                {
                    if (other < -1 || other >= 0xFFFF) continue;
                    if (other == -1) { mapped[id] = default; physical[id] = -1; }
                    else
                    {
                        if (other >= mapped.Length) throw new InvalidDataException("Sound DEF references beyond the native index.");
                        if (mapped[other].Length == 0) continue;
                        mapped[id] = mapped[other]; physical[id] = physical[other];
                    }
                }
            }
        }
        files.Sounds.Load();
        ((System.Collections.IDictionary)typeof(SoundsLoader).GetField("_ours", BindingFlags.Instance | BindingFlags.NonPublic)!.GetValue(files.Sounds)!).Clear();
        using var loaded = (UOFile)typeof(SoundsLoader).GetField("_file", BindingFlags.Instance | BindingFlags.NonPublic)!.GetValue(files.Sounds)!;
        if ((loaded is UOFileUop) != uop) throw new InvalidDataException("Sound container selection differs.");
        var sounds = new List<object>();
        foreach (string text in args[3].Split(','))
        {
            int id = int.Parse(text);
            if (id < 0 || id >= 0xFFFF || id >= physical.Length || physical[id] < 0)
                throw new InvalidDataException("Sound ID has no occupied native mapping.");
            ref var entry = ref loaded.GetValidRefEntry(id);
            if (entry.Offset != mapped[id].Offset || entry.Length != mapped[id].Length)
                throw new InvalidDataException("Sound DEF provenance differs from pinned loader.");
            if (!files.Sounds.TryGetSound(id, out byte[] pcm, out string name))
                throw new InvalidDataException("Pinned loader refused the native sound.");
            byte[] raw = new byte[entry.Length]; loaded.Seek(entry.Offset, SeekOrigin.Begin); loaded.Read(raw);
            byte[] nameBytes = System.Text.Encoding.UTF8.GetBytes(name);
            using var hash = IncrementalHash.CreateHash(HashAlgorithmName.SHA256);
            hash.AppendData(System.Text.Encoding.ASCII.GetBytes(nameBytes.Length.ToString(System.Globalization.CultureInfo.InvariantCulture) + ":"));
            hash.AppendData(nameBytes); hash.AppendData(pcm);
            sounds.Add(new { id, physical_id = physical[id],
                affected_ids = Enumerable.Range(0, Math.Min(physical.Length, 0xFFFF)).Where(i => physical[i] == physical[id]).ToArray(),
                name, samples = pcm.Length / 2, source_sha256 = Convert.ToHexString(hash.GetHashAndReset()).ToLowerInvariant(),
                pcm_sha256 = Convert.ToHexString(SHA256.HashData(pcm)).ToLowerInvariant(),
                raw_base64 = Convert.ToBase64String(raw), raw_sha256 = Convert.ToHexString(SHA256.HashData(raw)).ToLowerInvariant() });
        }
        Console.WriteLine(JsonSerializer.Serialize(new { identity, runtime = Environment.Version.ToString(), records = sounds }));
        return 0;
    }
    if (args[1] == "cliloc-read")
    {
        if (Path.GetFileName(args[3]) != args[3] || args[3] is "." or "..")
            throw new InvalidDataException("Expected one Cliloc source basename.");
        string path = Path.Combine(stage, args[3]);
        long size = new FileInfo(path).Length;
        if (size < 6 || size > 64 * 1024 * 1024) throw new InvalidDataException("Cliloc must contain 6 bytes to 64 MiB.");
        byte[] raw = File.ReadAllBytes(path), plain = raw;
        bool compressed = raw[3] == 0x8e;
        if (compressed)
        {
            if (raw.Length < 1029) throw new InvalidDataException("Truncated compressed Cliloc.");
            byte[] table = Enumerable.Range(0, 256).Select(i => (byte)i).ToArray(), prefix = new byte[1024];
            for (int i = 0; i < prefix.Length; i++)
            {
                int index = raw[i + 4]; byte value = table[index];
                for (int j = index; j > 0; j--) table[j] = table[j - 1];
                table[0] = prefix[i] = value;
            }
            long decodedLength = 0;
            for (int i = 0; i < 256; i++)
            {
                int frequency = System.Buffers.Binary.BinaryPrimitives.ReadInt32LittleEndian(prefix.AsSpan(i * 4));
                if (frequency < 0) throw new InvalidDataException("Invalid compressed Cliloc frequency.");
                decodedLength += frequency;
            }
            if (decodedLength < 6 || decodedLength > 64 * 1024 * 1024 || decodedLength + 1024 > raw.Length - 4)
                throw new InvalidDataException("Compressed Cliloc output exceeds its bounded input or 64 MiB.");
            plain = BwtDecompress.Decompress(raw);
        }
        var ids = new SortedSet<int>(); int at = 6, count = 0;
        var utf8 = new System.Text.UTF8Encoding(false, true);
        if (plain.Length < 6 || plain.Length > 64 * 1024 * 1024 || plain[3] == 0x8e)
            throw new InvalidDataException("Invalid decoded Cliloc header.");
        while (at < plain.Length)
        {
            if (plain.Length - at < 7 || ++count > 500_000) throw new InvalidDataException("Truncated or oversized Cliloc table.");
            int id = System.Buffers.Binary.BinaryPrimitives.ReadInt32LittleEndian(plain.AsSpan(at));
            int length = System.Buffers.Binary.BinaryPrimitives.ReadInt16LittleEndian(plain.AsSpan(at + 5));
            at += 7;
            if (id < 0 || length < 0 || length > plain.Length - at) throw new InvalidDataException("Invalid Cliloc record bounds.");
            utf8.GetString(plain, at, length); ids.Add(id); at += length;
        }
        // Invoke the pinned loader's exact-file reader, without language fallback
        // or loose Clilocs.txt overrides from an unrelated staged file.
        var loader = new ClilocLoader(files);
        typeof(ClilocLoader).GetMethod("ReadCliloc", BindingFlags.Instance | BindingFlags.NonPublic)!
            .Invoke(loader, new object[] { path });
        using var hash = IncrementalHash.CreateHash(HashAlgorithmName.SHA256);
        byte[] key = new byte[8];
        foreach (int id in ids)
        {
            byte[] text = utf8.GetBytes(loader.GetString(id));
            System.Buffers.Binary.BinaryPrimitives.WriteInt32LittleEndian(key, id);
            System.Buffers.Binary.BinaryPrimitives.WriteInt32LittleEndian(key.AsSpan(4), text.Length);
            hash.AppendData(key); hash.AppendData(text);
        }
        Console.WriteLine(JsonSerializer.Serialize(new { identity, runtime = Environment.Version.ToString(), compressed,
            record_count = count, effective_count = ids.Count, plain_base64 = Convert.ToBase64String(plain),
            source_sha256 = Convert.ToHexString(SHA256.HashData(raw)).ToLowerInvariant(),
            text_sha256 = Convert.ToHexString(hash.GetHashAndReset()).ToLowerInvariant() }));
        return 0;
    }
    if (args[1] == "tiledata-item")
    {
        const int landBytes = 512 * (4 + 32 * 30), groupBytes = 4 + 32 * 41;
        var path = files.GetUOFilePath("tiledata.mul");
        long length = new FileInfo(path).Length;
        if (version < ClientVersion.CV_7090 || length < landBytes || (length - landBytes) % groupBytes != 0
            || (length - landBytes) / groupBytes > 2048)
            throw new InvalidDataException("Expected bounded modern tiledata, version 7.0.9.0 or later.");
        files.TileData.Load();
        using var stream = File.OpenRead(path);
        var metadata = new List<object>();
        foreach (var text in args[3].Split(','))
        {
            int id = int.Parse(text);
            if (id < 0 || id >= files.TileData.StaticData.Length) throw new InvalidDataException("Item is outside tiledata.");
            ref var item = ref files.TileData.StaticData[id];
            stream.Position = landBytes + id / 32 * groupBytes + 4 + id % 32 * 41;
            var raw = new byte[41]; stream.ReadExactly(raw);
            metadata.Add(new { id, flags = item.Flags, weight = item.Weight, layer = item.Layer, count = item.Count,
                anim = item.AnimID, hue = item.Hue, light = item.LightIndex, height = item.Height, name = item.Name,
                raw_base64 = Convert.ToBase64String(raw), raw_sha256 = Convert.ToHexString(SHA256.HashData(raw)).ToLowerInvariant() });
        }
        Console.WriteLine(JsonSerializer.Serialize(new { identity, runtime = Environment.Version.ToString(), records = metadata }));
        return 0;
    }
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
