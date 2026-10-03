using System.IO.Compression;
using CentrED.MapGen.Commit;
using CentrED.MapGen.IR;
using CentrED.Network;

namespace CentrED.MapGen.Tests;

// StandaloneIrWriter on a synthetic non-Felucca map, and UOP extraction with a gap.
// Everything is written under a fresh temp folder; no UO data is read.
public class CommitWriterTests : IDisposable
{
    private readonly string _dir = Path.Combine(Path.GetTempPath(), "mapgen-commit-" + Guid.NewGuid().ToString("N"));

    public CommitWriterTests() => Directory.CreateDirectory(_dir);

    public void Dispose()
    {
        try { Directory.Delete(_dir, true); } catch { }
    }

    // Malas (map3): 320x256 blocks. Every tile id 0x0003, z 5; one static at (9,9).
    private (string Map, string Staidx, string Statics) MakeMalas(string dir)
    {
        Directory.CreateDirectory(dir);
        const int bw = 320, bh = 256;
        var map = new byte[bw * bh * 196];
        for (int b = 0; b < bw * bh; b++)
        for (int t = 0; t < 64; t++)
        {
            int o = b * 196 + 4 + t * 3;
            map[o] = 0x03; map[o + 1] = 0; map[o + 2] = 5;
        }
        var staidx = new byte[bw * bh * 12];
        for (int b = 0; b < bw * bh; b++)
        {
            BitConverter.GetBytes(-1).CopyTo(staidx, b * 12);
            BitConverter.GetBytes(-1).CopyTo(staidx, b * 12 + 4);
        }
        // One static in block (1,1) at local (1,1) = tile (9,9).
        int blk = 1 * bh + 1;
        BitConverter.GetBytes(0).CopyTo(staidx, blk * 12);
        BitConverter.GetBytes(7).CopyTo(staidx, blk * 12 + 4);
        var statics = new byte[7];
        BitConverter.GetBytes((ushort)0x0ED6).CopyTo(statics, 0);
        statics[2] = 1; statics[3] = 1; statics[4] = 5;
        var paths = (Path.Combine(dir, "map3.mul"), Path.Combine(dir, "staidx3.mul"), Path.Combine(dir, "statics3.mul"));
        File.WriteAllBytes(paths.Item1, map);
        File.WriteAllBytes(paths.Item2, staidx);
        File.WriteAllBytes(paths.Item3, statics);
        return paths;
    }

    private static GenIR SmallIr()
    {
        var ir = new GenIR(16, 16, new RectU16(0, 0, 15, 15), 1);
        ir.Height_Z = new sbyte[256];
        ir.LandId = new ushort[256];
        for (int i = 0; i < 256; i++) { ir.Height_Z[i] = 12; ir.LandId[i] = 0x00A8; }
        ir.StaticOps.Add(new StaticOp(StaticOpKind.Add, 3, 4, 12, 0x0CCD, 0));
        ir.StaticOps.Add(new StaticOp(StaticOpKind.Remove, 9, 9, 5, 0x0ED6, 0));
        return ir;
    }

    [Fact]
    public void RoundTrip_NonFeluccaMap_InfersDims_AndWritesBlocksAtTheRightPlace()
    {
        var (map, staidx, statics) = MakeMalas(Path.Combine(_dir, "src"));
        var outDir = Path.Combine(_dir, "out");
        var res = new StandaloneIrWriter().Write(SmallIr(), new StandaloneIrWriter.Options
        {
            SourceMapPath = map, SourceStaidxPath = staidx, SourceStaticsPath = statics,
            OutputDir = outDir,
            OutputMapName = "map3.mul", OutputStaidxName = "staidx3.mul", OutputStaticsName = "statics3.mul",
        });
        Assert.Equal(320, res.BlockWidth);
        Assert.Equal(256, res.BlockHeight);
        Assert.Equal(256, res.LandTilesWritten);

        var outMap = File.ReadAllBytes(res.MapPath);
        Assert.Equal(new FileInfo(map).Length, outMap.Length);
        // Tile (3,4): block (0,0), cell 4*8+3. Malas block ids are x*256+y.
        int o = (0 * 256 + 0) * 196 + 4 + (4 * 8 + 3) * 3;
        Assert.Equal(0xA8, outMap[o]);
        Assert.Equal(12, (sbyte)outMap[o + 2]);
        // Tile (15,15) is block (1,1) = id 257 under 256-high blocks (would be 513 under Felucca's 512).
        o = (1 * 256 + 1) * 196 + 4 + (7 * 8 + 7) * 3;
        Assert.Equal(0xA8, outMap[o]);
        // Outside the scope stays untouched.
        o = (2 * 256 + 0) * 196 + 4;
        Assert.Equal(0x03, outMap[o]);
        Assert.Equal(5, outMap[o + 2]);

        // Statics: the added tree is the only static; the source static at (9,9) was removed.
        var idx = File.ReadAllBytes(res.StaidxPath);
        var sta = File.ReadAllBytes(res.StaticsPath);
        Assert.Equal(320 * 256 * 12, idx.Length);
        Assert.Equal(7, sta.Length);
        Assert.Equal(0, BitConverter.ToInt32(idx, 0));
        Assert.Equal(7, BitConverter.ToInt32(idx, 4));
        Assert.Equal(-1, BitConverter.ToInt32(idx, (1 * 256 + 1) * 12));
        Assert.Equal(0x0CCD, BitConverter.ToUInt16(sta, 0));
        Assert.Equal(3, sta[2]);
        Assert.Equal(4, sta[3]);
        Assert.Equal(1, res.StaticsRemoved);

        // No temp files left behind; a second commit keeps the previous files as .bak.
        Assert.Empty(Directory.GetFiles(outDir, "*.tmp"));
        new StandaloneIrWriter().Write(SmallIr(), new StandaloneIrWriter.Options
        {
            SourceMapPath = map, SourceStaidxPath = staidx, SourceStaticsPath = statics,
            OutputDir = outDir,
            OutputMapName = "map3.mul", OutputStaidxName = "staidx3.mul", OutputStaticsName = "statics3.mul",
        });
        Assert.True(File.Exists(res.MapPath + ".bak"));
        Assert.Empty(Directory.GetFiles(outDir, "*.tmp"));
    }

    [Fact]
    public void Commit_RefusesToWriteIntoTheSourceFolder()
    {
        var (map, staidx, statics) = MakeMalas(Path.Combine(_dir, "src"));
        var before = File.GetLastWriteTimeUtc(map);
        var ex = Assert.Throws<InvalidOperationException>(() => new StandaloneIrWriter().Write(SmallIr(), new StandaloneIrWriter.Options
        {
            SourceMapPath = map, SourceStaidxPath = staidx, SourceStaticsPath = statics,
            OutputDir = Path.Combine(_dir, "src") + Path.DirectorySeparatorChar,
            OutputMapName = "map3.mul",
        }));
        Assert.Contains("refusing", ex.Message);
        Assert.Equal(before, File.GetLastWriteTimeUtc(map));
    }

    [Fact]
    public void Commit_RefusesAScopeLargerThanTheMap()
    {
        var (map, staidx, statics) = MakeMalas(Path.Combine(_dir, "src"));
        var ir = new GenIR(2600, 8, new RectU16(2550, 0, 2599, 7), 1);
        Assert.Throws<InvalidOperationException>(() => new StandaloneIrWriter().Write(ir, new StandaloneIrWriter.Options
        {
            SourceMapPath = map, SourceStaidxPath = staidx, SourceStaticsPath = statics,
            OutputDir = Path.Combine(_dir, "out2"),
        }));
    }

    [Theory]
    [InlineData("map0.mul", 896 * 512, 896, 512)]
    [InlineData("map1.mul", 768 * 512, 768, 512)]
    [InlineData("map2.mul", 288 * 200, 288, 200)]
    [InlineData("map3.mul", 320 * 256, 320, 256)]
    [InlineData("map4.mul", 181 * 181, 181, 181)]
    [InlineData("map5LegacyMUL.uop", 160 * 512, 160, 512)]
    public void MapDimensions_InferFromNameAndSize(string name, long blocks, int w, int h)
    {
        Assert.True(MapDimensions.TryInfer(name, blocks, out var bw, out var bh));
        Assert.Equal((w, h), (bw, bh));
    }

    [Fact]
    public void MapDimensions_UnknownSize_DoesNotGuess()
    {
        Assert.False(MapDimensions.TryInfer("custom.mul", 12345, out _, out _));
    }

    // ------------------------------------------------------------------ UOP

    // Writes a minimal MYP file "<name>.uop" whose entries are the given chunks (null = absent).
    private static void WriteUop(string path, IReadOnlyList<byte[]?> chunks, bool compress)
    {
        string fileBase = Path.GetFileNameWithoutExtension(path).ToLowerInvariant();
        using var fs = File.Create(path);
        using var bw = new BinaryWriter(fs);
        bw.Write(0x50594D);       // MYP
        bw.Write(5); bw.Write(0xFD23EC43u); // version, signature
        long nextBlockPos = fs.Position;
        bw.Write(0L);             // next block (patched)
        bw.Write(chunks.Count);   // capacity
        bw.Write(chunks.Count);   // file count

        var present = chunks.Select((c, i) => (c, i)).Where(t => t.c is not null).ToList();
        long tableStart = fs.Position;
        bw.Write(present.Count);
        bw.Write(0L);             // no further block
        long entriesPos = fs.Position;
        fs.Position += present.Count * 34L;

        var entries = new List<(long Offset, int Comp, int Decomp, ulong Hash, short Flag)>();
        foreach (var (data, i) in present)
        {
            byte[] payload = data!;
            short flag = 0;
            if (compress)
            {
                using var ms = new MemoryStream();
                using (var z = new ZLibStream(ms, CompressionLevel.Fastest, leaveOpen: true)) z.Write(data!, 0, data!.Length);
                payload = ms.ToArray();
                flag = 1;
            }
            long off = fs.Position;
            bw.Write(payload);
            entries.Add((off, payload.Length, data!.Length, Uop.HashFileName($"build/{fileBase}/{i:D8}.dat"), flag));
        }
        fs.Position = entriesPos;
        foreach (var e in entries)
        {
            bw.Write(e.Offset); bw.Write(0); bw.Write(e.Comp); bw.Write(e.Decomp);
            bw.Write(e.Hash); bw.Write(0u); bw.Write(e.Flag);
        }
        fs.Position = nextBlockPos;
        bw.Write(tableStart);
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public void UopExtract_LeavesAGapForAMissingChunk_InsteadOfShifting(bool compress)
    {
        const int chunk = 196 * 4;
        byte[] Fill(byte v) => Enumerable.Repeat(v, chunk).ToArray();
        var path = Path.Combine(_dir, "map9LegacyMUL.uop");
        WriteUop(path, new[] { Fill(1), null, Fill(3), Enumerable.Repeat((byte)4, 196).ToArray() }, compress);

        var ex = UopMapExtractor.Extract(path);
        Assert.Equal(chunk, ex.ChunkSize);
        Assert.Equal(new[] { 1 }, ex.MissingChunks);
        Assert.Equal(3 * chunk + 196, ex.Data.Length);
        Assert.Equal(1, ex.Data[0]);
        Assert.Equal(0, ex.Data[chunk]);            // gap stays empty
        Assert.Equal(3, ex.Data[2 * chunk]);        // chunk 2 at its own offset, not shifted down
        Assert.Equal(4, ex.Data[3 * chunk]);

        // Fixed-size request pads/truncates.
        Assert.Equal(chunk * 2, UopMapExtractor.Extract(path, chunk * 2).Data.Length);
    }

    [Fact]
    public void Commit_RefusesAUopSourceWithMissingChunks()
    {
        var src = Path.Combine(_dir, "uopsrc");
        var (_, staidx, statics) = MakeMalas(src);
        // Malas-sized UOP (320*256 blocks) in 4096-block chunks, chunk 3 missing.
        const int blocksPerChunk = 4096;
        int total = 320 * 256;
        var chunks = new List<byte[]?>();
        for (int b = 0; b < total; b += blocksPerChunk)
            chunks.Add(new byte[Math.Min(blocksPerChunk, total - b) * 196]);
        chunks[3] = null;
        var uop = Path.Combine(src, "map3LegacyMUL.uop");
        WriteUop(uop, chunks, compress: false);

        var opt = new StandaloneIrWriter.Options
        {
            SourceMapPath = uop, SourceStaidxPath = staidx, SourceStaticsPath = statics,
            OutputDir = Path.Combine(_dir, "uopout"),
        };
        var err = Assert.Throws<InvalidDataException>(() => new StandaloneIrWriter().Write(SmallIr(), opt));
        Assert.Contains("missing", err.Message);

        var res = new StandaloneIrWriter().Write(SmallIr(), new StandaloneIrWriter.Options
        {
            SourceMapPath = uop, SourceStaidxPath = staidx, SourceStaticsPath = statics,
            OutputDir = Path.Combine(_dir, "uopout"), AllowMissingSourceChunks = true,
        });
        Assert.Equal((320, 256), (res.BlockWidth, res.BlockHeight));
        Assert.Equal(320L * 256 * 196, new FileInfo(res.MapPath).Length);
    }
}
