using CentrED.MapGen.IR;

namespace CentrED.MapGen.Commit;

// Direct .mul writer that bypasses the CentrED protocol entirely. Loads source
// map + staidx + statics into memory, patches the cells inside IR.Scope, then
// writes the three result files. Roughly 1000× faster than IrCommitter at full
// Felucca scale because there's zero network round-trip overhead.
//
// Source format may be .uop (auto-extracted) or .mul. Output is always .mul —
// Cedserver can read either, and the user can switch the Cedserver.xml paths
// once. Statics + land tiles outside IR.Scope are preserved unchanged.
//
// Safety:
//   * Block dimensions come from Options, else are inferred from the source map
//     (file name + size), never silently assumed to be Felucca; a scope that does not
//     fit the map is refused.
//   * Output dir must differ from every source file's dir (never overwrite the source).
//   * A UOP source with missing/unreadable chunks is refused (they used to shift every
//     later block).
//   * All three outputs are written to .tmp first; only then each is swapped in with
//     File.Replace, keeping the previous file as <name>.bak. A failed swap restores the
//     already-swapped files from their .bak, so the output dir never mixes generations.
public sealed class StandaloneIrWriter
{
    public sealed class Options
    {
        public required string SourceMapPath { get; init; }
        public required string SourceStaidxPath { get; init; }
        public required string SourceStaticsPath { get; init; }
        public required string OutputDir { get; init; }
        /// <summary>Map width in 8x8 blocks. Null = infer from the source map (name + size).</summary>
        public int? BlockWidth { get; init; }
        /// <summary>Map height in 8x8 blocks. Null = infer from the source map (name + size).</summary>
        public int? BlockHeight { get; init; }
        /// <summary>Commit even if a UOP source has missing chunks (they are written as zero blocks).</summary>
        public bool AllowMissingSourceChunks { get; init; } = false;
        public string OutputMapName { get; init; } = "map0.mul";
        public string OutputStaidxName { get; init; } = "staidx0.mul";
        public string OutputStaticsName { get; init; } = "statics0.mul";
        public bool ClearStaticsInScope { get; init; } = false;
        public bool WriteLandTiles { get; init; } = true;
    }

    public sealed class Progress
    {
        public string Stage = "";
        public int Current;
        public int Total;
        public TimeSpan Elapsed;
        public override string ToString() => $"{Stage}: {Current}/{Total} ({Elapsed.TotalSeconds:F1}s)";
    }

    public sealed class Result
    {
        public int LandTilesWritten;
        public int StaticsWritten;
        public int StaticsRemoved;
        public TimeSpan Elapsed;
        public string MapPath = "";
        public string StaidxPath = "";
        public string StaticsPath = "";
        public int BlockWidth;
        public int BlockHeight;
    }

    public Result Write(GenIR ir, Options opt, Action<Progress>? onProgress = null, CancellationToken cancel = default)
    {
        var sw = System.Diagnostics.Stopwatch.StartNew();
        var result = new Result();
        var progress = new Progress();
        RefuseOverwritingSource(opt);
        Directory.CreateDirectory(opt.OutputDir);
        result.MapPath     = Path.Combine(opt.OutputDir, opt.OutputMapName);
        result.StaidxPath  = Path.Combine(opt.OutputDir, opt.OutputStaidxName);
        result.StaticsPath = Path.Combine(opt.OutputDir, opt.OutputStaticsName);

        // ---- Stage 1: load source map ----
        progress.Stage = "Loading source map";
        progress.Total = 1; progress.Current = 0; progress.Elapsed = sw.Elapsed;
        onProgress?.Invoke(progress);
        var (mapData, blockW, blockH) = LoadSourceMap(opt);
        result.BlockWidth = blockW;
        result.BlockHeight = blockH;
        var dims = new Dims(blockW, blockH);
        if (ir.Scope.X2 >= blockW * 8 || ir.Scope.Y2 >= blockH * 8)
            throw new InvalidOperationException(
                $"IR scope ({ir.Scope.X1},{ir.Scope.Y1})-({ir.Scope.X2},{ir.Scope.Y2}) does not fit the source map ({blockW * 8}x{blockH * 8} tiles)");
        progress.Current = 1; progress.Elapsed = sw.Elapsed;
        onProgress?.Invoke(progress);

        // ---- Stage 2: patch land tiles ----
        if (opt.WriteLandTiles && ir.Height_Z is not null && ir.LandId is not null)
        {
            progress.Stage = "Patching land tiles";
            progress.Total = ir.Scope.Height;
            progress.Current = 0;
            for (int y = ir.Scope.Y1; y <= ir.Scope.Y2; y++)
            {
                cancel.ThrowIfCancellationRequested();
                for (int x = ir.Scope.X1; x <= ir.Scope.X2; x++)
                {
                    int blockId = (x >> 3) * blockH + (y >> 3);
                    int tileOffset = blockId * 196 + 4 + ((y & 7) * 8 + (x & 7)) * 3;
                    int idx = ir.Index(x, y);
                    ushort id = ir.LandId[idx];
                    sbyte z = ir.Height_Z[idx];
                    mapData[tileOffset]     = (byte)(id & 0xFF);
                    mapData[tileOffset + 1] = (byte)(id >> 8);
                    mapData[tileOffset + 2] = (byte)z;
                    result.LandTilesWritten++;
                }
                if ((y & 31) == 0) { progress.Current = y - ir.Scope.Y1; progress.Elapsed = sw.Elapsed; onProgress?.Invoke(progress); }
            }
        }

        // ---- Stage 3: load source staidx + statics into per-block lists ----
        progress.Stage = "Loading source statics";
        var blockStatics = LoadSourceStatics(opt, dims, progress, onProgress, sw, cancel);

        // ---- Stage 4: apply IR mutations ----
        progress.Stage = "Applying static mutations";
        ApplyMutations(ir, opt, dims, blockStatics, ref result, progress, onProgress, sw, cancel);

        // ---- Stage 5: build staidx + statics ----
        progress.Stage = "Building staidx + statics";
        var (staidxBytes, staticsBytes) = BuildStatics(dims, blockStatics, progress, onProgress, sw, cancel);

        // ---- Stage 6: write all temps, then swap all three in ----
        progress.Stage = "Writing map + staidx + statics";
        progress.Total = 1; progress.Current = 0; progress.Elapsed = sw.Elapsed;
        onProgress?.Invoke(progress);
        cancel.ThrowIfCancellationRequested();
        WriteAllAtomic(new[]
        {
            (result.MapPath, mapData),
            (result.StaidxPath, staidxBytes),
            (result.StaticsPath, staticsBytes),
        });
        progress.Current = 1; progress.Elapsed = sw.Elapsed;
        onProgress?.Invoke(progress);

        sw.Stop();
        result.Elapsed = sw.Elapsed;
        progress.Stage = "Done";
        progress.Total = 1; progress.Current = 1; progress.Elapsed = sw.Elapsed;
        onProgress?.Invoke(progress);
        return result;
    }

    private readonly record struct Dims(int W, int H)
    {
        public int Blocks => W * H;
    }

    // Never write into a directory holding one of the source files, and never onto a source path.
    private static void RefuseOverwritingSource(Options opt)
    {
        string outDir = Normalize(opt.OutputDir);
        foreach (var src in new[] { opt.SourceMapPath, opt.SourceStaidxPath, opt.SourceStaticsPath })
        {
            string srcDir = Normalize(Path.GetDirectoryName(Path.GetFullPath(src)) ?? "");
            if (string.Equals(srcDir, outDir, StringComparison.OrdinalIgnoreCase))
                throw new InvalidOperationException(
                    $"refusing to commit: output dir {opt.OutputDir} is the source dir of {src}. Write to a separate directory.");
        }
        static string Normalize(string p) => Path.TrimEndingDirectorySeparator(Path.GetFullPath(p));
    }

    private static (byte[] Data, int BlockW, int BlockH) LoadSourceMap(Options opt)
    {
        bool uop = opt.SourceMapPath.EndsWith(".uop", StringComparison.OrdinalIgnoreCase);
        byte[] data;
        if (uop)
        {
            long? expected = opt.BlockWidth is { } w0 && opt.BlockHeight is { } h0 ? (long)w0 * h0 * 196 : null;
            var ex = UopMapExtractor.Extract(opt.SourceMapPath, expected);
            if (ex.MissingChunks.Count > 0 && !opt.AllowMissingSourceChunks)
                throw new InvalidDataException(
                    $"source map {opt.SourceMapPath} is missing {ex.MissingChunks.Count} chunk(s) (first: {ex.MissingChunks[0]}); refusing to commit a map with zeroed blocks");
            data = ex.Data;
        }
        else data = File.ReadAllBytes(opt.SourceMapPath);

        int bw, bh;
        if (opt.BlockWidth is { } w && opt.BlockHeight is { } h) { bw = w; bh = h; }
        else if (!MapDimensions.TryInfer(opt.SourceMapPath, data.Length / 196, out bw, out bh))
            throw new InvalidOperationException(
                $"cannot infer block dimensions of {opt.SourceMapPath} ({data.Length / 196} blocks); pass BlockWidth/BlockHeight");
        long needed = (long)bw * bh * 196;
        if (data.Length < needed)
            throw new InvalidDataException(
                $"source map {opt.SourceMapPath} has {data.Length} bytes, {bw}x{bh} blocks need {needed}; refusing to write a truncated map");
        return (data, bw, bh);
    }

    private static List<RawStatic>?[] LoadSourceStatics(Options opt, Dims dims, Progress progress, Action<Progress>? onProgress,
        System.Diagnostics.Stopwatch sw, CancellationToken cancel)
    {
        int totalBlocks = dims.Blocks;
        var perBlock = new List<RawStatic>?[totalBlocks];
        progress.Total = totalBlocks; progress.Current = 0;

        using var staidx = File.Open(opt.SourceStaidxPath, FileMode.Open, FileAccess.Read, FileShare.Read);
        using var statics = File.Open(opt.SourceStaticsPath, FileMode.Open, FileAccess.Read, FileShare.Read);
        using var staidxR = new BinaryReader(staidx);
        using var staticsR = new BinaryReader(statics);

        for (int blockId = 0; blockId < totalBlocks; blockId++)
        {
            cancel.ThrowIfCancellationRequested();
            staidx.Position = (long)blockId * 12;
            if (staidx.Position + 12 > staidx.Length) break;
            int offset = staidxR.ReadInt32();
            int length = staidxR.ReadInt32();
            staidxR.ReadInt32(); // unknown
            if (offset < 0 || length <= 0) { perBlock[blockId] = null; continue; }
            if ((long)offset + length > statics.Length) { perBlock[blockId] = null; continue; }
            int count = length / 7;
            var list = new List<RawStatic>(count);
            statics.Position = offset;
            for (int i = 0; i < count; i++)
            {
                ushort id = staticsR.ReadUInt16();
                byte lx = staticsR.ReadByte();
                byte ly = staticsR.ReadByte();
                sbyte z = staticsR.ReadSByte();
                ushort hue = staticsR.ReadUInt16();
                list.Add(new RawStatic(id, lx, ly, z, hue));
            }
            perBlock[blockId] = list;
            if ((blockId & 0xFFF) == 0) { progress.Current = blockId; progress.Elapsed = sw.Elapsed; onProgress?.Invoke(progress); }
        }
        progress.Current = totalBlocks; progress.Elapsed = sw.Elapsed; onProgress?.Invoke(progress);
        return perBlock;
    }

    private static void ApplyMutations(GenIR ir, Options opt, Dims dims, List<RawStatic>?[] perBlock, ref Result result,
        Progress progress, Action<Progress>? onProgress, System.Diagnostics.Stopwatch sw, CancellationToken cancel)
    {
        if (opt.ClearStaticsInScope)
        {
            // Filter out any static whose tile-coords lie inside scope.
            for (int blockY = ir.Scope.Y1 >> 3; blockY <= ir.Scope.Y2 >> 3; blockY++)
            {
                for (int blockX = ir.Scope.X1 >> 3; blockX <= ir.Scope.X2 >> 3; blockX++)
                {
                    int blockId = blockX * dims.H + blockY;
                    if (perBlock[blockId] is not { } list) continue;
                    int before = list.Count;
                    list.RemoveAll(s =>
                    {
                        int tx = blockX * 8 + s.Lx;
                        int ty = blockY * 8 + s.Ly;
                        return tx >= ir.Scope.X1 && tx <= ir.Scope.X2 && ty >= ir.Scope.Y1 && ty <= ir.Scope.Y2;
                    });
                    result.StaticsRemoved += before - list.Count;
                }
            }
        }

        progress.Total = ir.StaticOps.Count; progress.Current = 0;
        for (int i = 0; i < ir.StaticOps.Count; i++)
        {
            cancel.ThrowIfCancellationRequested();
            var op = ir.StaticOps[i];
            if (op.X < ir.Scope.X1 || op.X > ir.Scope.X2 || op.Y < ir.Scope.Y1 || op.Y > ir.Scope.Y2) continue;
            int blockX = op.X >> 3;
            int blockY = op.Y >> 3;
            int blockId = blockX * dims.H + blockY;
            if (blockId >= perBlock.Length) continue;
            var list = perBlock[blockId] ??= new List<RawStatic>();
            byte lx = (byte)(op.X & 7);
            byte ly = (byte)(op.Y & 7);
            if (op.Kind == StaticOpKind.Add)
            {
                list.Add(new RawStatic(op.Id, lx, ly, op.Z, op.Hue));
                result.StaticsWritten++;
            }
            else
            {
                int rm = list.RemoveAll(s => s.Id == op.Id && s.Lx == lx && s.Ly == ly && s.Z == op.Z && s.Hue == op.Hue);
                result.StaticsRemoved += rm;
            }
            if ((i & 0xFFFF) == 0) { progress.Current = i; progress.Elapsed = sw.Elapsed; onProgress?.Invoke(progress); }
        }
        progress.Current = ir.StaticOps.Count; progress.Elapsed = sw.Elapsed; onProgress?.Invoke(progress);
    }

    private static (byte[] Staidx, byte[] Statics) BuildStatics(Dims dims, List<RawStatic>?[] perBlock,
        Progress progress, Action<Progress>? onProgress, System.Diagnostics.Stopwatch sw, CancellationToken cancel)
    {
        int totalBlocks = dims.Blocks;
        progress.Total = totalBlocks; progress.Current = 0;

        using var staidxMem = new MemoryStream(totalBlocks * 12);
        using var staticsMem = new MemoryStream();
        using var staidxW = new BinaryWriter(staidxMem);
        using var staticsW = new BinaryWriter(staticsMem);

        int currentOffset = 0;
        for (int blockId = 0; blockId < totalBlocks; blockId++)
        {
            cancel.ThrowIfCancellationRequested();
            var list = perBlock[blockId];
            if (list is null || list.Count == 0)
            {
                // Empty marker per ULTIMA spec: all -1
                staidxW.Write(-1);
                staidxW.Write(-1);
                staidxW.Write(0);
            }
            else
            {
                int len = list.Count * 7;
                staidxW.Write(currentOffset);
                staidxW.Write(len);
                staidxW.Write(0);
                foreach (var s in list)
                {
                    staticsW.Write(s.Id);
                    staticsW.Write(s.Lx);
                    staticsW.Write(s.Ly);
                    staticsW.Write(s.Z);
                    staticsW.Write(s.Hue);
                }
                currentOffset += len;
            }
            if ((blockId & 0xFFF) == 0) { progress.Current = blockId; progress.Elapsed = sw.Elapsed; onProgress?.Invoke(progress); }
        }
        staidxW.Flush(); staticsW.Flush();
        progress.Current = totalBlocks; progress.Elapsed = sw.Elapsed; onProgress?.Invoke(progress);
        return (staidxMem.ToArray(), staticsMem.ToArray());
    }

    /// <summary>
    /// Writes every file to "&lt;path&gt;.tmp", then swaps each into place with File.Replace
    /// (previous file kept as "&lt;path&gt;.bak"). If a swap fails, files already swapped are
    /// restored from their .bak and the remaining temps are deleted, then the error rethrown.
    /// </summary>
    public static void WriteAllAtomic(IReadOnlyList<(string Path, byte[] Data)> files)
    {
        var temps = new List<(string Final, string Temp)>();
        try
        {
            foreach (var (path, data) in files)
            {
                var temp = path + ".tmp";
                File.WriteAllBytes(temp, data);
                temps.Add((path, temp));
            }
        }
        catch
        {
            foreach (var (_, temp) in temps) TryDelete(temp);
            throw;
        }

        var swapped = new List<(string Final, bool HadPrevious)>();
        try
        {
            foreach (var (final, temp) in temps)
            {
                bool had = File.Exists(final);
                if (had) File.Replace(temp, final, final + ".bak", ignoreMetadataErrors: true);
                else File.Move(temp, final);
                swapped.Add((final, had));
            }
        }
        catch
        {
            foreach (var (final, had) in swapped)
            {
                try
                {
                    if (had) File.Copy(final + ".bak", final, overwrite: true);
                    else TryDelete(final);
                }
                catch { /* best effort; the .bak stays on disk */ }
            }
            foreach (var (_, temp) in temps) TryDelete(temp);
            throw;
        }
    }

    private static void TryDelete(string path)
    {
        try { if (File.Exists(path)) File.Delete(path); } catch { /* ignore */ }
    }

    private readonly record struct RawStatic(ushort Id, byte Lx, byte Ly, sbyte Z, ushort Hue);
}
