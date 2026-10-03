using System.IO.Compression;
using CentrED;

namespace CentrED.MapGen.Commit;

/// <summary>
/// Extracts a flat .mul-equivalent byte buffer from a UO map .uop file. Read-only. This
/// is the ONE extractor: MapMiner's reader delegates here.
///
/// <para>Each UOP entry "build/&lt;name&gt;/NNNNNNNN.dat" holds a fixed-size run of 196-byte map
/// blocks (0xC4000 bytes = 4096 blocks, the last one shorter). Entry N is written at
/// N * chunkSize, so a missing or unreadable entry leaves a zero gap instead of shifting
/// every later block (which silently moved terrain on commit). Missing entries are
/// reported in <see cref="Extraction.MissingChunks"/>.</para>
/// </summary>
public static class UopMapExtractor
{
    public sealed class Extraction
    {
        public required byte[] Data { get; init; }
        public int ChunkCount { get; init; }
        public int ChunkSize { get; init; }
        public List<int> MissingChunks { get; } = new();
    }

    /// <summary>
    /// Back-compat entry: returns exactly blockWidth*blockHeight*196 bytes. Missing chunks
    /// stay zero-filled; use <see cref="Extract"/> to find out about them.
    /// </summary>
    public static byte[] ExtractMap(string uopPath, int blockWidth, int blockHeight) =>
        Extract(uopPath, (long)blockWidth * blockHeight * 196).Data;

    /// <summary>
    /// Extracts the map. With <paramref name="expectedBytes"/> the output is exactly that long
    /// (truncated or zero-padded); without, it is sized from the UOP's own entries.
    /// </summary>
    public static Extraction Extract(string uopPath, long? expectedBytes = null)
    {
        using var fs = File.Open(uopPath, FileMode.Open, FileAccess.Read, FileShare.Read);
        using var br = new BinaryReader(fs);

        if (br.ReadInt32() != 0x50594D) // "MYP"
            throw new InvalidDataException($"Not a UOP file: {uopPath}");
        br.ReadInt64();                  // version + signature
        long nextBlock = br.ReadInt64();
        br.ReadInt32();                  // capacity
        int totalCount = br.ReadInt32();

        // Entry names hash from the file name: "map0LegacyMUL" -> "build/map0legacymul/00000000.dat".
        string fileBase = Path.GetFileNameWithoutExtension(uopPath).ToLowerInvariant();
        var hashes = new Dictionary<ulong, int>(totalCount);
        for (int i = 0; i < totalCount; i++)
            hashes[Uop.HashFileName($"build/{fileBase}/{i:D8}.dat")] = i;

        var chunks = new (long Offset, int Length, short Flag, int Decompressed)[totalCount];
        fs.Position = nextBlock;
        while (nextBlock != 0)
        {
            int filesCount = br.ReadInt32();
            nextBlock = br.ReadInt64();
            for (int i = 0; i < filesCount; i++)
            {
                long offset = br.ReadInt64();
                int headerLen = br.ReadInt32();
                int compressedLen = br.ReadInt32();
                int decompressedLen = br.ReadInt32();
                ulong hash = br.ReadUInt64();
                br.ReadUInt32();                // adler
                short flag = br.ReadInt16();
                if (offset == 0) continue;
                if (!hashes.TryGetValue(hash, out int idx)) continue;
                int len = flag == 1 ? compressedLen : decompressedLen;
                chunks[idx] = (offset + headerLen, len, flag, decompressedLen);
            }
            if (nextBlock != 0) fs.Position = nextBlock;
        }

        // Fixed chunk stride: the largest entry (all but the last are full-size).
        int chunkSize = 0;
        foreach (var c in chunks) if (c.Offset != 0) chunkSize = Math.Max(chunkSize, c.Decompressed);
        if (chunkSize == 0) throw new InvalidDataException($"UOP has no map entries: {uopPath}");

        long natural = 0;
        for (int i = 0; i < chunks.Length; i++)
        {
            long len = chunks[i].Offset != 0 ? chunks[i].Decompressed : chunkSize;
            natural = Math.Max(natural, (long)i * chunkSize + len);
        }
        long size = expectedBytes ?? natural;
        if (size > int.MaxValue) throw new InvalidDataException($"map too large to extract: {size} bytes");

        var output = new byte[size];
        var result = new Extraction { Data = output, ChunkCount = chunks.Length, ChunkSize = chunkSize };
        for (int i = 0; i < chunks.Length; i++)
        {
            long start = (long)i * chunkSize;
            if (start >= size) break;
            var c = chunks[i];
            if (c.Offset == 0) { result.MissingChunks.Add(i); continue; }
            byte[] data;
            try { data = ReadChunk(fs, br, c.Offset, c.Length, c.Flag, c.Decompressed); }
            catch (Exception) { result.MissingChunks.Add(i); continue; }
            int copy = (int)Math.Min(data.Length, size - start);
            Buffer.BlockCopy(data, 0, output, (int)start, copy);
        }
        return result;
    }

    private static byte[] ReadChunk(FileStream fs, BinaryReader br, long offset, int length, short flag, int decompressed)
    {
        fs.Position = offset;
        if (flag != 1) return br.ReadBytes(length);
        var compressed = br.ReadBytes(length);
        var data = new byte[decompressed];
        using var ms = new MemoryStream(compressed);
        ms.Position = 2; // skip zlib header
        using var ds = new DeflateStream(ms, CompressionMode.Decompress);
        int got = 0;
        while (got < data.Length)
        {
            int n = ds.Read(data, got, data.Length - got);
            if (n <= 0) break;
            got += n;
        }
        if (got != data.Length) throw new InvalidDataException($"short chunk: {got}/{data.Length}");
        return data;
    }
}

/// <summary>Known UO facet sizes, for inferring block dimensions from a map file.</summary>
public static class MapDimensions
{
    // (map index, block width, block height). Felucca/Trammel also exist in the older 768-wide layout.
    private static readonly (int Map, int W, int H)[] Known =
    {
        (0, 896, 512), (1, 896, 512), (0, 768, 512), (1, 768, 512),
        (2, 288, 200), (3, 320, 256), (4, 181, 181), (5, 160, 512),
    };

    /// <summary>
    /// Infers (blockWidth, blockHeight) from the map file name (map&lt;N&gt;…) and its block count.
    /// Returns false when nothing matches or the count is ambiguous without a map index.
    /// </summary>
    public static bool TryInfer(string mapPath, long blockCount, out int blockWidth, out int blockHeight)
    {
        blockWidth = blockHeight = 0;
        int? mapIndex = null;
        var name = Path.GetFileName(mapPath).ToLowerInvariant();
        if (name.StartsWith("map") && name.Length > 3 && char.IsDigit(name[3])) mapIndex = name[3] - '0';

        // Exact block-count match first (a .mul may carry trailing bytes, so also accept >=).
        var exact = Known.Where(k => (long)k.W * k.H == blockCount && (mapIndex is null || k.Map == mapIndex)).ToList();
        if (exact.Count == 0 && mapIndex is not null)
            exact = Known.Where(k => k.Map == mapIndex && (long)k.W * k.H <= blockCount).OrderByDescending(k => k.W * k.H).Take(1).ToList();
        var dims = exact.Select(k => (k.W, k.H)).Distinct().ToList();
        if (dims.Count != 1) return false;
        (blockWidth, blockHeight) = dims[0];
        return true;
    }
}
