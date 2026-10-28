#if TOOLS
namespace GUO.Editor;

using System;
using System.Buffers.Binary;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json.Nodes;

/// <summary>Source-bound item document; native output is a separately reviewed staged copy.</summary>
public sealed class ItemMetadataDocument
{
    public readonly record struct Fields(string Name, ulong Flags, byte Height);
    private readonly List<Fields> _history = new();
    private int _cursor;
    private readonly byte[] _original;
    private Fields _saved;
    private string _documentPath, _fileHash;
    public int Id { get; }
    public Fields Current => _history[_cursor];
    public Fields Original { get; }
    public bool CanUndo => _cursor > 0;
    public bool CanRedo => _cursor + 1 < _history.Count;
    public bool IsDirty => Current != _saved;
    public string SourceHash => Convert.ToHexString(SHA256.HashData(_original)).ToLowerInvariant();
    internal string ReviewToken => Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(
        Id + "|" + SourceHash + "|" + _documentPath + "|" + _fileHash + "|" + Convert.ToBase64String(PreviewRecord())))).ToLowerInvariant();

    public ItemMetadataDocument(int id, byte[] record)
    {
        if (id < 0 || id >= 0x10000 || record.Length != 41) throw new ArgumentException("Expected a modern 41-byte item record.");
        Id = id;
        _original = (byte[])record.Clone();
        int nameLength = Array.IndexOf(record, (byte)0, 21, 20);
        Original = new Fields(Encoding.UTF8.GetString(record, 21, nameLength < 0 ? 20 : nameLength - 21),
            BinaryPrimitives.ReadUInt64LittleEndian(record), record[20]);
        _history.Add(Original);
        _saved = Original;
    }

    public void Edit(string name, ulong flags, int height)
    {
        if (height < 0 || height > 255) throw new ArgumentException("Height must be from 0 to 255.");
        if (name == null || name.Contains('\0') || (name != Original.Name && Encoding.UTF8.GetByteCount(name) > 20))
            throw new ArgumentException("The name must fit 20 UTF-8 bytes and contain no NUL characters.");
        var next = new Fields(name, flags, (byte)height);
        if (next == Current) return;
        _history.RemoveRange(_cursor + 1, _history.Count - _cursor - 1);
        _history.Add(next);
        _cursor++;
        if (_history.Count > 100) { _history.RemoveAt(0); _cursor--; }
    }

    public void Undo() { if (CanUndo) _cursor--; }
    public void Redo() { if (CanRedo) _cursor++; }
    public void Revert() => Edit(Original.Name, Original.Flags, Original.Height);

    /// <summary>Exact working bytes; native staging separately preserves unexposed staged fields.</summary>
    public byte[] PreviewRecord()
    {
        byte[] record = (byte[])_original.Clone();
        BinaryPrimitives.WriteUInt64LittleEndian(record, Current.Flags);
        record[20] = Current.Height;
        if (Current.Name != Original.Name)
        {
            Array.Clear(record, 21, 20);
            Encoding.UTF8.GetBytes(Current.Name).CopyTo(record, 21);
        }
        return record;
    }

    public static ulong ParseFlags(string text)
    {
        string digits = text?.StartsWith("0x", StringComparison.OrdinalIgnoreCase) == true ? text[2..] : text;
        if (!ulong.TryParse(digits, NumberStyles.AllowHexSpecifier, CultureInfo.InvariantCulture, out ulong flags))
            throw new ArgumentException("Flags must be hexadecimal, from 0x0 to 0xFFFFFFFFFFFFFFFF.");
        return flags;
    }

    public static byte[] ReadRecord(string path, int id)
    {
        const int landBytes = 512 * (4 + 32 * 30);
        const int itemBlock = 4 + 32 * 41;
        if (id < 0 || id >= 0x10000) throw new ArgumentException("Invalid item ID.");
        using var stream = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.ReadWrite);
        if (stream.Length < landBytes || (stream.Length - landBytes) % itemBlock != 0)
            throw new IOException("Item metadata documents currently require modern tiledata (7.0.9.0 or later).");
        long offset = landBytes + (id / 32) * itemBlock + 4 + (id % 32) * 41;
        if (offset + 41 > stream.Length) throw new IOException("Item is outside tiledata.");
        stream.Position = offset;
        byte[] record = new byte[41];
        stream.ReadExactly(record);
        return record;
    }

    public void Save(string project, string install, string source)
    {
        if (!ReadRecord(source, Id).AsSpan().SequenceEqual(_original))
            throw new IOException("Source item metadata changed. Reopen the document before saving.");
        string path = DocumentPath(project, install, Id);
        CheckSavedCopy(path);
        var json = new JsonObject
        {
            ["schema"] = 1, ["kind"] = "guo/item-metadata", ["id"] = Id,
            ["layout"] = "modern-41", ["original"] = Convert.ToBase64String(_original),
            ["source_sha256"] = SourceHash, ["name"] = Current.Name,
            ["flags"] = $"0x{Current.Flags:X16}", ["height"] = Current.Height,
        };
        Directory.CreateDirectory(Path.GetDirectoryName(path));
        string temporary = path + ".tmp-" + Guid.NewGuid().ToString("N");
        try
        {
            File.WriteAllText(temporary, json.ToJsonString(new() { WriteIndented = true }) + "\n");
            CheckSavedCopy(path);
            if (!ReadRecord(source, Id).AsSpan().SequenceEqual(_original)) throw new IOException("Source item metadata changed during save.");
            File.Move(temporary, path, overwrite: true);
        }
        finally { if (File.Exists(temporary)) File.Delete(temporary); }
        _documentPath = path; _fileHash = SavedHash(path);
        _saved = Current;
    }

    private static string SavedHash(string path)
        => File.Exists(path) ? Convert.ToHexString(SHA256.HashData(TextureDocument.Read(path, 16 * 1024))).ToLowerInvariant() : "missing";
    private void CheckSavedCopy(string path)
    {
        if (_documentPath == null)
        {
            if (File.Exists(path)) throw new IOException("A saved metadata copy already exists. Reopen it before saving or exporting.");
        }
        else if (path != _documentPath || SavedHash(path) != _fileHash)
            throw new IOException("Saved metadata copy changed outside this document. Reopen it before saving or exporting.");
    }
    internal void RequireNativeReview(string project, string install, string source, string token)
    {
        if (!ReadRecord(source, Id).AsSpan().SequenceEqual(_original)) throw new IOException("Source item metadata changed. Reopen native export against the current source.");
        CheckSavedCopy(DocumentPath(project, install, Id));
        if (ReviewToken != token) throw new IOException("Working item metadata changed. Reopen native export for a fresh review.");
    }
    internal byte[] NativeStageInput(string project, string install, string source, string token)
    {
        RequireNativeReview(project, install, source, token);
        return Encoding.UTF8.GetBytes(new JsonObject { ["schema"] = 1, ["domain"] = "item-metadata",
            ["records"] = new JsonArray(new JsonObject { ["id"] = Id, ["source_record_base64"] = Convert.ToBase64String(_original),
                ["record_base64"] = Convert.ToBase64String(PreviewRecord()) }) }.ToJsonString());
    }

    public static ItemMetadataDocument Open(string project, string install, int id, byte[] source)
    {
        string path = DocumentPath(project, install, id);
        if (!File.Exists(path)) return new ItemMetadataDocument(id, source) { _documentPath = path, _fileHash = "missing" };
        byte[] savedBytes = TextureDocument.Read(path, 16 * 1024);
        JsonNode json = JsonNode.Parse(savedBytes) ?? throw new IOException("Empty item document.");
        if ((int?)json["schema"] != 1 || (string)json["kind"] != "guo/item-metadata"
            || (string)json["layout"] != "modern-41" || (int?)json["id"] != id)
            throw new IOException("Unsupported or mismatched item document.");
        byte[] original = Convert.FromBase64String((string)json["original"] ?? "");
        if (!source.AsSpan().SequenceEqual(original)) throw new IOException("Item document belongs to a different source record.");
        var result = new ItemMetadataDocument(id, original) { _documentPath = path,
            _fileHash = Convert.ToHexString(SHA256.HashData(savedBytes)).ToLowerInvariant() };
        if ((string)json["source_sha256"] != result.SourceHash) throw new IOException("Item document baseline hash differs.");
        result.Edit((string)json["name"], ParseFlags((string)json["flags"] ?? ""), (int?)json["height"] ?? -1);
        result._saved = result.Current;
        result.CheckSavedCopy(path);
        return result;
    }

    private static string RealPath(string path)
    {
        string full = Path.GetFullPath(path);
        string current = Path.GetPathRoot(full);
        foreach (string segment in full[current.Length..].Split(Path.DirectorySeparatorChar, StringSplitOptions.RemoveEmptyEntries))
        {
            current = Path.Combine(current, segment);
            FileSystemInfo info = Directory.Exists(current) ? new DirectoryInfo(current) : new FileInfo(current);
            if (info.LinkTarget != null) current = info.ResolveLinkTarget(returnFinalTarget: true)?.FullName
                ?? throw new IOException("Could not resolve document path link.");
        }
        return Path.GetFullPath(current);
    }

    public static string DocumentPath(string project, string install, int id)
    {
        if (id < 0 || id >= 0x10000) throw new ArgumentException("Invalid item ID.");
        string root = RealPath(project).TrimEnd(Path.DirectorySeparatorChar);
        string protectedRoot = RealPath(install).TrimEnd(Path.DirectorySeparatorChar);
        string target = RealPath(Path.Combine(root, "metadata", "items", $"0x{id:X4}.json"));
        var comparison = OperatingSystem.IsWindows() ? StringComparison.OrdinalIgnoreCase : StringComparison.Ordinal;
        if (!target.StartsWith(root + Path.DirectorySeparatorChar, comparison)
            || target.Equals(protectedRoot, comparison) || target.StartsWith(protectedRoot + Path.DirectorySeparatorChar, comparison))
            throw new IOException("Item documents must stay in the project, outside the client installation.");
        return target;
    }
}
#endif
