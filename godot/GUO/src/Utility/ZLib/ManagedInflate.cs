// GUO-owned (not a ported file): a dependency-free zlib inflate, after Mark
// Adler's puff.c (zlib/contrib/puff, zlib licence).
//
// Why it exists: on the web, GUO runs on the community .NET web export
// (tools/godot_web, ADR-0008), whose engine template links the Mono runtime
// but not System.IO.Compression.Native. So every BCL deflate/zlib stream
// throws ZLibErrorDLLLoadError there, and upstream's managed ZLIBStream is a
// wrapper over DeflateStream, so it fails the same way. This is a plain
// RFC 1950/1951 decoder with no native code at all. ZLib.SelectCompressor
// uses it in a browser; GUO_ZLIB=managed selects it anywhere, for testing
// against the desktop's zlib.

using System;

namespace GUO.Utility
{
    internal static class ManagedInflate
    {
        private const int MAXBITS = 15;
        private const int MAXLCODES = 286;
        private const int MAXDCODES = 30;
        private const int FIXLCODES = 288;

        private static readonly short[] LBase =
        {
            3, 4, 5, 6, 7, 8, 9, 10, 11, 13, 15, 17, 19, 23, 27, 31,
            35, 43, 51, 59, 67, 83, 99, 115, 131, 163, 195, 227, 258
        };

        private static readonly short[] LExt =
        {
            0, 0, 0, 0, 0, 0, 0, 0, 1, 1, 1, 1, 2, 2, 2, 2,
            3, 3, 3, 3, 4, 4, 4, 4, 5, 5, 5, 5, 0
        };

        private static readonly short[] DBase =
        {
            1, 2, 3, 4, 5, 7, 9, 13, 17, 25, 33, 49, 65, 97, 129, 193,
            257, 385, 513, 769, 1025, 1537, 2049, 3073, 4097, 6145,
            8193, 12289, 16385, 24577
        };

        private static readonly short[] DExt =
        {
            0, 0, 0, 0, 1, 1, 2, 2, 3, 3, 4, 4, 5, 5, 6, 6,
            7, 7, 8, 8, 9, 9, 10, 10, 11, 11,
            12, 12, 13, 13
        };

        private static readonly byte[] Order = { 16, 17, 18, 0, 8, 7, 9, 6, 10, 5, 11, 4, 12, 3, 13, 2, 14, 1, 15 };

        private sealed class Huffman
        {
            public readonly short[] Count = new short[MAXBITS + 1];
            public readonly short[] Symbol;

            public Huffman(int symbols)
            {
                Symbol = new short[symbols];
            }
        }

        private static Huffman _fixedLen, _fixedDist;

        public class InflateException : Exception
        {
            public InflateException(string message) : base(message) { }
        }

        private ref struct State
        {
            public ReadOnlySpan<byte> In;
            public int InPos;
            public int BitBuf;
            public int BitCnt;
            public Span<byte> Out;
            public int OutPos;

            public int Bits(int need)
            {
                int val = BitBuf;
                while (BitCnt < need)
                {
                    if (InPos >= In.Length)
                    {
                        throw new InflateException("input ran out");
                    }

                    val |= In[InPos++] << BitCnt;
                    BitCnt += 8;
                }

                BitBuf = val >> need;
                BitCnt -= need;
                return val & ((1 << need) - 1);
            }
        }

        /// <summary>
        /// Inflates one zlib stream (2-byte header, deflate data, Adler-32
        /// trailer) into <paramref name="dest"/>. Returns the bytes written.
        /// Output beyond dest's length is an error; a short output is not
        /// (callers size dest from the archive's stated length). The Adler-32
        /// is checked when the trailer is present.
        /// </summary>
        public static int InflateZlib(ReadOnlySpan<byte> source, Span<byte> dest)
        {
            if (source.Length < 2 || (source[0] & 0x0F) != 8 || ((source[0] << 8) | source[1]) % 31 != 0)
            {
                throw new InflateException("not a zlib stream");
            }

            if ((source[1] & 0x20) != 0)
            {
                throw new InflateException("preset dictionary");
            }

            var s = new State { In = source.Slice(2), Out = dest };
            Inflate(ref s);

            int trailer = 2 + s.InPos;
            if (trailer + 4 <= source.Length)
            {
                uint expected = (uint)((source[trailer] << 24) | (source[trailer + 1] << 16) | (source[trailer + 2] << 8) | source[trailer + 3]);
                if (Adler32(dest.Slice(0, s.OutPos)) != expected)
                {
                    throw new InflateException("adler-32 mismatch");
                }
            }

            return s.OutPos;
        }

        public static uint Adler32(ReadOnlySpan<byte> data)
        {
            uint a = 1, b = 0;
            int i = 0;
            while (i < data.Length)
            {
                int n = Math.Min(5552, data.Length - i);
                for (int end = i + n; i < end; i++)
                {
                    a += data[i];
                    b += a;
                }

                a %= 65521;
                b %= 65521;
            }

            return (b << 16) | a;
        }

        private static void Inflate(ref State s)
        {
            int last;
            do
            {
                last = s.Bits(1);
                int type = s.Bits(2);
                switch (type)
                {
                    case 0:
                        Stored(ref s);
                        break;
                    case 1:
                        EnsureFixed();
                        Codes(ref s, _fixedLen, _fixedDist);
                        break;
                    case 2:
                        Dynamic(ref s);
                        break;
                    default:
                        throw new InflateException("invalid block type");
                }
            }
            while (last == 0);
        }

        private static void Stored(ref State s)
        {
            s.BitBuf = 0;
            s.BitCnt = 0;
            if (s.InPos + 4 > s.In.Length)
            {
                throw new InflateException("input ran out");
            }

            int len = s.In[s.InPos] | (s.In[s.InPos + 1] << 8);
            int nlen = s.In[s.InPos + 2] | (s.In[s.InPos + 3] << 8);
            s.InPos += 4;
            if (len != (~nlen & 0xFFFF))
            {
                throw new InflateException("stored length mismatch");
            }

            if (s.InPos + len > s.In.Length)
            {
                throw new InflateException("input ran out");
            }

            if (s.OutPos + len > s.Out.Length)
            {
                throw new InflateException("output full");
            }

            s.In.Slice(s.InPos, len).CopyTo(s.Out.Slice(s.OutPos));
            s.InPos += len;
            s.OutPos += len;
        }

        private static int Decode(ref State s, Huffman h)
        {
            int code = 0, first = 0, index = 0;
            for (int len = 1; len <= MAXBITS; len++)
            {
                code |= s.Bits(1);
                int count = h.Count[len];
                if (code - count < first)
                {
                    return h.Symbol[index + (code - first)];
                }

                index += count;
                first += count;
                first <<= 1;
                code <<= 1;
            }

            throw new InflateException("ran out of codes");
        }

        // Returns 0 for a complete code, > 0 for an incomplete one, < 0 for an
        // over-subscribed one (puff's construct()).
        private static int Construct(Huffman h, ReadOnlySpan<short> length)
        {
            Array.Clear(h.Count);
            for (int sym = 0; sym < length.Length; sym++)
            {
                h.Count[length[sym]]++;
            }

            if (h.Count[0] == length.Length)
            {
                return 0;
            }

            int left = 1;
            for (int len = 1; len <= MAXBITS; len++)
            {
                left <<= 1;
                left -= h.Count[len];
                if (left < 0)
                {
                    return left;
                }
            }

            Span<short> offs = stackalloc short[MAXBITS + 1];
            offs[1] = 0;
            for (int len = 1; len < MAXBITS; len++)
            {
                offs[len + 1] = (short)(offs[len] + h.Count[len]);
            }

            for (int sym = 0; sym < length.Length; sym++)
            {
                if (length[sym] != 0)
                {
                    h.Symbol[offs[length[sym]]++] = (short)sym;
                }
            }

            return left;
        }

        private static void Codes(ref State s, Huffman lencode, Huffman distcode)
        {
            int symbol;
            do
            {
                symbol = Decode(ref s, lencode);
                if (symbol < 256)
                {
                    if (s.OutPos >= s.Out.Length)
                    {
                        throw new InflateException("output full");
                    }

                    s.Out[s.OutPos++] = (byte)symbol;
                }
                else if (symbol > 256)
                {
                    symbol -= 257;
                    if (symbol >= 29)
                    {
                        throw new InflateException("invalid length symbol");
                    }

                    int len = LBase[symbol] + s.Bits(LExt[symbol]);
                    symbol = Decode(ref s, distcode);
                    if (symbol >= 30)
                    {
                        throw new InflateException("invalid distance symbol");
                    }

                    int dist = DBase[symbol] + s.Bits(DExt[symbol]);
                    if (dist > s.OutPos)
                    {
                        throw new InflateException("distance too far back");
                    }

                    if (s.OutPos + len > s.Out.Length)
                    {
                        throw new InflateException("output full");
                    }

                    // Byte by byte: a match may overlap its own output.
                    for (int i = 0; i < len; i++)
                    {
                        s.Out[s.OutPos] = s.Out[s.OutPos - dist];
                        s.OutPos++;
                    }
                }
            }
            while (symbol != 256);
        }

        private static void EnsureFixed()
        {
            if (_fixedLen != null)
            {
                return;
            }

            var lengths = new short[FIXLCODES];
            int sym = 0;
            for (; sym < 144; sym++) lengths[sym] = 8;
            for (; sym < 256; sym++) lengths[sym] = 9;
            for (; sym < 280; sym++) lengths[sym] = 7;
            for (; sym < FIXLCODES; sym++) lengths[sym] = 8;
            var len = new Huffman(FIXLCODES);
            Construct(len, lengths);

            var dl = new short[MAXDCODES];
            dl.AsSpan().Fill(5);
            var dist = new Huffman(MAXDCODES);
            Construct(dist, dl);

            _fixedDist = dist;
            _fixedLen = len;
        }

        private static void Dynamic(ref State s)
        {
            int nlen = s.Bits(5) + 257;
            int ndist = s.Bits(5) + 1;
            int ncode = s.Bits(4) + 4;
            if (nlen > MAXLCODES || ndist > MAXDCODES)
            {
                throw new InflateException("bad counts");
            }

            Span<short> lengths = stackalloc short[MAXLCODES + MAXDCODES];
            lengths.Clear();
            for (int i = 0; i < ncode; i++)
            {
                lengths[Order[i]] = (short)s.Bits(3);
            }

            var lencode = new Huffman(MAXLCODES);
            var distcode = new Huffman(MAXDCODES);
            if (Construct(lencode, lengths.Slice(0, 19)) != 0)
            {
                throw new InflateException("incomplete code-length code");
            }

            int index = 0;
            while (index < nlen + ndist)
            {
                int symbol = Decode(ref s, lencode);
                if (symbol < 16)
                {
                    lengths[index++] = (short)symbol;
                }
                else
                {
                    short len = 0;
                    if (symbol == 16)
                    {
                        if (index == 0)
                        {
                            throw new InflateException("repeat with no first length");
                        }

                        len = lengths[index - 1];
                        symbol = 3 + s.Bits(2);
                    }
                    else if (symbol == 17)
                    {
                        symbol = 3 + s.Bits(3);
                    }
                    else
                    {
                        symbol = 11 + s.Bits(7);
                    }

                    if (index + symbol > nlen + ndist)
                    {
                        throw new InflateException("too many lengths");
                    }

                    while (symbol-- > 0)
                    {
                        lengths[index++] = len;
                    }
                }
            }

            if (lengths[256] == 0)
            {
                throw new InflateException("no end-of-block code");
            }

            int err = Construct(lencode, lengths.Slice(0, nlen));
            if (err < 0 || (err > 0 && nlen - lencode.Count[0] != 1))
            {
                throw new InflateException("bad literal/length code");
            }

            err = Construct(distcode, lengths.Slice(nlen, ndist));
            if (err < 0 || (err > 0 && ndist - distcode.Count[0] != 1))
            {
                throw new InflateException("bad distance code");
            }

            Codes(ref s, lencode, distcode);
        }
    }
}
