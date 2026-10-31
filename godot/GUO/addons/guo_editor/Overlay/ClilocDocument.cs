#if TOOLS
namespace GUO.Editor;

using System;
using System.Buffers.Binary;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json.Nodes;
using System.Text.RegularExpressions;
using GUO.Utility;

/// <summary>F15a: one language's source-bound working patch; never a native cliloc writer.</summary>
public sealed class ClilocDocument
{
    public sealed record Entry(byte Flag, string Text);
    private sealed record EditStep(int Id, Entry Before, Entry After);
    private static readonly UTF8Encoding Utf8 = new(false, true);
    private readonly Dictionary<int, Entry> _source = new(), _changes = new();
    private Dictionary<int, Entry> _saved = new();
    private readonly List<EditStep[]> _history = new();
    private int _cursor;
    private string _savedFileHash = "missing";
    public string Language { get; }
    public string Project { get; }
    public string Install { get; }
    public string SourceFile { get; }
    public string SourceHash { get; }
    internal bool IsCustom => Language is "custom1" or "custom2";
    internal string SourceIdentity { get; }
    internal string DisplayName => IsCustom ? $"Custom {Language[^1]} · {Path.GetFileName(SourceFile)}" : Language.ToUpperInvariant();
    internal string RecoveryIdentity => Project + "|" + Language + "|" + (IsCustom ? SourceIdentity + "|" : "") + SourceHash;
    private readonly byte[] _header;
    public byte[] Header => (byte[])_header.Clone();
    public bool Compressed { get; }
    public int SourceRecords { get; }
    public int DuplicateIds => SourceRecords - _source.Count;
    public bool CanUndo => _cursor > 0;
    public bool CanRedo => _cursor < _history.Count;
    public int ChangeCount => _changes.Count;
    public bool IsDirty => !Same(_changes, _saved);
    public IEnumerable<int> Ids => _source.Keys.Concat(_changes.Keys).Distinct().Where(id => Get(id) != null).OrderBy(id => id);
    public IEnumerable<int> ChangedIds => _changes.Keys.OrderBy(id => id);
    public IEnumerable<int> ReviewIds => _source.Keys.Concat(_changes.Keys).Distinct().OrderBy(id => id);
    public bool Recovered { get; private set; }
    internal long Revision { get; private set; }
    internal string NativeReviewToken => Convert.ToHexString(SHA256.HashData(Utf8.GetBytes(
        RecoveryIdentity + "|" + Revision + "|" + _savedFileHash + "|" + Snapshot().ToJsonString()))).ToLowerInvariant();

    private ClilocDocument(string project, string install, string language, string source, byte[] raw)
    {
        if (!Regex.IsMatch(language, "^(?:[a-z]{3}|custom[12])$")) throw new IOException("Choose an installed three-letter language or custom source slot 1/2.");
        Project = Path.GetFullPath(project); Install = Path.GetFullPath(install); Language = language; SourceFile = EditorPaths.Real(source);
        string identity = OperatingSystem.IsWindows() ? SourceFile.ToUpperInvariant() : SourceFile;
        SourceIdentity = Convert.ToHexString(SHA256.HashData(Utf8.GetBytes(identity))).ToLowerInvariant();
        if (raw.Length < 6 || raw.Length > 64 * 1024 * 1024) throw new IOException("Cliloc file must contain 6 bytes to 64 MiB.");
        SourceHash = Convert.ToHexString(SHA256.HashData(raw)).ToLowerInvariant();
        Compressed = raw[3] == 0x8e;
        byte[] bytes = Compressed ? DecompressBounded(raw) : raw;
        if (bytes.Length < 6) throw new IOException("Truncated cliloc file header.");
        _header = bytes[..6];
        int at = 6, count = 0;
        while (at < bytes.Length)
        {
            if (bytes.Length - at < 7 || ++count > 500_000) throw new IOException("Truncated or oversized cliloc record table.");
            int id = BinaryPrimitives.ReadInt32LittleEndian(bytes.AsSpan(at));
            byte flag = bytes[at + 4];
            int length = BinaryPrimitives.ReadUInt16LittleEndian(bytes.AsSpan(at + 5)); at += 7;
            if (id < 0 || length > bytes.Length - at) throw new IOException("Invalid cliloc record ID or text length.");
            try { _source[id] = new Entry(flag, Utf8.GetString(bytes, at, length)); }
            catch (DecoderFallbackException) { throw new IOException("Cliloc text is not valid UTF-8; no document was changed."); }
            at += length;
        }
        SourceRecords = count;
        EditorPaths.ProtectSource(SourceFile);
    }

    // The ported BWT decoder owns decoding. Validate its frequency prefix first
    // so malformed input cannot request an unbounded output allocation.
    private static byte[] DecompressBounded(byte[] raw)
    {
        if (raw.Length < 1029) throw new IOException("Truncated compressed cliloc.");
        byte[] table = Enumerable.Range(0, 256).Select(i => (byte)i).ToArray(), prefix = new byte[1024];
        for (int i = 0; i < prefix.Length; i++)
        {
            int index = raw[i + 4]; byte value = table[index];
            for (int j = index; j > 0; j--) table[j] = table[j - 1];
            table[0] = prefix[i] = value;
        }
        long length = 0;
        for (int i = 0; i < 256; i++)
        {
            int frequency = BinaryPrimitives.ReadInt32LittleEndian(prefix.AsSpan(i * 4));
            if (frequency < 0) throw new IOException("Invalid compressed cliloc frequency table.");
            length += frequency;
        }
        if (length < 6 || length > 64 * 1024 * 1024 || length + 1024 > raw.Length - 4)
            throw new IOException("Compressed cliloc output exceeds its bounded input or 64 MiB.");
        try { return BwtDecompress.Decompress(raw); }
        catch (Exception e) when (e is ArgumentException or IndexOutOfRangeException or EndOfStreamException)
        { throw new IOException("Malformed compressed cliloc; source is unchanged."); }
    }

    public Entry Get(int id) => _changes.TryGetValue(id, out Entry value) ? value : _source.GetValueOrDefault(id);
    public Entry Original(int id) => _source.GetValueOrDefault(id);
    public bool Changed(int id) => _changes.ContainsKey(id);

    public void Edit(int id, string text)
    {
        ApplyTextBatch(new Dictionary<int, string> { [id] = text });
    }
    internal void ApplyTextBatch(IReadOnlyDictionary<int, string> rows, bool validateOnly = false)
        => ApplyTextBatch(rows, validateOnly, false);
    internal void ApplySyncBatch(IReadOnlyDictionary<int, string> rows, bool validateOnly = false)
        => ApplyTextBatch(rows, validateOnly, true);
    private void ApplyTextBatch(IReadOnlyDictionary<int, string> rows, bool validateOnly, bool allowRemovals)
    {
        var edits = new List<EditStep>();
        foreach (var row in rows)
        {
            if (row.Key < 0) throw new ArgumentException("Cliloc IDs must be non-negative whole numbers.");
            Entry before = Get(row.Key);
            if (row.Value == null && allowRemovals)
            { if (before != null) edits.Add(new(row.Key, before, null)); continue; }
            // Untouched long source records are legal and must survive a whole-language roundtrip.
            if (before != null && before.Text == row.Value) continue;
            if (row.Value == null || row.Value.Contains('\0') || Utf8.GetByteCount(row.Value) > 32767)
                throw new ArgumentException($"String {row.Key}: text must fit 32,767 UTF-8 bytes and contain no NUL.");
            edits.Add(new(row.Key, before, new Entry(before?.Flag ?? Original(row.Key)?.Flag ?? 0, row.Value)));
        }
        Apply(edits.ToArray(), validateOnly);
    }
    public void Delete(int id) => Set(id, null);
    public void Revert(int id) => Set(id, Original(id));
    private void Set(int id, Entry next)
    {
        if (id < 0) throw new ArgumentException("Cliloc IDs must be non-negative whole numbers.");
        Entry before = Get(id); if (before == next) return;
        Apply(new[] { new EditStep(id, before, next) });
    }
    private void Apply(EditStep[] edits, bool validateOnly = false)
    {
        if (edits.Length == 0) return;
        var candidate = new Dictionary<int, Entry>(_changes);
        foreach (var edit in edits)
            if (edit.After == Original(edit.Id)) candidate.Remove(edit.Id); else candidate[edit.Id] = edit.After;
        if (candidate.Count > 10_000 || candidate.Values.Where(entry => entry != null).Sum(entry => (long)Utf8.GetByteCount(entry.Text)) > 2 * 1024 * 1024)
            throw new ArgumentException("Working patch exceeds 10,000 changes or 2 MiB of text.");
        if (validateOnly) return;
        _history.RemoveRange(_cursor, _history.Count - _cursor); _history.Add(edits); _cursor++;
        foreach (var edit in edits) Assign(edit.Id, edit.After);
        Revision++;
        if (_history.Count > 100) { _history.RemoveAt(0); _cursor--; }
    }
    private void Assign(int id, Entry entry)
    { if (entry == Original(id)) _changes.Remove(id); else _changes[id] = entry; }
    public void Undo() { if (CanUndo) { foreach (EditStep edit in _history[--_cursor]) Assign(edit.Id, edit.Before); Revision++; } }
    public void Redo() { if (CanRedo) { foreach (EditStep edit in _history[_cursor++]) Assign(edit.Id, edit.After); Revision++; } }
    private static bool Same(Dictionary<int, Entry> a, Dictionary<int, Entry> b) => a.Count == b.Count && a.All(row => b.TryGetValue(row.Key, out Entry value) && value == row.Value);

    public string TokenWarning(int id, string text)
    {
        string[] Tokens(string value) => Regex.Matches(value ?? "", @"~\d+(?:_[^~]*)?~|#\d+").Select(match => match.Value).OrderBy(value => value).ToArray();
        return Tokens(Original(id)?.Text).SequenceEqual(Tokens(text)) ? "" : "Placeholder/reference tokens differ from source; check the consuming script.";
    }

    public JsonObject Snapshot()
    {
        var rows = new JsonArray();
        foreach (var row in _changes.OrderBy(row => row.Key))
            rows.Add(new JsonObject { ["id"] = row.Key, ["deleted"] = row.Value == null, ["flag"] = (int?)row.Value?.Flag, ["text"] = row.Value?.Text });
        var snapshot = new JsonObject { ["schema"] = 1, ["kind"] = "guo/cliloc-document", ["language"] = Language,
            ["encoding"] = "UTF-8", ["source_sha256"] = SourceHash, ["source_compressed"] = Compressed,
            ["header"] = Convert.ToBase64String(Header), ["changes"] = rows };
        if (IsCustom) snapshot["source_identity"] = SourceIdentity;
        return snapshot;
    }

    public void Restore(JsonNode json, bool saved)
    {
        if ((int?)json["schema"] != 1 || (string)json["kind"] != "guo/cliloc-document" || (string)json["language"] != Language
            || (string)json["source_sha256"] != SourceHash || (string)json["encoding"] != "UTF-8"
            || (IsCustom && (string)json["source_identity"] != SourceIdentity)
            || (string)json["header"] != Convert.ToBase64String(Header) || (bool?)json["source_compressed"] != Compressed)
            throw new IOException("Cliloc document belongs to another source or language.");
        var seen = new HashSet<int>(); var replacement = new Dictionary<int, Entry>(); int bytes = 0;
        foreach (JsonNode row in json["changes"].AsArray())
        {
            int id = (int)row["id"];
            if (id < 0 || !seen.Add(id) || seen.Count > 10_000) throw new IOException("Invalid or duplicate document ID.");
            Entry entry = null;
            if ((bool?)row["deleted"] != true)
            {
                string text = (string)row["text"];
                int flag = (int?)row["flag"] ?? -1;
                if (text == null || text.Contains('\0') || Utf8.GetByteCount(text) > 32767 || flag < 0 || flag > 255
                    || (Original(id) != null ? Original(id).Flag != flag : flag != 0)) throw new IOException("Invalid document text or altered source record flag.");
                bytes += Utf8.GetByteCount(text); entry = new((byte)flag, text);
            }
            if (bytes > 2 * 1024 * 1024) throw new IOException("Document text exceeds 2 MiB.");
            if (entry != Original(id)) replacement[id] = entry;
        }
        _changes.Clear(); foreach (var row in replacement) _changes.Add(row.Key, row.Value);
        _history.Clear(); _cursor = 0; Revision++;
        if (saved) _saved = new(_changes); else Recovered = true;
    }

    public string DocumentPath => IsCustom
        ? EditorPaths.ProjectFile(Project, Install, "metadata", "cliloc", "custom", SourceIdentity, SourceHash, Language + ".json")
        : EditorPaths.ProjectFile(Project, Install, "metadata", "cliloc", Language + ".json");
    internal string WritablePath(string path)
    {
        string target = EditorPaths.OutsideInstall(path, Install);
        if (target.Equals(SourceFile, OperatingSystem.IsWindows() ? StringComparison.OrdinalIgnoreCase : StringComparison.Ordinal))
            throw new IOException("The selected Cliloc source file is read-only. Choose a different project/export destination.");
        return target;
    }
    public void Save()
    {
        CheckSource(); CheckSavedCopy();
        string path = WritablePath(DocumentPath), json = Snapshot().ToJsonString(new() { WriteIndented = true });
        if (Encoding.UTF8.GetByteCount(json) > 16 * 1024 * 1024) throw new IOException("Cliloc document exceeds 16 MiB.");
        Directory.CreateDirectory(Path.GetDirectoryName(path));
        string temporary = path + ".tmp-" + Guid.NewGuid().ToString("N");
        try { File.WriteAllText(temporary, json + "\n"); CheckSource(); CheckSavedCopy(); File.Move(temporary, path, true); }
        finally { if (File.Exists(temporary)) File.Delete(temporary); }
        _savedFileHash = SavedHash(); _saved = new(_changes); Recovered = false;
    }
    private string SavedHash() => File.Exists(DocumentPath)
        ? Convert.ToHexString(SHA256.HashData(TextureDocument.Read(DocumentPath, 16 * 1024 * 1024))).ToLowerInvariant() : "missing";
    private void CheckSavedCopy()
    {
        if (SavedHash() != _savedFileHash) throw new IOException("Saved Cliloc copy changed outside this document. Reopen the source document before saving or exporting.");
    }
    internal void RequireNativeReview(string token)
    {
        CheckSource(); CheckSavedCopy();
        if (NativeReviewToken != token) throw new IOException("Working Cliloc changed. Reopen native export for a fresh review.");
    }
    internal byte[] NativeStageInput(string token)
    {
        RequireNativeReview(token); JsonObject input = Snapshot();
        if (IsCustom) input["native_source_file"] = SourceFile;
        return Utf8.GetBytes(input.ToJsonString());
    }
    internal void CheckSource()
    {
        using var source = File.OpenRead(SourceFile);
        if (source.Length > 64 * 1024 * 1024 || Convert.ToHexString(SHA256.HashData(source)).ToLowerInvariant() != SourceHash)
            throw new IOException("Source cliloc changed; reopen the source document before saving or exchanging CSV.");
    }

    public static ClilocDocument Open(string project, string install, string language, string source)
    {
        if (new FileInfo(source).Length > 64 * 1024 * 1024) throw new IOException("Cliloc file exceeds 64 MiB.");
        var document = new ClilocDocument(project, install, language, source, File.ReadAllBytes(source));
        if (File.Exists(document.DocumentPath))
        {
            byte[] saved = TextureDocument.Read(document.DocumentPath, 16 * 1024 * 1024);
            document.Restore(JsonNode.Parse(saved), true);
            document._savedFileHash = Convert.ToHexString(SHA256.HashData(saved)).ToLowerInvariant();
        }
        document.CheckSavedCopy(); document.CheckSource();
        return document;
    }
}
#endif
