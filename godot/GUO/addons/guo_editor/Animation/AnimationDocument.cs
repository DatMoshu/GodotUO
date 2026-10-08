#if TOOLS
namespace GUO.Editor;
using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;

/// <summary>Classic VD authoring data. Palette words retain their on-disk high bit.</summary>
public sealed class AnimationDocument
{
    public int Profile;
    public AnimationRecord[] Records;
    public (int Lookup, int Length, int Extra)[] Missing;
    public static int Actions(int profile) => profile switch { 0 => 22, 1 => 13, 2 => 35,
        _ => throw new InvalidDataException("Only classic VD types 0/1/2 are supported; fork types 3/4 need a separate codec.") };
    public AnimationDocument(int profile)
    {
        Profile = profile; Records = new AnimationRecord[Actions(profile) * 5];
        Missing = Enumerable.Repeat((-1, -1, -1), Records.Length).ToArray();
    }
}
public sealed class AnimationRecord
{
    public ushort[] Palette = new ushort[256];
    public int Extra = -1;
    public List<AnimationFrame> Frames = new();
    public byte[] Original;
    public bool Edited;
}
public sealed class AnimationFrame
{
    public short CenterX, CenterY;
    public int Width, Height;
    public byte[] Indices;
    public bool[] Opaque;
}
/// <summary>Bounded classic VD codec, discriminator 6. Valid imported records remain verbatim until edited.</summary>
public static class ClassicVd
{
    private const int Xor = unchecked((int)0x80200000), End = 0x7FFF7FFF, MaxBytes = 128 * 1024 * 1024;
    public static AnimationDocument Read(byte[] bytes)
    {
        if (bytes.Length < 4 || bytes.Length > MaxBytes) throw new InvalidDataException("Invalid VD file size.");
        using var stream = new MemoryStream(bytes, false);
        using var r = new BinaryReader(stream);
        if (r.ReadUInt16() != 6) throw new InvalidDataException("Not an animation VD (discriminator 6).");
        var doc = new AnimationDocument(r.ReadUInt16());
        int tableEnd = 4 + doc.Records.Length * 12;
        if (bytes.Length < tableEnd) throw new InvalidDataException("Truncated VD index.");
        var ranges = new List<(int Start, int End)>();
        long pixels = 0;
        for (int i = 0; i < doc.Records.Length; i++)
        {
            int offset = r.ReadInt32(), length = r.ReadInt32(), extra = r.ReadInt32();
            if (offset < 0 && length < 0) { doc.Missing[i] = (offset, length, extra); continue; }
            if (offset < tableEnd || length < 516 || (long)offset + length > bytes.Length || ranges.Any(x => offset < x.End && (long)offset + length > x.Start))
                throw new InvalidDataException("Invalid or overlapping VD record range.");
            ranges.Add((offset, offset + length));
            doc.Records[i] = ReadRecord(bytes.AsSpan(offset, length).ToArray(), extra, ref pixels);
        }
        return doc;
    }
    private static AnimationRecord ReadRecord(byte[] bytes, int extra, ref long pixels)
    {
        using var stream = new MemoryStream(bytes, false);
        using var r = new BinaryReader(stream);
        var record = new AnimationRecord { Extra = extra, Original = bytes };
        for (int i = 0; i < 256; i++) record.Palette[i] = r.ReadUInt16();
        int count = r.ReadInt32();
        if (count < 0 || count > 4096 || 516L + count * 4L > bytes.Length) throw new InvalidDataException("Invalid frame count.");
        var offsets = new int[count];
        for (int i = 0; i < count; i++)
        {
            long offset = 512L + r.ReadInt32();
            if (offset < 516L + count * 4L || offset > bytes.Length - 8) throw new InvalidDataException("Invalid frame offset.");
            offsets[i] = (int)offset;
        }
        for (int i = 0; i < count; i++)
        {
            stream.Position = offsets[i];
            int limit = offsets.Where(o => o > offsets[i]).DefaultIfEmpty(bytes.Length).Min();
            var f = new AnimationFrame { CenterX = r.ReadInt16(), CenterY = r.ReadInt16(), Width = r.ReadUInt16(), Height = r.ReadUInt16() };
            if (f.Width > 1024 || f.Height > 1024 || (f.Width == 0) != (f.Height == 0)) throw new InvalidDataException("Invalid frame dimensions.");
            pixels += (long)f.Width * f.Height;
            if (pixels > 16 * 1024 * 1024) throw new InvalidDataException("Decoded VD exceeds the pixel budget.");
            f.Indices = new byte[f.Width * f.Height]; f.Opaque = new bool[f.Indices.Length];
            if (f.Width != 0)
            {
                bool ended = false;
                while (stream.Position + 4 <= limit)
                {
                    int header = r.ReadInt32();
                    if (header == End) { ended = true; break; }
                    header ^= Xor;
                    int run = header & 4095, x = ((header >> 22) & 1023) + f.CenterX - 512,
                        y = ((header >> 12) & 1023) + f.CenterY + f.Height - 512;
                    if (run == 0 || x < 0 || y < 0 || y >= f.Height || x + run > f.Width || stream.Position + run > limit)
                        throw new InvalidDataException("RLE run outside frame.");
                    for (int n = 0; n < run; n++)
                    {
                        int p = y * f.Width + x + n;
                        if (f.Opaque[p]) throw new InvalidDataException("Overlapping RLE runs.");
                        f.Indices[p] = r.ReadByte(); f.Opaque[p] = true;
                    }
                }
                if (!ended) throw new InvalidDataException("Missing frame terminator.");
            }
            record.Frames.Add(f);
        }
        return record;
    }
    public static byte[] Write(AnimationDocument doc)
    {
        if (doc.Records.Length != AnimationDocument.Actions(doc.Profile) * 5) throw new InvalidDataException("Profile capacity changed; explicit remapping required.");
        using var stream = new MemoryStream();
        using var w = new BinaryWriter(stream);
        w.Write((ushort)6); w.Write((ushort)doc.Profile); stream.SetLength(4 + doc.Records.Length * 12);
        for (int i = 0; i < doc.Records.Length; i++)
        {
            var record = doc.Records[i]; stream.Position = 4 + i * 12;
            if (record == null) { var missing = doc.Missing[i]; w.Write(missing.Lookup); w.Write(missing.Length); w.Write(missing.Extra); continue; }
            byte[] raw = !record.Edited && record.Original != null ? record.Original : Encode(record);
            int offset = checked((int)stream.Length);
            if ((long)offset + raw.Length > MaxBytes) throw new InvalidDataException("VD output exceeds budget.");
            w.Write(offset); w.Write(raw.Length); w.Write(record.Extra); stream.Position = offset; w.Write(raw);
        }
        byte[] result = stream.ToArray(); Read(result); return result;
    }
    private static byte[] Encode(AnimationRecord record)
    {
        if (record.Palette.Length != 256 || record.Frames.Count > 4096) throw new InvalidDataException("Invalid palette/frame count.");
        using var stream = new MemoryStream(); using var w = new BinaryWriter(stream);
        foreach (ushort word in record.Palette) w.Write(word);
        w.Write(record.Frames.Count); stream.SetLength(516 + record.Frames.Count * 4);
        for (int i = 0; i < record.Frames.Count; i++)
        {
            var f = record.Frames[i];
            if (f.Width <= 0 || f.Height <= 0 || f.Width > 1024 || f.Height > 1024 || f.Indices.Length != f.Width * f.Height || f.Opaque.Length != f.Indices.Length)
                throw new InvalidDataException("Invalid edited frame.");
            int offset = checked((int)stream.Length); stream.Position = 516 + i * 4; w.Write(offset - 512); stream.Position = offset;
            w.Write(f.CenterX); w.Write(f.CenterY); w.Write((ushort)f.Width); w.Write((ushort)f.Height);
            for (int y = 0; y < f.Height; y++) for (int x = 0; x < f.Width;)
            {
                if (!f.Opaque[y * f.Width + x]) { x++; continue; }
                int start = x; while (x < f.Width && f.Opaque[y * f.Width + x]) x++;
                int rx = start - f.CenterX + 512, ry = y - f.CenterY - f.Height + 512;
                if (rx < 0 || rx > 1023 || ry < 0 || ry > 1023) throw new InvalidDataException("Center exceeds classic RLE coordinate range.");
                w.Write(((rx << 22) | (ry << 12) | (x - start)) ^ Xor); w.Write(f.Indices, y * f.Width + start, x - start);
            }
            w.Write(End);
        }
        return stream.ToArray();
    }
}
#endif
