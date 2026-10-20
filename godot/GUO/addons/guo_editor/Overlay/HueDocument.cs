#if TOOLS
namespace GUO.Editor;

using System;
using System.Buffers.Binary;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.Linq;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json.Nodes;

/// <summary>Undoable hue authoring on the existing format-1 project overlay.</summary>
public sealed class HueDocument
{
    private sealed record State(string Name, ushort Start, ushort End, ushort[] Colors);
    private readonly byte[] _original;
    private readonly Func<byte[]> _source;
    private readonly List<State> _history = new();
    private readonly State _native;
    private State _saved;
    private int _cursor;
    private JsonObject _extra = new();
    private string _fileHash;
    private State Current => _history[_cursor];
    public int Id { get; }
    public string Project { get; }
    public string Install { get; }
    public string Path => EditorPaths.ProjectFile(Project, Install, "assets", "hues", $"0x{Id:X4}.json");
    public string SourceHash { get; }
    public string RecoveryKey => Project + "|" + Id + "|" + SourceHash;
    public string Name => Current.Name;
    public string SourceName => _native.Name;
    public ushort Start => Current.Start;
    public ushort End => Current.End;
    public int Index { get; private set; }
    public string DraftName, DraftStart, DraftEnd, DraftColor;
    public string RecoveryError { get; internal set; }
    public Action Saved;
    public bool IsDirty => !Equal(Current, _saved);
    public bool CanUndo => _cursor > 0;
    public bool CanRedo => _cursor + 1 < _history.Count;
    public bool Pending => DraftName != Name || DraftStart != $"0x{Start:X4}" || DraftEnd != $"0x{End:X4}" || DraftColor != $"0x{ColorAt(Index):X4}";
    public ushort ColorAt(int index) => Current.Colors[index];
    public ushort[] Colors() => (ushort[])Current.Colors.Clone();
    public string NativeName => Encoding.ASCII.GetString(Encoding.ASCII.GetBytes(Name).Take(20).ToArray());
    public string NameWarning => Name != NativeName ? "Native hue names use the first 20 ASCII bytes. The complete authored name stays in the project copy." : "";
    public string SourceError
    {
        get { try { CheckSource(); return ""; } catch (Exception e) { return e.Message; } }
    }
    public string Validation
    {
        get { try { ParsedDraft(); return ""; } catch (Exception e) { return e.Message; } }
    }
    private HueDocument(string project, string install, int id, Func<byte[]> source)
    {
        if (id < 1 || id > 0xFFFF) throw new ArgumentException("Hue IDs are one-based, 1–65535.");
        Project = EditorPaths.Real(project); Install = EditorPaths.Real(install); Id = id; _source = source;
        _original = (byte[])source().Clone();
        if (_original.Length != 88) throw new IOException("Expected an 88-byte loaded hue record.");
        SourceHash = Hash(_original);
        ushort U16(int offset) => BinaryPrimitives.ReadUInt16LittleEndian(_original.AsSpan(offset));
        int nul = Array.IndexOf(_original, (byte)0, 68, 20);
        string name = Encoding.ASCII.GetString(_original, 68, nul < 0 ? 20 : nul - 68).Replace('\r', ' ').Replace('\n', ' ');
        _native = new(name, U16(64), U16(66), Enumerable.Range(0, 32).Select(i => U16(i * 2)).ToArray());
        _history.Add(_native); _saved = _native; _fileHash = FileHash(); ResetDraft();
    }
    private static string Hash(byte[] bytes) => Convert.ToHexString(SHA256.HashData(bytes)).ToLowerInvariant();
    private string FileHash()
    {
        if (!File.Exists(Path)) return "missing";
        using var stream = File.OpenRead(Path);
        if (stream.Length > 32768) throw new IOException("Hue project document exceeds 32 KiB.");
        return Convert.ToHexString(SHA256.HashData(stream)).ToLowerInvariant();
    }
    private void CheckSource()
    {
        if (!_source().AsSpan().SequenceEqual(_original)) throw new IOException("Loaded base hue changed. Reopen against its original library before saving.");
        if (FileHash() != _fileHash) throw new IOException("Saved project hue changed outside this document. Use Reopen hue to review that copy.");
    }
    public static ushort ParseHex(string text)
    {
        string value = text?.StartsWith("0x", StringComparison.OrdinalIgnoreCase) == true ? text[2..] : text;
        if (!ushort.TryParse(value, NumberStyles.AllowHexSpecifier, CultureInfo.InvariantCulture, out ushort result))
            throw new ArgumentException("Hue values must be hexadecimal from 0x0000 to 0xFFFF.");
        return result;
    }
    private static void ValidateName(string name)
    {
        if (name == null || name.Contains('\0') || name.Contains('\n') || name.Contains('\r') || Encoding.UTF8.GetByteCount(name) > 4096)
            throw new ArgumentException("Project hue name must fit 4,096 UTF-8 bytes with no NUL or line breaks.");
    }
    private State ParsedDraft()
    {
        ValidateName(DraftName); ushort start = ParseHex(DraftStart), end = ParseHex(DraftEnd), color = ParseHex(DraftColor);
        ushort[] colors = Colors(); colors[Index] = color; return new(DraftName, start, end, colors);
    }
    private static bool Equal(State a, State b) => a.Name == b.Name && a.Start == b.Start && a.End == b.End && a.Colors.AsSpan().SequenceEqual(b.Colors);
    private void Push(State state)
    {
        if (Equal(Current, state)) return;
        _history.RemoveRange(_cursor + 1, _history.Count - _cursor - 1); _history.Add(state); _cursor++;
        if (_history.Count > 100) { _history.RemoveAt(0); _cursor--; }
    }
    private void ResetDraft() { DraftName = Name; DraftStart = $"0x{Start:X4}"; DraftEnd = $"0x{End:X4}"; DraftColor = $"0x{ColorAt(Index):X4}"; }
    public bool CommitDraft()
    {
        if (RecoveryError != null || Validation.Length > 0 || SourceError.Length > 0) return false;
        if (Pending) Push(ParsedDraft()); ResetDraft(); return true;
    }
    public bool SelectColor(int index)
    {
        if (index < 0 || index >= 32) throw new ArgumentOutOfRangeException(nameof(index));
        if (!CommitDraft()) return false; Index = index; ResetDraft(); return true;
    }
    public void ReplacePalette(ushort[] colors)
    {
        if (colors == null || colors.Length != 32) throw new ArgumentException("A hue palette needs exactly 32 colors.");
        if (!CommitDraft()) throw new IOException(RecoveryError ?? (SourceError.Length > 0 ? SourceError : Validation));
        Push(new(Name, Start, End, (ushort[])colors.Clone())); ResetDraft();
    }
    public void Undo() { if (RecoveryError != null) return; if (!Pending && CanUndo) _cursor--; ResetDraft(); }
    public void Redo() { if (RecoveryError != null || Pending) return; if (CanRedo) _cursor++; ResetDraft(); }
    public void Revert() { if (RecoveryError != null) return; CheckSource(); Push(_native); ResetDraft(); }
    public byte[] PreviewRecord()
    {
        byte[] bytes = (byte[])_original.Clone();
        for (int i = 0; i < 32; i++) BinaryPrimitives.WriteUInt16LittleEndian(bytes.AsSpan(i * 2), ColorAt(i));
        BinaryPrimitives.WriteUInt16LittleEndian(bytes.AsSpan(64), Start); BinaryPrimitives.WriteUInt16LittleEndian(bytes.AsSpan(66), End);
        if (Name != SourceName) { Array.Clear(bytes, 68, 20); Encoding.ASCII.GetBytes(NativeName).CopyTo(bytes, 68); }
        return bytes;
    }
    private JsonObject Snapshot()
    {
        var row = (JsonObject)_extra.DeepClone();
        row["format"] = 1; row["hue"] = Id; row["name"] = Name;
        row["table_start"] = $"0x{Start:X4}"; row["table_end"] = $"0x{End:X4}";
        row["colors"] = new JsonArray(Colors().Select(c => (JsonNode)JsonValue.Create($"{c:X4}")).ToArray());
        row["source_sha256"] = SourceHash; row["source_record_base64"] = Convert.ToBase64String(_original); return row;
    }
    private State ParseSnapshot(JsonObject row)
    {
        if ((int?)row["format"] != 1 || (int?)row["hue"] != Id || row["colors"] is not JsonArray colors || colors.Count != 32)
            throw new IOException("Hue copy has an invalid format, ID or palette length.");
        if (row.ContainsKey("source_sha256") || row.ContainsKey("source_record_base64"))
            if ((string)row["source_sha256"] != SourceHash || !Convert.FromBase64String((string)row["source_record_base64"] ?? "").AsSpan().SequenceEqual(_original))
                throw new IOException("Hue copy belongs to another loaded base palette. Restore its original source library.");
        string name = (string)row["name"]; ValidateName(name);
        return new(name, ParseHex((string)row["table_start"]), ParseHex((string)row["table_end"]), colors.Select(c => ParseHex((string)c)).ToArray());
    }
    public static HueDocument Open(string project, string install, int id, Func<byte[]> source)
    {
        var document = new HueDocument(project, install, id, source);
        if (File.Exists(document.Path))
        {
            var row = JsonNode.Parse(File.ReadAllText(document.Path)) as JsonObject ?? throw new IOException("Invalid hue project JSON.");
            State state = document.ParseSnapshot(row);
            if (document.FileHash() != document._fileHash) throw new IOException("Hue copy changed while opening. Reopen it again.");
            document._extra = (JsonObject)row.DeepClone(); document._history[0] = state; document._saved = state; document.ResetDraft();
        }
        return document;
    }
    public void Save()
    {
        CheckSource(); if (!CommitDraft()) throw new IOException(RecoveryError ?? (SourceError.Length > 0 ? SourceError : Validation));
        byte[] bytes = Encoding.UTF8.GetBytes(Snapshot().ToJsonString(new() { WriteIndented = true }) + "\n");
        if (bytes.Length > 32768) throw new IOException("Hue project document exceeds 32 KiB.");
        Directory.CreateDirectory(System.IO.Path.GetDirectoryName(Path)); string temporary = Path + ".tmp-" + Guid.NewGuid().ToString("N");
        try { File.WriteAllBytes(temporary, bytes); CheckSource(); File.Move(temporary, Path, true); }
        finally { if (File.Exists(temporary)) File.Delete(temporary); }
        _fileHash = Hash(bytes); _saved = Current; Saved?.Invoke();
    }
    internal JsonObject Recovery() => new()
    {
        ["key"] = RecoveryKey, ["saved_file_sha256"] = _fileHash, ["snapshot"] = Snapshot(), ["index"] = Index,
        ["raw_name"] = DraftName, ["raw_start"] = DraftStart, ["raw_end"] = DraftEnd, ["raw_color"] = DraftColor,
    };
    internal void Restore(JsonNode row)
    {
        if ((string)row?["key"] != RecoveryKey || (string)row["saved_file_sha256"] != _fileHash || (int?)row["index"] is not int index || index < 0 || index >= 32)
            throw new IOException("Recovered hue draft belongs to another source or saved copy.");
        State state = ParseSnapshot(row["snapshot"] as JsonObject ?? throw new IOException("Invalid hue draft."));
        string[] raw = new[] { "raw_name", "raw_start", "raw_end", "raw_color" }.Select(k => (string)row[k] ?? throw new IOException("Hue draft is missing raw fields.")).ToArray();
        if (raw.Any(v => v.Length > 8192)) throw new IOException("Recovered hue field is too large.");
        Push(state); Index = index; DraftName = raw[0]; DraftStart = raw[1]; DraftEnd = raw[2]; DraftColor = raw[3];
    }
}
#endif
