#if TOOLS
namespace GUO.Editor;

using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json.Nodes;

/// <summary>Source-bound name and PCM document. Native sound archives are never modified.</summary>
public sealed class SoundDocument
{
    public const int MaximumNameBytes = 32;
    private sealed record State(string Name, byte[] Pcm, string Hash);
    private readonly Func<(byte[] Pcm, string Name)> _readSource;
    private readonly State _original;
    private readonly List<State> _history = new();
    private int _cursor;
    private State _saved;
    private State Current => _history[_cursor];
    public string Project { get; }
    public string Install { get; }
    public int Id { get; }
    public string SourceName => _original.Name;
    public string SourceHash { get; }
    public string CurrentName => Current.Name;
    public string DraftName { get; set; }
    public string RecoveryError { get; internal set; }
    public bool CanUndo => _cursor > 0;
    public bool CanRedo => _cursor + 1 < _history.Count;
    public bool Pending => DraftName != CurrentName;
    public bool IsDirty => Current.Name != _saved.Name || Current.Hash != _saved.Hash;
    public bool HasPcmChange => Current.Hash != _original.Hash;
    public int Samples => Current.Pcm.Length / 2;
    public int SourceSamples => _original.Pcm.Length / 2;
    public string PcmHash => Current.Hash;
    public string DocumentPath => EditorPaths.ProjectFile(Project, Install, "metadata", "sounds", $"0x{Id:X4}.json");
    public string RecoveryKey => Project + "|" + Id + "|" + SourceHash;

    private SoundDocument(string project, string install, int id, Func<(byte[] Pcm, string Name)> readSource)
    {
        if (id < 0 || id >= 0xFFFF) throw new ArgumentException("Sound ID is outside the loader's effect range.");
        Project = EditorPaths.Real(project); Install = EditorPaths.Real(install); Id = id; _readSource = readSource;
        var source = readSource(); SoundWave.ValidateLength(source.Pcm?.Length ?? 0);
        SourceHash = Fingerprint(source);
        string rawName = source.Name ?? ""; int terminator = rawName.IndexOf('\0');
        string name = (terminator < 0 ? rawName : rawName[..terminator]).TrimEnd(' ');
        _original = new(name, (byte[])source.Pcm.Clone(), Hash(source.Pcm));
        _history.Add(_original); _saved = _original; DraftName = name;
    }

    private static string Hash(byte[] pcm) => Convert.ToHexString(SHA256.HashData(pcm)).ToLowerInvariant();

    private static string Fingerprint((byte[] Pcm, string Name) source)
    {
        using var hash = IncrementalHash.CreateHash(HashAlgorithmName.SHA256);
        byte[] name = Encoding.UTF8.GetBytes(source.Name ?? "");
        hash.AppendData(Encoding.ASCII.GetBytes(name.Length.ToString(System.Globalization.CultureInfo.InvariantCulture) + ":"));
        hash.AppendData(name); hash.AppendData(source.Pcm ?? Array.Empty<byte>());
        return Convert.ToHexString(hash.GetHashAndReset()).ToLowerInvariant();
    }

    private void CheckSource()
    { if (Fingerprint(_readSource()) != SourceHash) throw new IOException("Active source sound changed. Reopen the document before editing or saving."); }
    public byte[] Pcm() => (byte[])Current.Pcm.Clone();
    public byte[] SourcePcm() => (byte[])_original.Pcm.Clone();
    internal void ExportWave(string path) { CheckSource(); SoundWave.Export(Current.Pcm, path); }
    private bool ValidName(string name) => name != null && !name.Contains('\0') && (name == SourceName || Encoding.UTF8.GetByteCount(name) <= MaximumNameBytes);
    public string Validation => ValidName(DraftName) ? "" : "Name must fit 32 UTF-8 bytes and contain no NUL. The draft is retained.";
    private void Push(State next)
    {
        if (next.Name == Current.Name && next.Hash == Current.Hash) return;
        _history.RemoveRange(_cursor + 1, _history.Count - _cursor - 1); _history.Add(next); _cursor++;
        // PCM histories share immutable buffers. Bound retained sample memory as well as operation count.
        while (_history.Count > 100 || (_history.Count > 2 && _history.Select(s => s.Hash).Distinct().Sum(h => (long)_history.First(s => s.Hash == h).Pcm.Length) > 128L * 1024 * 1024))
        { _history.RemoveAt(0); _cursor--; }
    }
    public bool CommitDraft()
    {
        if (RecoveryError != null || Validation.Length > 0) return false;
        if (Pending) Push(new(DraftName, Current.Pcm, Current.Hash));
        return true;
    }
    public void ImportWave(byte[] wave)
    {
        byte[] pcm = SoundWave.Decode(wave);
        if (RecoveryError != null || Validation.Length > 0) throw new IOException(RecoveryError ?? Validation);
        CheckSource(); CommitDraft(); Push(new(CurrentName, pcm, Hash(pcm)));
    }
    public void Undo()
    {
        if (RecoveryError != null) return;
        if (Pending && !CommitDraft()) { DraftName = CurrentName; return; }
        if (CanUndo) _cursor--; DraftName = CurrentName;
    }
    public void Redo() { if (!CommitDraft()) return; if (CanRedo) _cursor++; DraftName = CurrentName; }
    public void Revert() { if (RecoveryError != null) return; Push(_original); DraftName = SourceName; }

    public JsonObject Snapshot() => new()
    {
        ["schema"] = 2, ["kind"] = "guo/sound-effect", ["id"] = Id,
        ["source_sha256"] = SourceHash, ["name"] = CurrentName,
        ["pcm_sha256"] = HasPcmChange ? Current.Hash : null,
    };
    internal string BlobPath(string hash)
    {
        if (hash == null || hash.Length != 64 || !hash.All(c => c is >= '0' and <= '9' or >= 'a' and <= 'f')) throw new IOException("Invalid project PCM hash.");
        return EditorPaths.ProjectFile(Project, Install, "assets", "sound-documents", hash + ".wav");
    }
    private byte[] ReadBlob(string hash)
    {
        byte[] pcm = SoundWave.Decode(SoundWave.ReadFile(BlobPath(hash)));
        if (Hash(pcm) != hash) throw new IOException("Project sound WAV checksum differs. Restore its original blob before reopening.");
        return pcm;
    }
    private void PersistPcm()
    {
        if (!HasPcmChange) return;
        string path = BlobPath(Current.Hash);
        if (File.Exists(path)) { ReadBlob(Current.Hash); return; }
        Directory.CreateDirectory(Path.GetDirectoryName(path)); string temporary = path + ".tmp-" + Guid.NewGuid().ToString("N");
        try { File.WriteAllBytes(temporary, SoundWave.Encode(Current.Pcm)); File.Move(temporary, path); }
        finally { if (File.Exists(temporary)) File.Delete(temporary); }
    }
    private State ReadSnapshot(JsonNode row)
    {
        bool legacy = (int?)row?["schema"] == 1 && (string)row["kind"] == "guo/sound-name";
        if (!legacy && row is JsonObject fields && !fields.ContainsKey("pcm_sha256")) throw new IOException("Sound document is missing its PCM reference.");
        if ((!legacy && ((int?)row?["schema"] != 2 || (string)row["kind"] != "guo/sound-effect")) || (int?)row["id"] != Id
            || (string)row["source_sha256"] != SourceHash || (string)row["name"] is not string name || !ValidName(name))
            throw new IOException("Sound document does not match its ID/source, or contains an invalid name. Reopen against its original source.");
        string hash = legacy ? null : (string)row["pcm_sha256"];
        return hash == null ? new(name, _original.Pcm, _original.Hash) : new(name, ReadBlob(hash), hash);
    }
    public void Save()
    {
        if (!CommitDraft()) throw new IOException(RecoveryError ?? Validation);
        CheckSource(); PersistPcm(); string path = DocumentPath; Directory.CreateDirectory(Path.GetDirectoryName(path));
        string temporary = path + ".tmp-" + Guid.NewGuid().ToString("N");
        try { File.WriteAllText(temporary, Snapshot().ToJsonString(new() { WriteIndented = true }) + "\n"); File.Move(temporary, path, true); }
        finally { if (File.Exists(temporary)) File.Delete(temporary); }
        _saved = Current;
    }
    public static SoundDocument Open(string project, string install, int id, Func<(byte[] Pcm, string Name)> readSource)
    {
        var document = new SoundDocument(project, install, id, readSource); string path = document.DocumentPath;
        if (File.Exists(path))
        {
            if (new FileInfo(path).Length > 16384) throw new IOException("Sound document exceeds 16 KiB.");
            State state = document.ReadSnapshot(JsonNode.Parse(File.ReadAllText(path)));
            document.Push(state); document._saved = state; document.DraftName = state.Name;
        }
        return document;
    }
    internal JsonObject Recovery()
    {
        PersistPcm();
        return new() { ["key"] = RecoveryKey, ["saved_name"] = _saved.Name, ["saved_pcm_sha256"] = _saved.Hash, ["snapshot"] = Snapshot(), ["raw_name"] = DraftName };
    }
    internal void Restore(JsonNode row)
    {
        // Validate everything before changing the document, including the saved-copy baseline.
        State state = ReadSnapshot(row?["snapshot"]);
        string savedHash = (string)row["saved_pcm_sha256"] ?? ((int?)row["snapshot"]?["schema"] == 1 ? _original.Hash : null);
        if ((string)row["key"] != RecoveryKey || (string)row["saved_name"] != _saved.Name || savedHash != _saved.Hash
            || (string)row["raw_name"] is not string raw || raw.Length > 4096)
            throw new IOException("Recovered sound draft belongs to another saved copy or has invalid fields.");
        Push(state); DraftName = raw;
    }
}
#endif
