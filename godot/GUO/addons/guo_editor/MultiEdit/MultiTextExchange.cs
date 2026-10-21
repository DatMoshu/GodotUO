#if TOOLS
namespace GUO.Editor;

using System;
using System.IO;
using System.Linq;
using System.Security.Cryptography;

/// <summary>Bounded captured interchange input. Recheck the chosen file at the apply boundary.</summary>
internal sealed class MultiTextInput
{
    internal const int MaxBytes = 4 * 1024 * 1024;
    private readonly string _chosen, _real, _hash;
    internal byte[] Bytes { get; }
    internal MultiTextInput(string path)
    {
        _chosen = Path.GetFullPath(path); _real = EditorPaths.Real(path);
        Bytes = Read(_real); _hash = Hash(Bytes); EditorPaths.ProtectSource(_real);
    }
    internal static byte[] Read(string path)
    {
        using var file = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.Read);
        if (file.Length > MaxBytes) throw new IOException("Fiddler TXT exceeds 4 MiB. Split the file before importing.");
        byte[] bytes = new byte[(int)file.Length]; file.ReadExactly(bytes);
        if (file.ReadByte() != -1) throw new IOException("The file changed while reading. Choose it again.");
        return bytes;
    }
    internal static string Hash(byte[] bytes) => Convert.ToHexString(SHA256.HashData(bytes));
    internal void Check()
    {
        if (EditorPaths.Real(_chosen) != _real || Hash(Read(_real)) != _hash)
            throw new IOException("The selected TXT file changed after review. Close this review and import it again.");
    }
}

/// <summary>Snapshot includes order, duplicates, hue and opaque flags; undo/redo also invalidates review.</summary>
internal sealed class MultiTextSnapshot
{
    private readonly long _revision;
    private readonly string _signature;
    internal MultiPart[] Parts { get; }
    internal MultiTextSnapshot(MultiDocument document)
    {
        _revision = document.Revision; Parts = document.Parts.ToArray();
        _signature = MultiStore.ToJson(document.Name, document.Source, Parts);
    }
    internal void Check(MultiDocument document)
    {
        if (document.Revision != _revision || MultiStore.ToJson(document.Name, document.Source, document.Parts) != _signature)
            throw new IOException("The multi changed after review. Close this review and choose the file again.");
    }
    internal static MultiPart[] Centre(MultiPart[] parts)
    {
        int cx = (int)Math.Floor((parts.Min(p => (int)p.X) + parts.Max(p => (int)p.X)) / 2.0);
        int cy = (int)Math.Floor((parts.Min(p => (int)p.Y) + parts.Max(p => (int)p.Y)) / 2.0);
        return parts.Select(p => { p.X = checked((short)(p.X - cx)); p.Y = checked((short)(p.Y - cy)); return p; }).ToArray();
    }
}

/// <summary>Captured overwrite review and atomic output outside the installation and protected source files.</summary>
internal sealed class MultiTextDestination
{
    private readonly string _chosen, _real, _prior;
    internal MultiTextDestination(string path)
    {
        _chosen = Path.GetFullPath(path); AssetActions.EnsureExportDestination(_chosen);
        _real = EditorPaths.Real(_chosen);
        if (!Directory.Exists(Path.GetDirectoryName(_real))) throw new IOException("Choose an existing export folder.");
        _prior = Token();
    }
    private string Token() => File.Exists(_real) ? MultiTextInput.Hash(MultiTextInput.Read(_real)) : "absent";
    internal void Check()
    {
        AssetActions.EnsureExportDestination(_chosen);
        if (EditorPaths.Real(_chosen) != _real || Token() != _prior)
            throw new IOException("The export destination changed after review. Choose it again.");
    }
    internal void Write(byte[] bytes)
    {
        if (bytes.Length > MultiTextInput.MaxBytes) throw new IOException("Fiddler TXT exceeds 4 MiB.");
        Check(); string temp = _real + ".tmp-" + Guid.NewGuid().ToString("N");
        try { File.WriteAllBytes(temp, bytes); Check(); File.Move(temp, _real, true); }
        finally { if (File.Exists(temp)) File.Delete(temp); }
    }
}
#endif
