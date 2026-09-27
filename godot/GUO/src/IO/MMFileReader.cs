using System;
using System.IO;
using System.IO.MemoryMappedFiles;

namespace GUO.IO
{
    public class MMFileReader : FileReader
    {
        private readonly MemoryMappedViewAccessor _accessor;
        private readonly MemoryMappedFile _mmf;
        private readonly BinaryReader _file;

        // PORT DEVIATION (GUO): a browser has no mmap over a multi-GB file
        // (the web build reads the install through a lazy, HTTP-backed file
        // in Emscripten's filesystem; ADR-0008), so the reader can go through
        // the FileStream itself instead of a mapped view. Every read already
        // goes through FileReader's BinaryReader, so nothing above changes.
        // On for the web; GUO_NO_MMAP=1 turns it on anywhere, for testing.
        public static readonly bool UseMemoryMap =
            !OperatingSystem.IsBrowser() && Environment.GetEnvironmentVariable("GUO_NO_MMAP") != "1";
        // END PORT DEVIATION (GUO)

        public MMFileReader(FileStream stream) : base(stream)
        {
            if (Length <= 0)
                return;

            // PORT DEVIATION (GUO): see UseMemoryMap.
            if (!UseMemoryMap)
            {
                _file = new BinaryReader(stream);
                return;
            }
            // END PORT DEVIATION (GUO)

            _mmf = MemoryMappedFile.CreateFromFile
            (
                stream,
                null,
                0,
                MemoryMappedFileAccess.Read,
                HandleInheritability.None,
                false
            );

            _accessor = _mmf.CreateViewAccessor(0, Length, MemoryMappedFileAccess.Read);

            try
            {
                unsafe
                {
                    byte* ptr = null;
                    _accessor.SafeMemoryMappedViewHandle.AcquirePointer(ref ptr);
                    _file = new BinaryReader(new UnmanagedMemoryStream(ptr, Length));
                }
            }
            catch (Exception ex)
            {
                _accessor.SafeMemoryMappedViewHandle.ReleasePointer();

                throw new InvalidOperationException("Failed to acquire memory-mapped file pointer.", ex);
            }
        }

        public override BinaryReader Reader => _file;

        public override void Dispose()
        {
            _accessor?.SafeMemoryMappedViewHandle.ReleasePointer();
            _accessor?.Dispose();
            _mmf?.Dispose();

            base.Dispose();
        }
    }
}
