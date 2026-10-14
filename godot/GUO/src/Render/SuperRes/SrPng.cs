// SPDX-License-Identifier: BSD-2-Clause
// PORT DEVIATION (GUO): SuperRes 4x minimal RGBA PNG encoder.
// The background worker must touch no Godot objects, so it cannot use
// Image.SavePngToBuffer; this writes a plain 8-bit RGBA PNG with pure
// .NET (filter 0 rows, zlib via the BCL). No decoder needed here.

using System;
using System.IO;
using System.IO.Compression;

namespace GUO.Renderer.SuperRes
{
    internal static class SrPng
    {
        private static readonly uint[] CrcTable = BuildCrcTable();

        private static uint[] BuildCrcTable()
        {
            var t = new uint[256];
            for (uint n = 0; n < 256; n++)
            {
                uint c = n;
                for (int k = 0; k < 8; k++)
                {
                    c = (c & 1) != 0 ? 0xEDB88320u ^ (c >> 1) : c >> 1;
                }

                t[n] = c;
            }

            return t;
        }

        private static uint Crc(byte[] type, byte[] data, int dataLen)
        {
            uint c = 0xFFFFFFFFu;
            foreach (byte b in type)
            {
                c = CrcTable[(c ^ b) & 0xFF] ^ (c >> 8);
            }

            for (int i = 0; i < dataLen; i++)
            {
                c = CrcTable[(c ^ data[i]) & 0xFF] ^ (c >> 8);
            }

            return c ^ 0xFFFFFFFFu;
        }

        private static void WriteU32BE(Stream s, uint v)
        {
            s.WriteByte((byte)(v >> 24));
            s.WriteByte((byte)(v >> 16));
            s.WriteByte((byte)(v >> 8));
            s.WriteByte((byte)v);
        }

        private static void WriteChunk(Stream s, string type, byte[] data, int len)
        {
            byte[] tb = { (byte)type[0], (byte)type[1], (byte)type[2], (byte)type[3] };
            WriteU32BE(s, (uint)len);
            s.Write(tb, 0, 4);
            if (len > 0)
            {
                s.Write(data, 0, len);
            }

            WriteU32BE(s, Crc(tb, data ?? Array.Empty<byte>(), len));
        }

        /// <summary>RGBA bytes (w*h*4) to a readable PNG.</summary>
        public static byte[] EncodeRgba(byte[] rgba, int w, int h)
        {
            if (rgba == null || w <= 0 || h <= 0 || rgba.Length < w * h * 4)
            {
                throw new ArgumentException("bad rgba for PNG encode");
            }

            // One filter-0 byte per row, then the row's RGBA.
            byte[] raw = new byte[h * (1 + w * 4)];
            for (int y = 0; y < h; y++)
            {
                raw[y * (1 + w * 4)] = 0;
                Buffer.BlockCopy(rgba, y * w * 4, raw, y * (1 + w * 4) + 1, w * 4);
            }

            byte[] comp;
            using (var ms = new MemoryStream())
            {
                using (var z = new ZLibStream(ms, CompressionLevel.Optimal, leaveOpen: true))
                {
                    z.Write(raw, 0, raw.Length);
                }

                comp = ms.ToArray();
            }

            using var out_ = new MemoryStream();
            out_.Write(new byte[] { 137, 80, 78, 71, 13, 10, 26, 10 }, 0, 8);

            byte[] ihdr = new byte[13];
            ihdr[0] = (byte)(w >> 24); ihdr[1] = (byte)(w >> 16);
            ihdr[2] = (byte)(w >> 8); ihdr[3] = (byte)w;
            ihdr[4] = (byte)(h >> 24); ihdr[5] = (byte)(h >> 16);
            ihdr[6] = (byte)(h >> 8); ihdr[7] = (byte)h;
            ihdr[8] = 8; // bit depth
            ihdr[9] = 6; // color type RGBA
            ihdr[10] = 0; ihdr[11] = 0; ihdr[12] = 0;
            WriteChunk(out_, "IHDR", ihdr, 13);
            WriteChunk(out_, "IDAT", comp, comp.Length);
            WriteChunk(out_, "IEND", Array.Empty<byte>(), 0);
            return out_.ToArray();
        }
    }
}
