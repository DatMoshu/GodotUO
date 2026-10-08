using System;
using System.IO;
using System.Linq;
using GUO.Editor;

static class VdChecks
{
    static int checks;
    static void Check(bool value, string name) { if (!value) throw new Exception(name); checks++; }
    static void Refuses(byte[] bytes, string name)
    {
        try { ClassicVd.Read(bytes); } catch (InvalidDataException) { checks++; return; }
        throw new Exception("Accepted malformed fixture: " + name);
    }
    // Original independently assembled fixture: action 1/direction 2, negative X center,
    // positive Y center, nonzero index extra, two pixels including opaque palette index 0.
    static byte[] Fixture(int profile)
    {
        int slots = AnimationDocument.Actions(profile) * 5, start = 4 + slots * 12;
        using var stream = new MemoryStream(); using var w = new BinaryWriter(stream);
        w.Write((ushort)6); w.Write((ushort)profile);
        for (int i = 0; i < slots; i++) { w.Write(i == 7 ? start : -1); w.Write(i == 7 ? 538 : -1); w.Write(i == 7 ? 123456 : -1); }
        for (int i = 0; i < 256; i++) w.Write((ushort)(i == 0 ? 0x8000 : i == 1 ? 0x7C00 : i));
        w.Write(1); w.Write(8); w.Write((short)-2); w.Write((short)3); w.Write((ushort)2); w.Write((ushort)1);
        // Signed relative coordinate (x=2,y=-4), encoded per classic RLE.
        w.Write(unchecked((int)(((514u << 22) | (508u << 12) | 2u) ^ 0x80200000u)));
        w.Write((byte)0); w.Write((byte)1); w.Write(0x7FFF7FFF);
        return stream.ToArray();
    }
    static void Main()
    {
        foreach (int profile in new[] { 0, 1, 2 })
        {
            byte[] bytes = Fixture(profile); var doc = ClassicVd.Read(bytes); var record = doc.Records[7]; var frame = record.Frames[0];
            Check(doc.Records.Length == AnimationDocument.Actions(profile) * 5, "profile capacity");
            Check(doc.Records[0] == null && record.Extra == 123456, "missing/extra preserved");
            Check(frame.CenterX == -2 && frame.CenterY == 3 && frame.Opaque.All(x => x) && frame.Indices.SequenceEqual(new byte[] { 0, 1 }), "RLE centers/opaque index zero");
            Check(bytes.SequenceEqual(ClassicVd.Write(doc)), "no-edit byte parity");
            frame.CenterX = 4; frame.CenterY = -5; record.Edited = true;
            var edited = ClassicVd.Read(ClassicVd.Write(doc)).Records[7];
            Check(edited.Frames[0].CenterX == 4 && edited.Frames[0].CenterY == -5 && edited.Frames[0].Indices.SequenceEqual(frame.Indices) && edited.Palette.SequenceEqual(record.Palette), "edited center/palette roundtrip");
        }
        byte[] original = Fixture(0); int offset = 4 + 22 * 5 * 12;
        var missing = (byte[])original.Clone(); BitConverter.GetBytes(-2).CopyTo(missing, 4); BitConverter.GetBytes(-3).CopyTo(missing, 8); BitConverter.GetBytes(987).CopyTo(missing, 12);
        Check(ClassicVd.Write(ClassicVd.Read(missing)).SequenceEqual(missing), "missing index metadata preserved");
        Refuses(original[..3], "truncated header");
        var bad = (byte[])original.Clone(); bad[2] = 3; Refuses(bad, "fork type 3");
        bad = (byte[])original.Clone(); bad[2] = 4; Refuses(bad, "fork type 4");
        bad = (byte[])original.Clone(); BitConverter.GetBytes(int.MaxValue).CopyTo(bad, 4 + 7 * 12); Refuses(bad, "record overflow");
        bad = (byte[])original.Clone(); BitConverter.GetBytes(-1).CopyTo(bad, offset + 512); Refuses(bad, "negative count");
        bad = (byte[])original.Clone(); BitConverter.GetBytes(0).CopyTo(bad, offset + 516); Refuses(bad, "frame offset into table");
        bad = (byte[])original.Clone(); bad[^1] = 0; Refuses(bad, "missing terminator");
        bad = (byte[])original.Clone(); bad[offset + 528] = 3; Refuses(bad, "run beyond width");
        Console.WriteLine($"[animation] {checks} independent VD checks passed");
    }
}
