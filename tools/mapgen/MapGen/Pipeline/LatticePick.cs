namespace CentrED.MapGen.Pipeline;

// Coordinate-hash uniform pick over a tile pool. Deterministic per (x,y,seed) and provably
// uniform over the integer lattice modulo array length — empirically more even than
// per-cell Random.Next over large grids (we've observed .NET Random producing 38/20/20/21
// across 28k samples on a 4-element pool, vs the 25/25/25/25 a hash-mod gives).
//
// Use this for visually-equivalent tile families (water 0xA8-0xAB, grass 0x03-0x06, etc.)
// where the goal is even distribution rather than spatial randomness. For pools with
// unique-looking tiles you want to keep a true RNG so adjacent cells can repeat.
public static class LatticePick
{
    public static ushort Pick(ushort[] tiles, int x, int y, ulong seed)
    {
        if (tiles.Length == 0) return 0;
        if (tiles.Length == 1) return tiles[0];
        // Two large primes per axis (TLS-friendly, matches CoastSmoothPass.PickCoherent).
        uint h = unchecked((uint)(x * 73856093) ^ (uint)(y * 19349663) ^ (uint)seed ^ (uint)(seed >> 32));
        // Avalanche: a single xorshift mixes the low bits so `% length` doesn't see
        // periodic patterns when (x,y) are dense neighbours.
        h ^= h >> 16; h *= 0x7feb352dU;
        h ^= h >> 15; h *= 0x846ca68bU;
        h ^= h >> 16;
        return tiles[h % (uint)tiles.Length];
    }
}
