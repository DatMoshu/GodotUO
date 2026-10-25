#if TOOLS
namespace GUO.Editor;

using System;
using System.Buffers.Binary;
using System.IO;
using System.Text;

/// <summary>F14a: lossless RIFF packaging of the effect PCM supplied by SoundsLoader.</summary>
internal static class SoundWave
{
    internal const int Frequency = 22050, MaximumBytes = 64 * 1024 * 1024;
    internal const int MaximumWaveBytes = MaximumBytes + 1024 * 1024;

    internal static byte[] ReadFile(string path)
    {
        using var input = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.Read);
        if (input.Length < 44 || input.Length > MaximumWaveBytes) throw new IOException("WAV must be at least 44 bytes and no larger than 65 MiB.");
        byte[] bytes = new byte[(int)input.Length]; input.ReadExactly(bytes); return bytes;
    }

    internal static byte[] Decode(byte[] wave)
    {
        const string format = "Use uncompressed PCM WAV: mono, 16-bit, 22050 Hz. No resampling is performed.";
        if (wave == null || wave.Length < 44 || wave.Length > MaximumWaveBytes) throw new IOException("Invalid WAV size. " + format);
        bool Tag(int offset, string value) => wave.AsSpan(offset, 4).SequenceEqual(Encoding.ASCII.GetBytes(value));
        if (!Tag(0, "RIFF") || !Tag(8, "WAVE") || BinaryPrimitives.ReadUInt32LittleEndian(wave.AsSpan(4)) != wave.Length - 8)
            throw new IOException("Invalid or truncated RIFF/WAVE container. " + format);
        bool haveFormat = false; byte[] pcm = null; int offset = 12;
        while (offset < wave.Length)
        {
            if (wave.Length - offset < 8) throw new IOException("Truncated WAV chunk header.");
            uint length = BinaryPrimitives.ReadUInt32LittleEndian(wave.AsSpan(offset + 4));
            long end = (long)offset + 8 + length, padded = end + (length & 1);
            if (padded > wave.Length) throw new IOException("WAV chunk exceeds its container.");
            var data = wave.AsSpan(offset + 8, (int)length);
            if (Tag(offset, "fmt "))
            {
                if (haveFormat || pcm != null || length != 16 || BinaryPrimitives.ReadUInt16LittleEndian(data) != 1
                    || BinaryPrimitives.ReadUInt16LittleEndian(data[2..]) != 1 || BinaryPrimitives.ReadUInt32LittleEndian(data[4..]) != Frequency
                    || BinaryPrimitives.ReadUInt32LittleEndian(data[8..]) != Frequency * 2 || BinaryPrimitives.ReadUInt16LittleEndian(data[12..]) != 2
                    || BinaryPrimitives.ReadUInt16LittleEndian(data[14..]) != 16) throw new IOException("Unsupported or inconsistent WAV format. " + format);
                haveFormat = true;
            }
            else if (Tag(offset, "data"))
            {
                if (!haveFormat || pcm != null) throw new IOException("WAV needs one format chunk before one data chunk.");
                ValidateLength((int)length); pcm = data.ToArray();
            }
            offset = (int)padded;
        }
        return haveFormat && pcm != null ? pcm : throw new IOException("WAV format or sample data is missing. " + format);
    }

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
