#if TOOLS
namespace GUO.Editor;

using System;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.Linq;
using System.Security.Cryptography;
using System.Text;

internal sealed record ItemNameDraft(int Id, string SourceHash, string Name, string Flags, string Height);
internal sealed record ClilocTileChange(int TileId, int Id, string Kind, string Before, string After, string Origin, string Warning);

/// <summary>Exact-file name snapshot, including validated saved/unsaved project metadata.</summary>
internal sealed class TileNameSnapshot
{
    internal IReadOnlyList<string> Names { get; }
    internal IReadOnlyList<string> Origins { get; }
    internal string Stamp { get; }
    internal string Description { get; }
    private TileNameSnapshot(string[] names, string[] origins, string stamp, bool modern)
    { Names = Array.AsReadOnly(names); Origins = Array.AsReadOnly(origins); Stamp = stamp; Description = $"{names.Length:N0} {(modern ? "modern" : "legacy")} items · source ASCII names + explicit project name changes"; }

    internal static TileNameSnapshot Capture(string source, bool modern, string project, string install, IReadOnlyList<ItemNameDraft> drafts)
    {
        source = EditorPaths.Real(source);
        byte[] bytes;
        using (var input = File.OpenRead(source))
        {
            if (input.Length > 8 * 1024 * 1024) throw new IOException("Tiledata source exceeds 8 MiB.");
            bytes = new byte[checked((int)input.Length)]; input.ReadExactly(bytes);
        }
        int landBytes = 512 * (4 + 32 * (modern ? 30 : 26)), recordBytes = modern ? 41 : 37, blockBytes = 4 + 32 * recordBytes;
        if (bytes.Length < landBytes || (bytes.Length - landBytes) % blockBytes != 0)
            throw new IOException("Tiledata has incomplete blocks or does not match the configured client layout. Check the client version before synchronizing.");
        int count = (bytes.Length - landBytes) / blockBytes * 32;
        if (count > 0x10000) throw new IOException("Tiledata synchronization supports item IDs 0x0000–0xFFFF.");
        var names = new string[count]; var origins = Enumerable.Repeat("Source tiledata", count).ToArray();
        int Offset(int id) => landBytes + (id / 32) * blockBytes + 4 + (id % 32) * recordBytes;
        byte[] Record(int id)
        { if (id < 0 || id >= count) throw new IOException($"Item metadata 0x{id:X4} is outside this tiledata source."); return bytes.AsSpan(Offset(id), recordBytes).ToArray(); }
        for (int id = 0; id < count; id++)
        {
            ReadOnlySpan<byte> raw = bytes.AsSpan(Offset(id) + recordBytes - 20, 20);
            int nul = raw.IndexOf((byte)0); names[id] = Encoding.ASCII.GetString(nul < 0 ? raw : raw[..nul]);
        }
        var identity = new StringBuilder(source).Append('|').Append(modern).Append('|').Append(Convert.ToHexString(SHA256.HashData(bytes)));
        string folder = EditorPaths.ProjectFile(project, install, "metadata", "items");
        if (Directory.Exists(folder))
            foreach (string file in Directory.GetFiles(folder, "0x*.json").OrderBy(path => path, StringComparer.Ordinal))
            {
                string stem = Path.GetFileNameWithoutExtension(file);
                if (stem.Length != 6 || !int.TryParse(stem[2..], NumberStyles.AllowHexSpecifier, CultureInfo.InvariantCulture, out int id))
                    throw new IOException("Invalid item metadata filename. Repair project metadata before synchronizing.");
                if (!modern) throw new IOException("Project item metadata requires modern tiledata; choose the matching configured source layout.");
                string path = ItemMetadataDocument.DocumentPath(project, install, id);
                if (new FileInfo(path).Length > 16 * 1024) throw new IOException("Item metadata document exceeds 16 KiB.");
                string Hash() { using var stream = File.OpenRead(path); return Convert.ToHexString(SHA256.HashData(stream)); }
                string hash = Hash(); var doc = ItemMetadataDocument.Open(project, install, id, Record(id));
                if (Hash() != hash) throw new IOException("Project item metadata changed while building the preview. Open review again.");
                identity.Append('|').Append(id).Append(':').Append(hash);
                if (doc.Current.Name != doc.Original.Name) { names[id] = doc.Current.Name; origins[id] = "Saved project name"; }
            }
        var seen = new HashSet<int>();
        foreach (ItemNameDraft draft in drafts.OrderBy(row => row.Id))
        {
            if (!modern || !seen.Add(draft.Id)) throw new IOException("Duplicate or incompatible recovered item metadata draft.");
            var doc = new ItemMetadataDocument(draft.Id, Record(draft.Id));
            if (draft.SourceHash != doc.SourceHash) throw new IOException($"Item 0x{draft.Id:X4}: metadata draft belongs to another source. Reopen and repair metadata before synchronizing.");
            if (!int.TryParse(draft.Height, out int height)) throw new IOException($"Item 0x{draft.Id:X4}: repair invalid metadata height before synchronizing.");
            doc.Edit(draft.Name, ItemMetadataDocument.ParseFlags(draft.Flags), height);
            identity.Append('|').Append(draft.Id).Append(':').Append(draft.SourceHash).Append(':').Append(draft.Flags).Append(':').Append(draft.Height)
                .Append(':').Append(Encoding.UTF8.GetByteCount(draft.Name)).Append(':').Append(draft.Name);
            // An active draft reverting a saved project name must override that saved name too.
            if (doc.Current.Name != doc.Original.Name) { names[draft.Id] = doc.Current.Name; origins[draft.Id] = "Unsaved project name"; }
            else { ReadOnlySpan<byte> raw = bytes.AsSpan(Offset(draft.Id) + recordBytes - 20, 20); int nul = raw.IndexOf((byte)0);
                names[draft.Id] = Encoding.ASCII.GetString(nul < 0 ? raw : raw[..nul]); origins[draft.Id] = "Source name (project draft)"; }
        }
        EditorPaths.ProtectSource(source);
        string stamp = Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(identity.ToString())));
        return new(names, origins, stamp, modern);
    }
}

/// <summary>F16a/b: selected project-only sync, never a tiledata or Cliloc binary writer.</summary>
internal sealed class ClilocTileSyncPlan
{
    private readonly ClilocDocument _document;
    private readonly long _revision;
    private readonly TileNameSnapshot _source;
    private readonly Func<TileNameSnapshot> _capture;
    internal IReadOnlyList<ClilocTileChange> Changes { get; }
    internal string Description => _source.Description;
    internal static int ClilocId(int tileId)
    {
        if (tileId < 0 || tileId >= 0x10000) throw new ArgumentException("Item ID must be 0x0000–0xFFFF.");
        return tileId + (tileId < 0x4000 ? 1020000 : tileId < 0x8000 ? 1078872 : 1084024);
    }
    internal ClilocTileSyncPlan(ClilocDocument document, TileNameSnapshot source, Func<TileNameSnapshot> capture)
    {
        document.CheckSource(); _document = document; _revision = document.Revision; _source = source; _capture = capture;
        var rows = new List<ClilocTileChange>();
        for (int tile = 0; tile < source.Names.Count; tile++)
        {
            int id = ClilocId(tile); string before = document.Get(id)?.Text, after = string.IsNullOrWhiteSpace(source.Names[tile]) ? null : source.Names[tile];
            if (before == after) continue;
            rows.Add(new(tile, id, after == null ? "Remove" : before == null ? "Add" : "Update", before, after, source.Origins[tile], document.TokenWarning(id, after)));
        }
        Changes = rows.AsReadOnly();
    }
    internal void Apply(IEnumerable<int> selected)
    {
        _document.CheckSource();
        if (_document.Revision != _revision) throw new IOException("The Cliloc working document changed after review. Close and rebuild synchronization preview.");
        if (_capture().Stamp != _source.Stamp) throw new IOException("Tiledata or project metadata changed after review. Close and rebuild synchronization preview.");
        var ids = selected.ToHashSet();
        if (ids.Count == 0) throw new IOException("Select at least one synchronization change.");
        var rows = Changes.Where(row => ids.Contains(row.Id)).ToDictionary(row => row.Id, row => row.After);
        if (rows.Count != ids.Count) throw new IOException("Selection contains a change outside this synchronization plan.");
        _document.ApplySyncBatch(rows);
    }
}
#endif
