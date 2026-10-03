namespace CentrED.MapGen.Passes.Biome;

// Shared shoreline-rim sprite matcher used by AutoCoastPass (legacy land-side
// detection) and DigShorePass (water-side rim placement, the Felucca-correct
// pattern). The 12-row Transitions table mirrors CentrED's CoastlineTool
// _transitionTiles.
internal static class CoastRimMatcher
{
    [Flags]
    public enum Dir : byte
    {
        None = 0,
        N = 1 << 0, R = 1 << 1, E = 1 << 2, D = 1 << 3,
        S = 1 << 4, L = 1 << 5, W = 1 << 6, U = 1 << 7,
    }

    public static readonly (int Dx, int Dy, Dir Bit)[] Offsets =
    {
        (0, -1, Dir.N), (1, -1, Dir.R), (1, 0, Dir.E), (1, 1, Dir.D),
        (0,  1, Dir.S), (-1, 1, Dir.L), (-1, 0, Dir.W), (-1, -1, Dir.U),
    };

    public static readonly (Dir Full, Dir Partial, ushort[] Tiles)[] Transitions =
    {
        (Dir.W,                   Dir.L | Dir.U, new ushort[] { 0x179D, 0x179E }),
        (Dir.S,                   Dir.L | Dir.D, new ushort[] { 0x179F, 0x17A0 }),
        (Dir.N,                   Dir.U | Dir.R, new ushort[] { 0x17A1, 0x17A2 }),
        (Dir.E,                   Dir.R | Dir.D, new ushort[] { 0x17A3, 0x17A4 }),
        (Dir.L,                   Dir.None,      new ushort[] { 0x17A5 }),
        (Dir.D,                   Dir.None,      new ushort[] { 0x17A6 }),
        (Dir.U,                   Dir.None,      new ushort[] { 0x17A7 }),
        (Dir.R,                   Dir.None,      new ushort[] { 0x17A8 }),
        (Dir.S | Dir.L | Dir.W,   Dir.D | Dir.U, new ushort[] { 0x17A9 }),
        (Dir.W | Dir.U | Dir.N,   Dir.L | Dir.R, new ushort[] { 0x17AA }),
        (Dir.N | Dir.R | Dir.E,   Dir.D | Dir.U, new ushort[] { 0x17AB }),
        (Dir.E | Dir.D | Dir.S,   Dir.L | Dir.R, new ushort[] { 0x17AC }),
    };

    // Flip mask through the iso-direction axis: N↔S, E↔W, R↔L, D↔U. Used by
    // DigShorePass to translate "land neighbours from a water cell's POV" into
    // the "water neighbours from a land cell's POV" frame the Transitions table
    // was authored in. Sprite orientation contract is preserved.
    public static Dir Flip(Dir m)
    {
        Dir r = Dir.None;
        if ((m & Dir.N) != 0) r |= Dir.S;
        if ((m & Dir.S) != 0) r |= Dir.N;
        if ((m & Dir.E) != 0) r |= Dir.W;
        if ((m & Dir.W) != 0) r |= Dir.E;
        if ((m & Dir.R) != 0) r |= Dir.L;
        if ((m & Dir.L) != 0) r |= Dir.R;
        if ((m & Dir.D) != 0) r |= Dir.U;
        if ((m & Dir.U) != 0) r |= Dir.D;
        return r;
    }

    // Deterministic variant: picks between a row's alternatives by a per-cell hash, so the
    // result does not depend on how many random draws earlier passes made.
    public static ushort Match(Dir mask, uint hash)
    {
        foreach (var (full, partial, tiles) in Transitions)
        {
            if ((mask & full) != full) continue;
            if ((mask & ~(full | partial)) != Dir.None) continue;
            return tiles[hash % (uint)tiles.Length];
        }
        ushort fallback = 0;
        int bestPop = 0;
        foreach (var (full, _, tiles) in Transitions)
        {
            if ((mask & full) != full) continue;
            int pop = System.Numerics.BitOperations.PopCount((uint)(byte)full);
            if (pop > bestPop)
            {
                bestPop = pop;
                fallback = tiles[hash % (uint)tiles.Length];
            }
        }
        return fallback;
    }

    // Strict CoastlineTool match (full required, no bits outside full|partial)
    // with a "largest contained subset" fallback. Returns 0 if nothing matched.
    public static ushort Match(Dir mask, Random rng)
    {
        foreach (var (full, partial, tiles) in Transitions)
        {
            if ((mask & full) != full) continue;
            if ((mask & ~(full | partial)) != Dir.None) continue;
            return tiles[rng.Next(tiles.Length)];
        }

        ushort fallback = 0;
        int bestPop = 0;
        foreach (var (full, _, tiles) in Transitions)
        {
            if ((mask & full) != full) continue;
            int pop = System.Numerics.BitOperations.PopCount((uint)(byte)full);
            if (pop > bestPop)
            {
                bestPop = pop;
                fallback = tiles[rng.Next(tiles.Length)];
            }
        }
        return fallback;
    }
}
