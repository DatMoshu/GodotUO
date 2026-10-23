#if TOOLS
namespace GUO.Editor;

using System;
using System.Collections.Generic;
using System.IO;
using System.Reflection;
using System.Text;
using GUO.Assets;
using GUO.IO;
using GUO.Store;

public partial class EditorSmoke
{
    private void CheckSyntheticSoundMetadata()
    {
        string folder = Path.Combine(_out, "sound_metadata_fixture");
        Directory.CreateDirectory(folder);
        byte[] raw = new byte[256];
        Encoding.UTF8.GetBytes("synthetic archive").CopyTo(raw, 0);
        Encoding.UTF8.GetBytes("synthetic empty").CopyTo(raw, 44);
        Encoding.UTF8.GetBytes("synthetic short").CopyTo(raw, 84);
        Encoding.UTF8.GetBytes("synthetic truncated").CopyTo(raw, 210);
        string archivePath = Path.Combine(folder, "sound.bin");
        File.WriteAllBytes(archivePath, raw);
        using var archive = new UOFile(archivePath);
        archive.Entries = new UOFileIndex[15];
        for (int i = 0; i < archive.Entries.Length; i++) archive.Entries[i] = new UOFileIndex(archive, 0, 44, 0);
        archive.Entries[1] = archive.Entries[0]; // The alias state produced by Sound.def.
        archive.Entries[2] = new UOFileIndex(archive, 44, 40, 0);
        archive.Entries[3] = new UOFileIndex(archive, 84, 20, 0);
        archive.Entries[4] = new UOFileIndex(archive, 210, 80, 0);
        archive.Entries[5] = new UOFileIndex(archive, -1, 44, 0);
        archive.Entries[6] = new UOFileIndex(archive, 0xFFFFFFFFL, 44, 0);
        archive.Entries[13] = new UOFileIndex(archive, 0, 0, 0);
        archive.Entries[14] = new UOFileIndex(archive, 0, -1, 0);

        // Construct only; do not Load/Dispose the manager's other loaders, whose
        // legacy static caches belong to the real editor. All fixture I/O is owned here.
        var files = new UOFileManager(_data.Files.Version, folder);
        var loader = files.Sounds;
        const BindingFlags flags = BindingFlags.Instance | BindingFlags.NonPublic;
        typeof(SoundsLoader).GetField("_file", flags).SetValue(loader, archive);
        var ours = (Dictionary<int, string>)typeof(SoundsLoader).GetField("_ours", flags).GetValue(loader);
        string Wave(int id, int rate, int length)
        {
            string path = Path.Combine(folder, id + ".wav");
            using var writer = new BinaryWriter(File.Create(path));
            writer.Write(Encoding.ASCII.GetBytes("RIFF")); writer.Write(36 + length);
            writer.Write(Encoding.ASCII.GetBytes("WAVEfmt ")); writer.Write(16);
            writer.Write((short)1); writer.Write((short)1); writer.Write(rate);
            writer.Write(rate * 2); writer.Write((short)2); writer.Write((short)16);
            writer.Write(Encoding.ASCII.GetBytes("data")); writer.Write(length); writer.Write(new byte[length]);
            return path;
        }
        ours[7] = Wave(7, 22050, 8);
        ours[8] = Wave(8, 44100, 8);
        ours[9] = Path.Combine(folder, "missing.wav");
        ours[10] = Wave(10, 22050, 0);
        ours[11] = Wave(11, 22050, 8);
        ours[12] = Wave(12, 22050, 8);
        using var content = new StoreRuntimeContent();
        var sounds = (Dictionary<int, byte[]>)typeof(StoreRuntimeContent).GetField("_sounds", flags).GetValue(content);
        sounds[11] = new byte[12]; sounds[12] = Array.Empty<byte>(); sounds[-1] = new byte[2];
        typeof(UOFileManager).GetProperty("Content", flags).SetValue(files, content);

        var checks = new Dictionary<string, object>();
        _report["sound_metadata_synthetic"] = checks;
        foreach (var item in new[]
        {
            ("archive", 0, true, 4, "synthetic archive"), ("alias", 1, true, 4, "synthetic archive"),
            ("empty_archive", 2, true, 0, "synthetic empty"), ("short_entry", 3, false, 0, ""),
            ("truncated_body", 4, true, 40, "synthetic truncated"), ("negative_offset", 5, false, 0, ""),
            ("sentinel_offset", 6, false, 0, ""), ("loose", 7, true, 8, "7"),
            ("invalid_loose_fallback", 8, true, 4, "synthetic archive"),
            ("missing_loose_fallback", 9, true, 4, "synthetic archive"),
            ("empty_loose", 10, true, 0, "10"), ("pack_over_loose", 11, true, 12, "pack-sound-11"),
            ("empty_pack_over_loose", 12, true, 0, "pack-sound-12"),
            ("zero_entry", 13, false, 0, ""), ("negative_length", 14, false, 0, ""),
            ("absent", 99, false, 0, ""), ("negative_id", -2, false, 0, ""),
            ("pack_before_negative_id", -1, true, 2, "pack-sound--1"),
        })
        {
            (bool Found, string Name, int Bytes, Type Error, long Position, long StreamPosition) Read(bool metadata)
            {
                archive.Seek(5, SeekOrigin.Begin);
                try
                {
                    bool found;
                    string name;
                    int bytes;
                    if (metadata) found = loader.TryGetSoundMetadata(item.Item2, out name, out bytes);
                    else { found = loader.TryGetSound(item.Item2, out byte[] data, out name); bytes = data?.Length ?? 0; }
                    return (found, name, bytes, null, archive.Position, archive.Reader.BaseStream.Position);
                }
                catch (Exception ex) { return (false, null, 0, ex.GetType(), archive.Position, archive.Reader.BaseStream.Position); }
            }
            var original = Read(false);
            var metadata = Read(true);
            bool ok = original == metadata;
            if (item.Item1 == "short_entry") ok &= original.Error != null;
            else ok &= original.Error == null && original.Found == item.Item3 && original.Bytes == item.Item4
                && (original.Name ?? "").TrimEnd('\0') == item.Item5;
            if (item.Item1 == "truncated_body") ok &= metadata.Position == 290 && metadata.StreamPosition == 256;
            if (item.Item1 is "pack_over_loose" or "empty_pack_over_loose" or "pack_before_negative_id")
                ok &= metadata.Position == 5 && metadata.StreamPosition == 5;
            checks[item.Item1] = new Dictionary<string, object>
            {
                ["ok"] = ok, ["bytes"] = metadata.Bytes, ["error_type"] = metadata.Error?.Name,
                ["position"] = metadata.Position, ["stream_position"] = metadata.StreamPosition,
            };
            if (!ok) _failures.Add("Sound metadata synthetic " + item.Item1 + " disagreed with original lookup");
        }
        bool packOwned = loader.TryGetSound(11, out byte[] owned, out _) && ReferenceEquals(owned, sounds[11]);
        checks["pack_ownership"] = new Dictionary<string, object> { ["ok"] = packOwned };
        if (!packOwned) _failures.Add("Sound metadata fixture altered pack ownership");
    }
}
#endif
