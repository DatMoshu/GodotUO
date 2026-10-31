#if TOOLS
namespace GUO.Editor;

using System;
using System.Buffers.Binary;
using System.IO;
using System.Linq;
using Godot;
using GUO.Host;
using GUO.IO;

public partial class EditorSmoke
{
    private void CheckTextures()
    {
        void Check(string name, bool ok) { _assetReport["texture_" + name] = ok; if (!ok) AssetFail("Textures: " + name); }
        var panel = _assets.Panel<TexturePanel>(); _assets.ShowPanel(panel); panel.FilterSize(0); panel.Search("");
        int[] ids = panel.ResultAssetIds();
        Check("valid_native_entries_only", ids.Length > 0 && ids.All(id => _data.TextureSize(id) is 64 or 128));
        foreach (int size in new[] { 64, 128 })
        {
            panel.FilterSize(size); panel.Search(""); int[] matches = panel.ResultAssetIds();
            Check($"filter_{size}", matches.Length == ids.Count(id => _data.TextureSize(id) == size)
                && matches.All(id => _data.TextureSize(id) == size));
            if (matches.Length == 0) { _assetReport[$"texture_{size}_fixture"] = "No native entry of this size in selected library"; continue; }
            int id = matches[0]; Check($"hex_id_{size}", panel.Search($"0x{id:X4}") == id && _inspector.Current.Source == "Textures");
            using Image image = _data.TextureImage(id); Check($"dimensions_{size}", image?.GetSize() == new Vector2I(size, size));
            Check($"opaque_{size}", image != null && Opaque(image) == size * size);
            string path = Path.Combine(_out, $"texture-export-{size}{Suffix}.png"); AssetActions.ExportPng(image, path);
            using Image reopened = Image.LoadFromFile(path); Check($"png_roundtrip_{size}", SamePixels(image, reopened));
            var reference = panel.LandReferences(id).FirstOrDefault(t => t.Name.Length > 0 && !int.TryParse(t.Name, out _));
            if (reference.Name != null)
            {
                panel.Search(reference.Name); Check($"land_name_search_{size}", panel.ResultAssetIds().Contains(id));
            }
        }
        panel.FilterSize(0); int chosen = ids[0]; Check("decimal_id", panel.Search(chosen.ToString()) == chosen);
        Check("export_action", _inspector.Current.Actions.Any(a => a.Id == "export-texture-png"));
        Check("land_references_in_visible_fields", _inspector.Current.Fields.Any(f => f.Name == "Land tiles" && f.Value.Length > 0));
        Check("invalid_ids_safe", _data.TextureImage(-1) == null && _data.TextureImage(0x4000) == null);
        var entry = new UOFileIndex(null, 0, 8192, 0);
        Check("zero_offset_valid", EditorData.TextureEntrySize(entry, 8192) == 64);
        Check("short_file_refused", EditorData.TextureEntrySize(entry, 8191) == 0);
        entry.Offset = long.MaxValue; Check("overflow_offset_refused", EditorData.TextureEntrySize(entry, 8192) == 0);
        entry.Offset = -1; Check("negative_offset_refused", EditorData.TextureEntrySize(entry, 8192) == 0);
        entry.Offset = 0; entry.Length = 8193; Check("unknown_length_refused", EditorData.TextureEntrySize(entry, 32768) == 0);
        // Exercise the effective-entry guard on the live loader without calling its unsafe decoder.
        UOFileIndex original = _data.Files.Texmaps.File.Entries[chosen];
        try { _data.Files.Texmaps.File.Entries[chosen] = entry; Check("malformed_entry_not_decoded", _data.TextureImage(chosen) == null); }
        finally { _data.Files.Texmaps.File.Entries[chosen] = original; }
        string synthetic = Path.Combine(_out, "texture-fixtures" + Suffix); Directory.CreateDirectory(synthetic);
        byte[] pixels = new byte[8192 + 32768];
        foreach (int at in new[] { 0, 8192 })
            foreach (var (pixel, word) in new[] { (0, (ushort)0), (1, (ushort)0xFC00), (2, (ushort)0x03E0), (3, (ushort)0x001F) })
                BinaryPrimitives.WriteUInt16LittleEndian(pixels.AsSpan(at + pixel * 2), word);
        File.WriteAllBytes(Path.Combine(synthetic, "texmaps.mul"), pixels);
        byte[] index = new byte[36];
        foreach (var (id, at, length) in new[] { (0, 0, 8192), (1, 8192, 32768), (2, -1, -1) })
        {
            BinaryPrimitives.WriteInt32LittleEndian(index.AsSpan(id * 12), at);
            BinaryPrimitives.WriteInt32LittleEndian(index.AsSpan(id * 12 + 4), length);
        }
        File.WriteAllBytes(Path.Combine(synthetic, "texidx.mul"), index);
        File.WriteAllText(Path.Combine(synthetic, "TexTerr.def"), "2 { 1 }\n");
        var files = new GUO.Assets.UOFileManager(UoDataProbe.ParseVersion(_data.ClientVersion), synthetic); files.Texmaps.Load();
        using (files.Texmaps.File)
            foreach (int id in new[] { 0, 1, 2 })
            {
                var texture = files.Texmaps.GetTexmap((uint)id);
                Check($"literal_colors_{id}", texture.Pixels[0] == 0xFF000000 && texture.Pixels[1] == 0xFF0000FF
                    && texture.Pixels[2] == 0xFF00FF00 && texture.Pixels[3] == 0xFFFF0000);
                Check($"native_and_remap_size_{id}", texture.Width == (id == 0 ? 64 : 128) && texture.Height == texture.Width);
            }
        panel.Search(panel.SmokeQuery);
    }
}
#endif
