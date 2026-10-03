using System.Buffers.Binary;
using System.IO.Compression;

namespace CentrED.MapGen.Preview;

// Minimal RGBA8 → PNG-32 encoder. Pure managed; no NuGet deps; uses BCL DeflateStream
// for IDAT compression and a small inline CRC32 + Adler32 implementation for the
// PNG chunk checksums and zlib trailer respectively.
//
// Input pixel layout matches PreviewRenderer.Output.Pixels: 0xAABBGGRR (FNA color order).
// We swizzle to PNG's RGBA byte order at write time.
public static class PngWriter
{
    private static readonly byte[] Signature = { 0x89, 0x50, 0x4E, 0x47, 0x0D, 0x0A, 0x1A, 0x0A };

    public static void Write(string path, uint[] pixels, int width, int height)
    {
        Directory.CreateDirectory(Path.GetDirectoryName(Path.GetFullPath(path))!);
        using var fs = File.Create(path);
        fs.Write(Signature);

        // IHDR — 13 bytes: width, height, bitDepth=8, colorType=6 (RGBA), compress=0, filter=0, interlace=0
        Span<byte> ihdr = stackalloc byte[13];
        BinaryPrimitives.WriteUInt32BigEndian(ihdr.Slice(0, 4), (uint)width);
        BinaryPrimitives.WriteUInt32BigEndian(ihdr.Slice(4, 4), (uint)height);
        ihdr[8] = 8; ihdr[9] = 6; ihdr[10] = 0; ihdr[11] = 0; ihdr[12] = 0;
        WriteChunk(fs, "IHDR", ihdr);

        // IDAT — for each scanline: filter byte 0 + RGBA pixels. Whole stream zlib-compressed.
        // Pre-build the raw scanline buffer; then compress with DeflateStream wrapped in zlib.
        int rowBytes = width * 4 + 1;
        var raw = new byte[rowBytes * height];
        for (int y = 0; y < height; y++)
        {
            int dst = y * rowBytes;
            raw[dst++] = 0; // filter type "None"
            for (int x = 0; x < width; x++)
            {
                uint c = pixels[y * width + x];
                raw[dst++] = (byte)(c & 0xFF);          // R
                raw[dst++] = (byte)((c >> 8) & 0xFF);   // G
                raw[dst++] = (byte)((c >> 16) & 0xFF);  // B
                raw[dst++] = (byte)((c >> 24) & 0xFF);  // A
            }
        }

        // zlib stream = 2-byte zlib header + DEFLATE-compressed data + 4-byte Adler32 of raw.
        using var compressed = new MemoryStream();
        // zlib header (CMF=0x78 deflate-32k window, FLG=0x9C default-compression checksum)
        compressed.WriteByte(0x78);
        compressed.WriteByte(0x9C);
        using (var deflate = new DeflateStream(compressed, CompressionLevel.Fastest, leaveOpen: true))
            deflate.Write(raw, 0, raw.Length);
        uint adler = Adler32(raw);
        Span<byte> adlerBe = stackalloc byte[4];
        BinaryPrimitives.WriteUInt32BigEndian(adlerBe, adler);
        compressed.Write(adlerBe);

        WriteChunk(fs, "IDAT", compressed.GetBuffer().AsSpan(0, (int)compressed.Length));
        WriteChunk(fs, "IEND", ReadOnlySpan<byte>.Empty);
    }

    private static void WriteChunk(Stream s, string type, ReadOnlySpan<byte> data)
    {
        Span<byte> hdr = stackalloc byte[8];
        BinaryPrimitives.WriteUInt32BigEndian(hdr.Slice(0, 4), (uint)data.Length);
        hdr[4] = (byte)type[0]; hdr[5] = (byte)type[1]; hdr[6] = (byte)type[2]; hdr[7] = (byte)type[3];
        s.Write(hdr);
        s.Write(data);
        // CRC32 covers type bytes + data.
        uint crc = 0xFFFFFFFFu;
        crc = Crc32Update(crc, hdr.Slice(4, 4));
        crc = Crc32Update(crc, data);
        crc ^= 0xFFFFFFFFu;
        Span<byte> crcBe = stackalloc byte[4];
        BinaryPrimitives.WriteUInt32BigEndian(crcBe, crc);
        s.Write(crcBe);
    }

    private static readonly uint[] CrcTable = BuildCrcTable();
    private static uint[] BuildCrcTable()
    {
        var t = new uint[256];
        for (uint n = 0; n < 256; n++)
        {
            uint c = n;
            for (int k = 0; k < 8; k++) c = (c & 1) != 0 ? 0xEDB88320u ^ (c >> 1) : (c >> 1);
            t[n] = c;
        }
        return t;
    }
    private static uint Crc32Update(uint crc, ReadOnlySpan<byte> data)
    {
        for (int i = 0; i < data.Length; i++)
            crc = CrcTable[(crc ^ data[i]) & 0xFF] ^ (crc >> 8);
        return crc;
    }

    // PNG zlib wrapper requires Adler32 of the *uncompressed* data.
    private static uint Adler32(ReadOnlySpan<byte> data)
    {
        const uint Mod = 65521;
        uint a = 1, b = 0;
        for (int i = 0; i < data.Length; i++)
        {
            a = (a + data[i]) % Mod;
            b = (b + a) % Mod;
        }
        return (b << 16) | a;
    }
}
