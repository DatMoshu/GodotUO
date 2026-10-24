// SPDX-License-Identifier: BSD-2-Clause
using System;
using GUO.Game.GameObjects;
using GUO.Game.Map;

namespace GUO.Game.Scenes;

// Diagnostic only. Compare a retained suffix with a fresh raw linked-list
// traversal, without consulting the first-Land cache or changing cover stamps.
// This repeats the expensive work deliberately; never enable it for FPS claims.
internal static class CoverCandidateVerifier
{
    internal static readonly bool Enabled = Environment.GetEnvironmentVariable("GUO_VERIFY_COVER_CANDIDATES") == "1";
    internal static long Plans, References, Mismatches;

    internal static void Check<TSource>(TSource map, int x, int y, int rows, int reach, int far, int half,
        ReadOnlySpan<CoverCandidates.Candidate> candidates) where TSource : IChunkSource<Chunk>
    {
        Plans++;
        int at = 0;
        ChunkCursor<Chunk> cursor = default;
        for (int dy = 0; dy <= far; dy++)
        for (int dx = 0; dx <= far; dx++)
        {
            if (dx == 0 && dy == 0) continue;
            if ((dx > reach || dy > reach)
                && (dx + dy > rows || (half >= 0 && Math.Abs(dx - dy) * 22 >= 22 + half))) continue;
            int tileX = x + dx, tileY = y + dy;
            Chunk chunk = cursor.At(tileX, tileY, map);
            GameObject obj = chunk?.GetHeadObject(tileX % 8, tileY % 8);
            while (obj != null && obj is not Land) obj = obj.TNext;
            if (obj is not Land land) continue;
            References++;
            if (at >= candidates.Length || !ReferenceEquals(land, candidates[at].Land)
                || candidates[at].Column != dx - dy) Fail();
            at++;
        }
        if (at != candidates.Length) Fail();
    }

    private static void Fail()
    {
        Mismatches++;
        throw new InvalidOperationException("Retained covering-land candidates differ from the live linked-list traversal.");
    }
}
