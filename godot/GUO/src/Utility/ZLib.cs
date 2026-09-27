// SPDX-License-Identifier: BSD-2-Clause

using System;
using System.IO;
using System.Runtime.InteropServices;
using GUO.Utility.Platforms;

namespace GUO.Utility
{
    public static class ZLib
    {
        // thanks ServUO :)

        private static readonly ICompressor _compressor;

        static ZLib()
        {
            // PORT DEVIATION (GUO): upstream picks the native zlib on 64-bit
            // and never looks back, because ClassicUO ships zlib.dll beside
            // its executable. GUO runs inside Godot, where nothing puts that
            // native library on the search path, so the P/Invoke throws
            // DllNotFoundException the first time a .uop archive is read --
            // which is immediately, and fatally.
            //
            // Upstream's own managed implementation is right here and handles
            // every case the readers need, so the native path is now attempted
            // and fallen back from rather than assumed. When zlib.dll IS
            // present this behaves exactly as upstream does; when it is not,
            // the client works instead of dying. Dropping a native dependency
            // is also squarely the point of getting off FNA.
            _compressor = SelectCompressor();
        }

        private static ICompressor SelectCompressor()
        {
            // Android has a system libz, so the P/Invoke resolves, but
            // zlibVersion() returning a .NET string makes the marshaller free
            // zlib's static version pointer, which the device's tagged-pointer
            // check aborts on. The managed zlib below is used there. ADR-0017.
            if (Environment.Is64BitProcess && !OperatingSystem.IsAndroid())
            {
                ICompressor native = PlatformHelper.IsWindows
                    ? new Compressor64()
                    : (ICompressor)new CompressorUnix64();

                try
                {
                    // Touching Version resolves the P/Invoke. If the native
                    // library is missing this throws here, once, at startup,
                    // rather than deep inside a file read.
                    _ = native.Version;
                    return native;
                }
                catch (DllNotFoundException)
                {
                }
                catch (EntryPointNotFoundException)
                {
                }
                catch (BadImageFormatException)
                {
                }
            }

            // Upstream's ManagedUniversal is deliberately NOT used here. Its
            // ZLIBStream verifies the Adler-32 trailer by seeking to four
            // bytes before the END OF THE STREAM, which only holds when the
            // stream is exactly one zlib member. UOP archives hand the reader
            // a slice, so that check reads the wrong four bytes and every
            // .uop load dies with "CRC mismatch".
            //
            // Rather than debug a vendored reimplementation, use the one the
            // platform ships: System.IO.Compression.ZLibStream, present since
            // .NET 6, is a real zlib and needs no native library. Upstream's
            // class is left in place untouched so the diff stays small.
            return new BclZLib();
        }

        /// <summary>
        /// zlib via the .NET base class library. No native dependency, and no
        /// hand-rolled inflate to get subtly wrong.
        /// </summary>
        private sealed class BclZLib : ICompressor
        {
            public string Version => "bcl";

            public ZLibError Compress(byte[] dest, ref int destLength, byte[] source, int sourceLength)
            {
                using var outStream = new MemoryStream(dest, 0, dest.Length, true);
                using (var z = new System.IO.Compression.ZLibStream(
                    outStream, System.IO.Compression.CompressionMode.Compress, leaveOpen: true))
                {
                    z.Write(source, 0, sourceLength);
                }

                destLength = (int)outStream.Position;
                return ZLibError.Ok;
            }

            public ZLibError Compress(
                byte[] dest, ref int destLength, byte[] source, int sourceLength, ZLibQuality quality)
            {
                return Compress(dest, ref destLength, source, sourceLength);
            }

            public ZLibError Decompress(byte[] dest, ref int destLength, byte[] source, int sourceLength)
            {
                using var inStream = new MemoryStream(source, 0, sourceLength, false);
                return Inflate(inStream, dest.AsSpan(0, destLength));
            }

            public unsafe ZLibError Decompress(IntPtr dest, ref int destLength, IntPtr source, int sourceLength)
            {
                using var inStream = new UnmanagedMemoryStream((byte*)source.ToPointer(), sourceLength);
                return Inflate(inStream, new Span<byte>((byte*)dest.ToPointer(), destLength));
            }

            private static ZLibError Inflate(Stream input, Span<byte> dest)
            {
                try
                {
                    using var z = new System.IO.Compression.ZLibStream(
                        input, System.IO.Compression.CompressionMode.Decompress);

                    int total = 0;
                    while (total < dest.Length)
                    {
                        int read = z.Read(dest.Slice(total));
                        if (read <= 0)
                        {
                            break;
                        }

                        total += read;
                    }

                    // Short reads are normal: callers size the destination
                    // from the archive's stated length and it can be padded.
                    return ZLibError.Ok;
                }
                catch (InvalidDataException)
                {
                    return ZLibError.DataError;
                }
            }
        }

        public static ZLibError Decompress(byte[] source, int offset, byte[] dest, int length)
        {
            return _compressor.Decompress(dest, ref length, source, source.Length - offset);
        }

        public static ZLibError Decompress(IntPtr source, int sourceLength, int offset, IntPtr dest, int length)
        {
            return _compressor.Decompress(dest, ref length, source, sourceLength - offset);
        }

        public static unsafe ZLibError Decompress(ReadOnlySpan<byte> source, Span<byte> dest)
        {
            fixed (byte* srcPtr = source)
            fixed (byte* destPtr = dest)
                return Decompress((IntPtr)srcPtr, source.Length, 0, (IntPtr)destPtr, dest.Length);
        }

        private enum ZLibQuality
        {
            Default = -1,

            None = 0,

            Speed = 1,
            Size = 9
        }

        public enum ZLibError
        {
            VersionError = -6,
            BufferError = -5,
            MemoryError = -4,
            DataError = -3,
            StreamError = -2,
            FileError = -1,

            Ok = 0,

            StreamEnd = 1,
            NeedDictionary = 2
        }


        private interface ICompressor
        {
            string Version { get; }

            ZLibError Compress(byte[] dest, ref int destLength, byte[] source, int sourceLength);
            ZLibError Compress(byte[] dest, ref int destLength, byte[] source, int sourceLength, ZLibQuality quality);

            ZLibError Decompress(byte[] dest, ref int destLength, byte[] source, int sourceLength);
            ZLibError Decompress(IntPtr dest, ref int destLength, IntPtr source, int sourceLength);
        }


        private sealed class Compressor64 : ICompressor
        {
            public string Version => SafeNativeMethods.zlibVersion();

            public ZLibError Compress(byte[] dest, ref int destLength, byte[] source, int sourceLength)
            {
                return SafeNativeMethods.compress(dest, ref destLength, source, sourceLength);
            }

            public ZLibError Compress(byte[] dest, ref int destLength, byte[] source, int sourceLength, ZLibQuality quality)
            {
                return SafeNativeMethods.compress2
                (
                    dest,
                    ref destLength,
                    source,
                    sourceLength,
                    quality
                );
            }

            public ZLibError Decompress(byte[] dest, ref int destLength, byte[] source, int sourceLength)
            {
                return SafeNativeMethods.uncompress(dest, ref destLength, source, sourceLength);
            }

            public ZLibError Decompress(IntPtr dest, ref int destLength, IntPtr source, int sourceLength)
            {
                return SafeNativeMethods.uncompress(dest, ref destLength, source, sourceLength);
            }

            private class SafeNativeMethods
            {
                [DllImport("zlib")]
                public static extern string zlibVersion();

                [DllImport("zlib")]
                public static extern ZLibError compress(byte[] dest, ref int destLength, byte[] source, int sourceLength);

                [DllImport("zlib")]
                public static extern ZLibError compress2(byte[] dest, ref int destLength, byte[] source, int sourceLength, ZLibQuality quality);

                [DllImport("zlib")]
                public static extern ZLibError uncompress(byte[] dest, ref int destLen, byte[] source, int sourceLen);

                [DllImport("zlib")]
                public static extern ZLibError uncompress(IntPtr dest, ref int destLen, IntPtr source, int sourceLen);
            }
        }

        private sealed class CompressorUnix64 : ICompressor
        {
            public string Version => SafeNativeMethods.zlibVersion();

            public ZLibError Compress(byte[] dest, ref int destLength, byte[] source, int sourceLength)
            {
                long destLengthLong = destLength;
                ZLibError z = SafeNativeMethods.compress(dest, ref destLengthLong, source, sourceLength);
                destLength = (int) destLengthLong;

                return z;
            }

            public ZLibError Compress(byte[] dest, ref int destLength, byte[] source, int sourceLength, ZLibQuality quality)
            {
                long destLengthLong = destLength;

                ZLibError z = SafeNativeMethods.compress2
                (
                    dest,
                    ref destLengthLong,
                    source,
                    sourceLength,
                    quality
                );

                destLength = (int) destLengthLong;

                return z;
            }

            public ZLibError Decompress(byte[] dest, ref int destLength, byte[] source, int sourceLength)
            {
                long destLengthLong = destLength;
                ZLibError z = SafeNativeMethods.uncompress(dest, ref destLengthLong, source, sourceLength);
                destLength = (int) destLengthLong;

                return z;
            }

            public ZLibError Decompress(IntPtr dest, ref int destLength, IntPtr source, int sourceLength)
            {
                return SafeNativeMethods.uncompress(dest, ref destLength, source, sourceLength);
            }

            private class SafeNativeMethods
            {
                [DllImport("libz")]
                public static extern string zlibVersion();

                [DllImport("libz")]
                public static extern ZLibError compress(byte[] dest, ref long destLength, byte[] source, long sourceLength);

                [DllImport("libz")]
                public static extern ZLibError compress2(byte[] dest, ref long destLength, byte[] source, long sourceLength, ZLibQuality quality);

                [DllImport("libz")]
                public static extern ZLibError uncompress(byte[] dest, ref long destLen, byte[] source, long sourceLen);

                [DllImport("libz")]
                public static extern ZLibError uncompress(IntPtr dest, ref int destLen, IntPtr source, int sourceLen);
            }
        }

        private sealed class ManagedUniversal : ICompressor
        {
            public string Version => "1.2.11";

            public ZLibError Compress(byte[] dest, ref int destLength, byte[] source, int sourceLength)
            {
                ZLibManaged.Compress(dest, ref destLength, source);

                return ZLibError.Ok;
            }

            public ZLibError Compress(byte[] dest, ref int destLength, byte[] source, int sourceLength, ZLibQuality quality)
            {
                return Compress(dest, ref destLength, source, sourceLength);
            }

            public ZLibError Decompress(byte[] dest, ref int destLength, byte[] source, int sourceLength)
            {
                ZLibManaged.Decompress
                (
                    source,
                    0,
                    sourceLength,
                    0,
                    dest,
                    destLength
                );

                return ZLibError.Ok;
            }

            public ZLibError Decompress(IntPtr dest, ref int destLength, IntPtr source, int sourceLength)
            {
                ZLibManaged.Decompress
                (
                    source,
                    sourceLength,
                    0,
                    dest,
                    destLength
                );

                return ZLibError.Ok;
            }
        }
    }
}