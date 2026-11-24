// SPDX-License-Identifier: BSD-2-Clause
using System;
using System.IO;
using Godot;
using GUO.IO;

namespace GUO.Assets;

/// <summary>ADR-0054 negative cases: synthetic lengths and block counts, no game files.</summary>
public partial class MapInferenceProbe : Node
{
    // MapBlock is 196 bytes (4-byte header + 64 cells of 3 bytes).
    private const int Block = 196;

    public override void _Ready() => GetTree().Quit(Run() ? 0 : 1);

    internal static bool Run()
    {
        try
        {
            TileData();
            Maps();
            GD.Print("MapInferenceProbe: PASS (tiledata + map negative cases, ADR-0054)");

            return true;
        }
        catch (Exception e)
        {
            GD.PrintErr("MapInferenceProbe: FAIL " + e.Message);

            return false;
        }
    }

    private static void Check(bool ok, string reason) { if (!ok) throw new InvalidOperationException(reason); }

    private static long Old(long groups) => 512 * (4 + 32 * 26) + groups * (4 + 32 * 37);
    private static long New(long groups) => 512 * (4 + 32 * 30) + groups * (4 + 32 * 41);

    private static void TileData()
    {
        // Official: old 512 groups (1,036,288 B) and new 2048 groups (3,188,736 B).
        Check(Old(512) == 1036288 && New(2048) == 3188736, "official length arithmetic");

        foreach (bool v in new[] { false, true })
        {
            // Fits neither: the version decides.
            foreach (long len in new long[] { 0, 1, 100000, -1, 493567, Old(512) + 1, New(2048) - 1, New(2048) + 7, 5000001 })
            {
                Check(!TileDataLayout.Fits(len, true) && !TileDataLayout.Fits(len, false), $"{len} fits neither");
                Check(TileDataLayout.IsOld(len, v) == v, $"fits neither ({len}) keeps version {v}");
            }

            // Truncated and padded official files fit neither layout.
            foreach (long len in new[] { Old(512) - 1, Old(512) - 1188 / 2, Old(512) + 1, Old(512) + 1187, New(2048) - 658, New(2048) + 1, New(2048) + 1315 })
            {
                Check(!TileDataLayout.Fits(len, true) && !TileDataLayout.Fits(len, false), $"{len} is truncated/padded");
                Check(TileDataLayout.IsOld(len, v) == v, $"truncated/padded ({len}) keeps version {v}");
            }
        }

        // Official lengths with the matching version: no override.
        Check(TileDataLayout.IsOld(Old(512), true), "official old, version old");
        Check(!TileDataLayout.IsOld(New(2048), false), "official new, version new");
        // The positive case, for contrast: a clean fit that contradicts the version wins.
        Check(TileDataLayout.IsOld(Old(512), false), "old length overrides new version");
        Check(!TileDataLayout.IsOld(New(2048), true), "new length overrides old version");

        // Ambiguity does exist: 428032 + 1188a == 493568 + 1316b has solutions (gcd 4 divides 65536).
        // The smallest is a=146 old groups == b=82 new groups, 601,480 B, a realistic size.
        // Both layouts fit, so the version decides, whichever way it says.
        long amb = Old(146);
        Check(amb == New(82) && amb == 601480, "smallest ambiguous length");
        Check(TileDataLayout.Fits(amb, true) && TileDataLayout.Fits(amb, false), "ambiguous fits both");
        Check(TileDataLayout.IsOld(amb, true) && !TileDataLayout.IsOld(amb, false), "ambiguous keeps version");

        // Every ambiguous length up to 64 MB keeps the version's answer.
        int found = 0;

        for (long a = 0; Old(a) <= 64L << 20; a++)
        {
            long len = Old(a);

            if (TileDataLayout.Fits(len, false))
            {
                found++;
                Check(TileDataLayout.IsOld(len, true) && !TileDataLayout.IsOld(len, false), $"ambiguous {len}");
            }
        }

        Check(found > 1, "ambiguous lengths enumerated");
    }

    private static void Maps()
    {
        var known = new[] { (7168, 4096), (6144, 4096), (2304, 1600), (2560, 2048), (1448, 1448), (1280, 4096) };

        // Default size: block count equals the default, nothing changes.
        foreach (var (w, h) in known)
        {
            long blocks = (long)(w >> 3) * (h >> 3);
            Check(!MapSizeInference.TryInfer(blocks, w, h, out int ow, out int oh) && ow == w && oh == h, $"default {w}x{h} unchanged");
        }

        // Each Known size, seen from another default, is inferred exactly.
        foreach (var (w, h) in known)
        {
            long blocks = (long)(w >> 3) * (h >> 3);

            foreach (var (dw, dh) in new[] { (2304, 1600), (7168, 4096) })
            {
                bool same = dw == w && dh == h;
                int w2 = w, h2 = h;
                bool r = MapSizeInference.TryInfer(blocks, dw, dh, out int ow, out int oh);
                if (blocks == 81920) { (w2, h2) = dh == 4096 ? (1280, 4096) : (2560, 2048); } // tie, see below
                same = dw == w2 && dh == h2;
                Check(same ? !r : r && ow == w2 && oh == h2, $"known {w}x{h} from default {dw}x{dh}");
            }
        }

        // 2560x2048 and 1280x4096 share a block count (81,920): the default height breaks the tie.
        Check(MapSizeInference.TryInfer(81920, 7168, 4096, out int tw, out int th) && tw == 1280 && th == 4096, "tie keeps default height 4096");
        Check(MapSizeInference.TryInfer(81920, 2304, 1600, out tw, out th) && tw == 2560 && th == 2048, "tie without matching height takes first listed");

        // Unknown size divisible by default height, within 7168 wide: default height, implied width.
        Check(MapSizeInference.TryInfer(512 * 700L, 7168, 4096, out int iw, out int ih) && iw == 5600 && ih == 4096, "implied width 5600");

        // Divisible by the height but wider than 7168: rejected, defaults kept.
        Check(!MapSizeInference.TryInfer(512 * 897L, 7168, 4096, out iw, out ih) && iw == 7168 && ih == 4096, "897 columns rejected");
        Check(!MapSizeInference.TryInfer(512 * 2000L, 6144, 4096, out iw, out ih) && iw == 6144 && ih == 4096, "2000 columns rejected");

        // Zero, negative, not divisible by the height.
        foreach (long blocks in new long[] { 0, -1, -917504, 1, 3, 511, 513, 917503, 917505, 393217 })
        {
            Check(!MapSizeInference.TryInfer(blocks, 7168, 4096, out iw, out ih) && iw == 7168 && ih == 4096, $"count {blocks} rejected");
        }

        // BlockCount: null, empty, not a whole number of blocks, a clean MUL, a UOP with no entries.
        Check(MapSizeInference.BlockCount(null, Block) == -1, "null file");
        string dir = Path.Combine(Path.GetTempPath(), "guo_mapinfer_" + System.Environment.ProcessId);
        Directory.CreateDirectory(dir);

        try
        {
            long Count(string name, int bytes, bool uop)
            {
                string path = Path.Combine(dir, name);
                File.WriteAllBytes(path, new byte[bytes]);

                using FileReader r = uop ? new UOFileUop(path, "build/map0legacymul/{0:D8}.dat") : new UOFileMul(path);

                return MapSizeInference.BlockCount(r, Block);
            }

            Check(Count("empty.mul", 0, false) == -1, "empty MUL");
            Check(Count("odd.mul", Block * 10 + 1, false) == -1, "MUL not a whole block count");
            Check(Count("ten.mul", Block * 10, false) == 10, "MUL of ten blocks");
            Check(Count("noentries.uop", 64, true) == -1, "UOP with no entries");
        }
        finally
        {
            Directory.Delete(dir, true);
        }
    }
}
