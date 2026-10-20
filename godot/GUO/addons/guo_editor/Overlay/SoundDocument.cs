#if TOOLS
namespace GUO.Editor;

using System;
using System.Collections.Generic;
using System.IO;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json.Nodes;

/// <summary>F14b: source-bound effect naming; PCM remains immutable until the import milestone.</summary>
public sealed class SoundDocument
{
    public const int MaximumNameBytes = 32;
    private readonly Func<(byte[] Pcm, string Name)> _readSource;
    private readonly byte[] _pcm;
    private readonly List<string> _history = new();
    private int _cursor;
    private string _saved;
    public string Project { get; }
    public string Install { get; }
    public int Id { get; }
    public string SourceName { get; }
    public string SourceHash { get; }
    public string CurrentName => _history[_cursor];
    public string DraftName { get; set; }
    public string RecoveryError { get; internal set; }
    public bool CanUndo => _cursor > 0;
    public bool CanRedo => _cursor + 1 < _history.Count;
    public bool Pending => DraftName != CurrentName;
    public bool IsDirty => CurrentName != _saved;
    public int Samples => _pcm.Length / 2;
    public string DocumentPath => EditorPaths.ProjectFile(Project, Install, "metadata", "sounds", $"0x{Id:X4}.json");
    public string RecoveryKey => Project + "|" + Id + "|" + SourceHash;

    private SoundDocument(string project, string install, int id, Func<(byte[] Pcm, string Name)> readSource)
    {
        if (id < 0 || id >= 0xFFFF) throw new ArgumentException("Sound ID is outside the loader's effect range.");
        Project = EditorPaths.Real(project); Install = EditorPaths.Real(install); Id = id; _readSource = readSource;
        var source = readSource(); SoundWave.ValidateLength(source.Pcm?.Length ?? 0);
        _pcm = (byte[])source.Pcm.Clone(); SourceHash = Fingerprint(source);
        string rawName = source.Name ?? ""; int terminator = rawName.IndexOf('\0');
        SourceName = (terminator < 0 ? rawName : rawName[..terminator]).TrimEnd(' ');
        _history.Add(SourceName); _saved = DraftName = SourceName;
    }

    private static string Fingerprint((byte[] Pcm, string Name) source)
    {
        using var hash = IncrementalHash.CreateHash(HashAlgorithmName.SHA256);
        byte[] name = Encoding.UTF8.GetBytes(source.Name ?? "");
        hash.AppendData(Encoding.ASCII.GetBytes(name.Length.ToString(System.Globalization.CultureInfo.InvariantCulture) + ":"));
        hash.AppendData(name); hash.AppendData(source.Pcm ?? Array.Empty<byte>());
        return Convert.ToHexString(hash.GetHashAndReset()).ToLowerInvariant();
    }

    public byte[] Pcm() => (byte[])_pcm.Clone();
    public string Validation => DraftName == null || DraftName.Contains('\0') || (DraftName != SourceName && Encoding.UTF8.GetByteCount(DraftName) > MaximumNameBytes)
        ? "Name must fit 32 UTF-8 bytes and contain no NUL. The draft is retained." : "";
    public bool CommitDraft()
    {
        if (RecoveryError != null || Validation.Length > 0) return false;
        if (!Pending) return true;
        _history.RemoveRange(_cursor + 1, _history.Count - _cursor - 1); _history.Add(DraftName); _cursor++;
        if (_history.Count > 100) { _history.RemoveAt(0); _cursor--; }
        return true;
    }
    public void Undo()
    {
        if (Pending && !CommitDraft()) { DraftName = CurrentName; return; }
        if (CanUndo) _cursor--; DraftName = CurrentName;
    }
    public void Redo() { if (!CommitDraft()) return; if (CanRedo) _cursor++; DraftName = CurrentName; }
    public void Revert() { if (RecoveryError != null) return; DraftName = SourceName; CommitDraft(); }

    public JsonObject Snapshot() => new()
    {
        ["schema"] = 1, ["kind"] = "guo/sound-name", ["id"] = Id,
        ["source_sha256"] = SourceHash, ["name"] = CurrentName,
    };
    private string ReadSnapshot(JsonNode row)
    {
        if ((int?)row?["schema"] != 1 || (string)row["kind"] != "guo/sound-name" || (int?)row["id"] != Id
            || (string)row["source_sha256"] != SourceHash || (string)row["name"] is not string name
            || name.Contains('\0') || (name != SourceName && Encoding.UTF8.GetByteCount(name) > MaximumNameBytes))
            throw new IOException("Sound document does not match its ID/source, or contains an invalid name. Reopen against its original source.");
        return name;
    }
    public void Save()
    {
        if (!CommitDraft()) throw new IOException(RecoveryError ?? Validation);
        if (Fingerprint(_readSource()) != SourceHash) throw new IOException("Active source sound changed. Reopen the document before saving.");
        string path = DocumentPath; Directory.CreateDirectory(Path.GetDirectoryName(path));
        string temporary = path + ".tmp-" + Guid.NewGuid().ToString("N");
        try { File.WriteAllText(temporary, Snapshot().ToJsonString(new() { WriteIndented = true }) + "\n"); File.Move(temporary, path, true); }
        finally { if (File.Exists(temporary)) File.Delete(temporary); }
        _saved = CurrentName;
    }
    public static SoundDocument Open(string project, string install, int id, Func<(byte[] Pcm, string Name)> readSource)
    {
        var document = new SoundDocument(project, install, id, readSource); string path = document.DocumentPath;
        if (File.Exists(path))
        {
            if (new FileInfo(path).Length > 16384) throw new IOException("Sound naming document exceeds 16 KiB.");
            string name = document.ReadSnapshot(JsonNode.Parse(File.ReadAllText(path)));
            document.DraftName = name; document.CommitDraft(); document._saved = name;
        }
        return document;
    }
    internal JsonObject Recovery() => new()
    { ["key"] = RecoveryKey, ["saved_name"] = _saved, ["snapshot"] = Snapshot(), ["raw_name"] = DraftName };
    internal void Restore(JsonNode row)
    {
        // Validate everything before changing the document, including the saved-copy baseline.
        string name = ReadSnapshot(row?["snapshot"]);
        if ((string)row["key"] != RecoveryKey || (string)row["saved_name"] != _saved
            || (string)row["raw_name"] is not string raw || raw.Length > 4096)
            throw new IOException("Recovered sound draft belongs to another saved copy or has invalid fields.");
        DraftName = name; CommitDraft(); DraftName = raw;
    }
}
#endif
