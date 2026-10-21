#if TOOLS
namespace GUO.Editor;

using System;
using System.Buffers.Binary;
using System.Collections.Generic;
using System.IO;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json.Nodes;
using Godot;

internal sealed record TextureSource(byte[] Record, string Identity);

/// <summary>One source-bound native texture working copy; a save is one atomic project JSON.</summary>
public sealed class TextureDocument
{
    private readonly Func<TextureSource> _source;
    private readonly byte[] _original;
    private readonly List<byte[]> _history = new();
    private int _cursor;
    private string _savedHash, _fileHash;
    private byte[] Current => _history[_cursor];
    public int Id { get; }
    public string Project { get; }
    public string Install { get; }
    public string SourceHash { get; }
    public string DocumentPath => EditorPaths.ProjectFile(Project, Install, "metadata", "textures", $"0x{Id:X4}.json");
    public string RecoveryKey => Project + "|" + Id + "|" + SourceHash;
    public string RecoveryError { get; internal set; }
    public int Size => Dimension(Current);
    public int SourceSize => Dimension(_original);
    public string RecordHash => Hash(Current);
    public bool IsDirty => RecordHash != _savedHash;
    public bool CanUndo => _cursor > 0;
    public bool CanRedo => _cursor + 1 < _history.Count;
    internal string ReviewToken => Hash(Encoding.UTF8.GetBytes(Snapshot().ToJsonString() + "|" + _fileHash));
    public string SourceError { get { try { Check(); return ""; } catch (Exception e) { return e.Message; } } }

    private TextureDocument(string project, string install, int id, Func<TextureSource> source)
    {
        if (id < 0 || id >= 0x4000) throw new ArgumentException("Texture ID must be below 0x4000.");
        Project = EditorPaths.Real(project); Install = EditorPaths.Real(install); Id = id; _source = source;
        TextureSource native = source(); Dimension(native.Record); _original = (byte[])native.Record.Clone();
        SourceHash = Fingerprint(native); _history.Add(_original); _savedHash = Hash(_original); _fileHash = FileHash();
    }
    private static string Hash(byte[] bytes) => Convert.ToHexString(SHA256.HashData(bytes)).ToLowerInvariant();
    private static string Fingerprint(TextureSource source)
    {
        using var hash = IncrementalHash.CreateHash(HashAlgorithmName.SHA256);
        hash.AppendData(Encoding.UTF8.GetBytes(source.Identity + "\0")); hash.AppendData(source.Record);
        return Convert.ToHexString(hash.GetHashAndReset()).ToLowerInvariant();
    }
    internal static int Dimension(byte[] record) => record?.Length switch
    { 8192 => 64, 32768 => 128, _ => throw new IOException("Texture records must be exactly 8,192 or 32,768 bytes.") };
    private string FileHash()
    {
        if (!File.Exists(DocumentPath)) return "missing";
        return Hash(Read(DocumentPath, 65536));
    }
    internal static byte[] Read(string path, int limit)
    {
        using var stream = File.OpenRead(path);
        if (stream.Length > limit) throw new IOException($"{Path.GetFileName(path)} exceeds {limit} bytes.");
        using var buffer = new MemoryStream(); byte[] block = new byte[8192]; int count;
        while ((count = stream.Read(block, 0, Math.Min(block.Length, limit + 1 - (int)buffer.Length))) > 0) buffer.Write(block, 0, count);
        if (buffer.Length > limit) throw new IOException("Input grew beyond its size limit.");
        return buffer.ToArray();
    }
    private void Check()
    {
        if (RecoveryError != null) throw new IOException(RecoveryError);
        if (Fingerprint(_source()) != SourceHash) throw new IOException("Source texture changed. Reopen against the original library before editing or saving.");
        if (FileHash() != _fileHash) throw new IOException("Saved texture copy changed outside this document. Reopen it to review that copy.");
    }
    private void Push(byte[] record)
    {
        Dimension(record); if (Current.AsSpan().SequenceEqual(record)) return;
        _history.RemoveRange(_cursor + 1, _history.Count - _cursor - 1); _history.Add((byte[])record.Clone()); _cursor++;
        if (_history.Count > 100) { _history.RemoveAt(0); _cursor--; }
    }
    internal void Import(byte[] record, string token)
    {
        Check(); if (ReviewToken != token) throw new IOException("Working texture changed. Reopen the image review.");
        Push(record);
    }
    public void Undo() { if (RecoveryError == null && CanUndo) _cursor--; }
    public void Redo() { if (RecoveryError == null && CanRedo) _cursor++; }
    public void Revert() { Check(); Push(_original); }
    public byte[] Record() => (byte[])Current.Clone();
    public byte[] SourceRecord() => (byte[])_original.Clone();
    internal static Image Image(byte[] record)
    {
        int size = Dimension(record); var pixels = new uint[size * size];
        for (int i = 0; i < pixels.Length; i++) pixels[i] = GUO.Utility.HuesHelper.Color16To32(BinaryPrimitives.ReadUInt16LittleEndian(record.AsSpan(i * 2))) | 0xFF000000;
        return EditorData.FromPixels(pixels, size, size);
    }
    internal static byte[] DecodePng(byte[] png)
    {
        byte[] signature = { 137, 80, 78, 71, 13, 10, 26, 10 };
        if (png == null || png.Length < 33 || png.Length > 2 * 1024 * 1024 || !png.AsSpan(0, 8).SequenceEqual(signature)
            || BinaryPrimitives.ReadUInt32BigEndian(png.AsSpan(8)) != 13 || Encoding.ASCII.GetString(png, 12, 4) != "IHDR")
            throw new IOException("Choose a PNG texture under 2 MiB with a standard IHDR header.");
        uint width = BinaryPrimitives.ReadUInt32BigEndian(png.AsSpan(16)), height = BinaryPrimitives.ReadUInt32BigEndian(png.AsSpan(20));
        if (width != height || width is not (64 or 128)) throw new IOException("Texture PNG must be 64 × 64 or 128 × 128. No resizing is performed.");
        using var image = new Godot.Image();
        if (image.LoadPngFromBuffer(png) != Error.Ok || image.GetWidth() != width || image.GetHeight() != height)
            throw new IOException("The PNG could not be decoded as the declared texture size.");
        image.Convert(Godot.Image.Format.Rgba8); byte[] rgba = image.GetData(), record = new byte[width * height * 2];
        for (int i = 0; i < record.Length / 2; i++)
            BinaryPrimitives.WriteUInt16LittleEndian(record.AsSpan(i * 2), (ushort)(((rgba[i * 4] >> 3) << 10) | ((rgba[i * 4 + 1] >> 3) << 5) | (rgba[i * 4 + 2] >> 3)));
        return record;
    }
    private JsonObject Snapshot() => new()
    {
        ["schema"] = 1, ["kind"] = "guo/texture", ["id"] = Id, ["source_sha256"] = SourceHash,
        ["record_base64"] = Convert.ToBase64String(Current), ["record_sha256"] = RecordHash,
    };
    private byte[] Parse(JsonNode row)
    {
        if ((int?)row?["schema"] != 1 || (string)row["kind"] != "guo/texture" || (int?)row["id"] != Id || (string)row["source_sha256"] != SourceHash)
            throw new IOException("Texture document belongs to another ID/source or schema.");
        byte[] record = Convert.FromBase64String((string)row["record_base64"] ?? ""); Dimension(record);
        if (Hash(record) != (string)row["record_sha256"]) throw new IOException("Texture record checksum differs.");
        return record;
    }
    internal static TextureDocument Open(string project, string install, int id, Func<TextureSource> source)
    {
        var document = new TextureDocument(project, install, id, source);
        if (File.Exists(document.DocumentPath))
        {
            byte[] json = Read(document.DocumentPath, 65536); byte[] record = document.Parse(JsonNode.Parse(json));
            if (Hash(json) != document._fileHash) throw new IOException("Texture copy changed while opening.");
            document._history[0] = record; document._savedHash = Hash(record);
        }
        return document;
    }
    public void Save()
    {
        Check(); byte[] json = Encoding.UTF8.GetBytes(Snapshot().ToJsonString(new() { WriteIndented = true }) + "\n");
        Directory.CreateDirectory(Path.GetDirectoryName(DocumentPath)); string temporary = DocumentPath + ".tmp-" + Guid.NewGuid().ToString("N");
        try { File.WriteAllBytes(temporary, json); Check(); File.Move(temporary, DocumentPath, true); }
        finally { if (File.Exists(temporary)) File.Delete(temporary); }
        _fileHash = Hash(json); _savedHash = RecordHash;
    }
    internal JsonObject Recovery() => new() { ["key"] = RecoveryKey, ["saved_file_sha256"] = _fileHash, ["snapshot"] = Snapshot() };
    internal void Restore(JsonNode row)
    {
        Check(); byte[] record = Parse(row?["snapshot"]);
        if ((string)row["key"] != RecoveryKey || (string)row["saved_file_sha256"] != _fileHash)
            throw new IOException("Recovered texture belongs to another saved copy.");
        Push(record);
    }
}
#endif
