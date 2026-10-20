#if TOOLS
namespace GUO.Editor;

using System;
using System.IO;
using System.Text;

/// <summary>F14a: lossless RIFF packaging of the effect PCM supplied by SoundsLoader.</summary>
internal static class SoundWave
{
    internal const int Frequency = 22050, MaximumBytes = 64 * 1024 * 1024;

    internal static void ValidateLength(int length)
    {
        if (length <= 0 || length > MaximumBytes || length % 2 != 0)
            throw new ArgumentException("Sound export requires nonempty 16-bit mono PCM, at most 64 MiB and an even byte count.");
    }

    internal static byte[] Encode(byte[] pcm)
    {
        if (pcm == null) throw new ArgumentNullException(nameof(pcm));
        ValidateLength(pcm.Length);
        using var output = new MemoryStream(pcm.Length + 44);
        using var writer = new BinaryWriter(output, Encoding.ASCII, leaveOpen: true);
        writer.Write(Encoding.ASCII.GetBytes("RIFF")); writer.Write(pcm.Length + 36);
        writer.Write(Encoding.ASCII.GetBytes("WAVEfmt ")); writer.Write(16);
        writer.Write((ushort)1); writer.Write((ushort)1); writer.Write(Frequency);
        writer.Write(Frequency * 2); writer.Write((ushort)2); writer.Write((ushort)16);
        writer.Write(Encoding.ASCII.GetBytes("data")); writer.Write(pcm.Length); writer.Write(pcm);
        return output.ToArray();
    }

    internal static void Export(byte[] pcm, string path)
    {
        byte[] wave = Encode(pcm);
        AssetActions.EnsureExportDestination(path);
        string temporary = path + ".tmp-" + Guid.NewGuid().ToString("N");
        try { File.WriteAllBytes(temporary, wave); File.Move(temporary, path, true); }
        finally { if (File.Exists(temporary)) File.Delete(temporary); }
    }
}
#endif
