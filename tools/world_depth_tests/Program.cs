using GUO.Renderer;
using Draw = GUO.Renderer.WorldDepthOrder.Draw;
using Segment = GUO.Renderer.WorldDepthOrder.Segment;

int checks = 0;
void Require(bool value, string name)
{
    if (!value) throw new Exception(name);
    checks++;
}

// Independent oracle: exactly RenderLists' established depth, then queue Seq.
Draw[] Oracle(IEnumerable<Draw> draws) => draws.OrderBy(d => d.Depth).ThenBy(d => d.Seq).ToArray();

uint[] Pack(Draw[] ordered, int[] expectedSeq, string name)
{
    Require(ordered.Select(d => d.Seq).SequenceEqual(expectedSeq), name + " complete oracle order");
    uint[] keys = new uint[ordered.Length];
    Require(WorldDepthOrder.TryPack(ordered, keys), name + " packs");
    // Sorting by the generated GPU key must preserve every entry, not just a winner.
    Require(ordered.Zip(keys).OrderBy(p => p.Second).Select(p => p.First.Seq)
        .SequenceEqual(expectedSeq), name + " complete key order");
    for (int i = 1; i < keys.Length; i++)
        Require(WorldDepthKey.Encode(keys[i - 1]) < WorldDepthKey.Encode(keys[i]), name + " distinct depth");
    return keys;
}

// Different x+y diagonals can have exactly equal depth (priority -127 vs -27).
// Seq comes from submission; no coordinate is available to the production planner.
Pack(Oracle(new[] { new Draw(16f, 8, 1600, 1, 0), new Draw(16f, 3, 1600, 0, 0) }),
    new[] { 3, 8 }, "cross-diagonal equal-D");

// Chunk traversal queues (1,15) before (0,16), despite global-x order.
Pack(Oracle(new[] { new Draw(17f, 5, 1700, 0, 0), new Draw(17f, 4, 1700, 1, 0) }),
    new[] { 4, 5 }, "cross-chunk traversal");

// Seventeen same-tile objects remain distinct; no four-bit tile-index truncation.
var pile = Enumerable.Range(0, 17).Select(i => new Draw(20f, i, 2000, i % 2, i % 3)).ToArray();
Pack(pile, Enumerable.Range(0, 17).ToArray(), "above16 tile entries");

// Rebuild the complete rank run when dynamic membership changes. Compare the
// static's output before/after; retaining its old rank would collide with the insert.
var before = Oracle(new[] { new Draw(30, 2, 3000, 0, 0), new Draw(30, 6, 3000, 1, 0) });
var inserted = Oracle(before.Append(new Draw(30, 4, 3000, 2, 1)));
uint[] beforeKeys = Pack(before, new[] { 2, 6 }, "before insertion");
uint[] insertedKeys = Pack(inserted, new[] { 2, 4, 6 }, "after insertion");
Require(beforeKeys[1] != insertedKeys[2], "static rank refreshed after dynamic insertion");
uint[] removedKeys = Pack(Oracle(inserted.Where(d => d.Seq != 4)), new[] { 2, 6 }, "after removal");
Require(beforeKeys.SequenceEqual(removedKeys), "removal restores complete order and ranks");

// Mixed blend modes/pages must keep A,B,A as three segments, never material-bucket A,A,B.
var mixed = new[] {
    new Draw(1, 0, 100, 0, 0), new Draw(1, 1, 100, 0, 0),
    new Draw(1, 2, 100, 1, 0), new Draw(1, 3, 100, 1, 2),
    new Draw(1, 4, 100, 0, 0)
};
Segment[] runs = new Segment[mixed.Length];
int runCount = WorldDepthOrder.OrderedSegments(mixed, runs);
Require(runs.Take(runCount).SequenceEqual(new[] {
    new Segment(0, 2, 0, 0), new Segment(2, 1, 1, 0),
    new Segment(3, 1, 1, 2), new Segment(4, 1, 0, 0)
}), "complete mixed page/blend segments");
Require(runs.Take(runCount).SelectMany(r => mixed.Skip(r.Start).Take(r.Count)).SequenceEqual(mixed),
    "fallback preserves complete draw order");

void Reject(Draw[] draws, string name)
{
    uint[] keys = Enumerable.Repeat(uint.MaxValue, draws.Length).ToArray();
    Require(!WorldDepthOrder.TryPack(draws, keys), name + " rejected");
    Require(keys.All(k => k == 0), name + " leaves no partial keys");
    var segments = new Segment[draws.Length];
    int count = WorldDepthOrder.OrderedSegments(draws, segments);
    Require(segments.Take(count).SelectMany(r => draws.Skip(r.Start).Take(r.Count)).SequenceEqual(draws),
        name + " fallback retains complete oracle input");
}

var maximumRun = Enumerable.Range(0, 512).Select(i => new Draw(1, i, (1 << 19) - 1, i % 2, i % 3)).ToArray();
uint[] maximumKeys = Pack(maximumRun, Enumerable.Range(0, 512).ToArray(), "maximum bucket and rank");
Require(maximumKeys[^1] == WorldDepthKey.Count - 1, "highest packed key valid");
Reject(maximumRun.Append(new Draw(1, 512, (1 << 19) - 1, 2, 1)).ToArray(), "rank overflow");
Reject(new[] { new Draw(0, 0, -1, 0, 0) }, "negative bucket");
Reject(new[] { new Draw(0, 0, 1 << 19, 0, 0) }, "bucket overflow");
Reject(new[] { new Draw(float.NaN, 0, 0, 0, 0) }, "nonfinite depth");
Reject(new[] { new Draw(0, 1, 0, 0, 0), new Draw(0, 0, 0, 1, 0) }, "wrong submission order");
Reject(new[] { new Draw(0, 0, 1, 0, 0), new Draw(1, 1, 0, 1, 0) }, "bucket reverses oracle");
Reject(new[] { new Draw(0, 0, 0, 0, 0), new Draw(0, 0, 0, 1, 0) }, "ambiguous duplicate sequence");
Require(WorldDepthOrder.TryPack(Array.Empty<Draw>(), Array.Empty<uint>()), "empty run");

// ---- GPU-01: the key's meaning (WorldDepthKey layout) -------------------------

// Depth: every (x + y) the maps have and every PriorityZ a chain can carry,
// plus the mobile body's + 0.5, round to the exact hundredth and keep the
// float's order. This is the whole domain CalculateDepthZ() produces.
{
    var domain = new List<(float Depth, uint D)>(4_500_000);
    bool exact = true;
    for (int s = 0; s <= 7168 + 4096 + 2; s++)
    {
        for (int z = -130; z <= 130 + 50; z++)
        {
            float depth = s + (127 + z) * 0.01f;
            uint d = WorldDepthKey.DepthAbs(depth);
            exact &= d == (uint)(100 * s + 127 + z + WorldDepthKey.DepthShift);
            domain.Add((depth, d));
        }
    }
    Require(exact, "depth part is the exact hundredth over the whole domain");
    domain.Sort((a, b) => a.Depth.CompareTo(b.Depth));
    // Below x + y = 32 the float still separates (127 + z) * 0.01f from
    // (127 + z - 100) * 0.01f + 1 by a few ulps, while both are the same
    // hundredth; from 32 on the two round to one float and the orders agree.
    // The client never draws those 32 diagonals (the map's void corner), and
    // GpuWorld refuses a frame whose first diagonal is under this bound.
    bool monotone = true, cornerOnly = true;
    int disagreements = 0;
    for (int i = 1; i < domain.Count; i++)
    {
        bool ok = domain[i].Depth == domain[i - 1].Depth ? domain[i].D == domain[i - 1].D : domain[i].D > domain[i - 1].D;
        if (!ok)
        {
            disagreements++;
            cornerOnly &= domain[i].Depth < WorldDepthKey.FirstExactDiagonal;
            if (Environment.GetEnvironmentVariable("WDT_DIAG") != null)
                Console.WriteLine($"mismatch depth {domain[i - 1].Depth:R} D {domain[i - 1].D} vs depth {domain[i].Depth:R} D {domain[i].D}");
        }
        monotone &= ok || domain[i].Depth < WorldDepthKey.FirstExactDiagonal;
    }
    Require(monotone && cornerOnly, "depth part keeps the float depth's order from diagonal 32 on");
    Require(disagreements > 0 && disagreements < 400, "the float disagrees only in the void corner (known, bounded)");
    Require(domain.Count > 3_000_000, "depth domain visited");
}
// The body's + 0.5f lands on + 50 exactly, from the float the sort uses.
Require(WorldDepthKey.DepthAbs(1234f + (127 + 10) * 0.01f + 0.5f) == WorldDepthKey.DepthAbs(1234f + (127 + 10) * 0.01f) + 50, "mobile body + 0.5 is + 50");

// Tile order: the walk is chunk x, chunk y, then x and y within the chunk.
// For every pair of tiles on one diagonal, TileOrderAbs must agree with it.
{
    static (int, int, int, int) Walk(int x, int y) => (x >> 3, y >> 3, x & 7, y & 7);
    bool agrees = true;
    int pairs = 0;
    for (int s = 0; s < 200; s++)
    {
        for (int xa = 0; xa <= s; xa++)
        {
            int ya = s - xa;
            for (int xb = xa + 1; xb <= s; xb++)
            {
                int yb = s - xb;
                int walk = Walk(xa, ya).CompareTo(Walk(xb, yb));
                int order = WorldDepthKey.TileOrderAbs(xa, ya).CompareTo(WorldDepthKey.TileOrderAbs(xb, yb));
                agrees &= walk != 0 && Math.Sign(walk) == Math.Sign(order);
                pairs++;
            }
        }
    }
    Require(agrees && pairs > 1_000_000, "tile order matches the chunk walk on every diagonal pair");
    // The audit's cases: (0,16) walks after (1,15); (7,9) and (8,7) share D=1726 across diagonals.
    Require(WorldDepthKey.TileOrderAbs(1, 15) < WorldDepthKey.TileOrderAbs(0, 16), "chunk walk, not global x");
    Require(WorldDepthKey.DepthAbs(16f + (127 - 1) * 0.01f) == WorldDepthKey.DepthAbs(15f + (127 + 99) * 0.01f), "equal D across diagonals");
}

// Dense within a chunk column: eight tiles, indices 0..7, each once.
{
    bool dense = true;
    for (int s = 0; s < 64; s++)
    {
        var seen = new HashSet<int>();
        for (int i = 0; i < 8; i++)
        {
            int x = 8 + i, y = s + 40 - x;
            if (y < 0) continue;
            int order = WorldDepthKey.TileOrderAbs(x, y);
            dense &= order / 8 == 1 && seen.Add(order % 8);
        }
    }
    Require(dense, "tile order is dense within a chunk column");
}

// Slots interleave statics and queued objects as the chain does: static rank r
// at 2r + 1, a queued object after k statics at 2k; covering land at 0.
{
    var keys = new List<(string Name, uint Key)>();
    int x = 100, y = 50, s = x + y;
    uint depth = WorldDepthKey.DepthAbs(s + 127 * 0.01f);
    uint origin = WorldDepthKey.DepthOrigin(s - 5);
    uint K(int slot) { Require(WorldDepthKey.Relative(depth, WorldDepthKey.TieAbs(x, y, slot), s, origin, -300, out uint k), "in range"); return k; }
    keys.Add(("cover", K(0)));
    keys.Add(("item before statics", K(0)));
    keys.Add(("static 0", K(1)));
    keys.Add(("item after 1 static", K(2)));
    keys.Add(("static 1", K(3)));
    keys.Add(("mobile after 2 statics", K(4)));
    keys.Add(("static 6", K(13)));
    keys.Add(("overflow static after 7", K(14)));
    for (int i = 1; i < keys.Count; i++)
        Require(keys[i].Key >= keys[i - 1].Key, $"slot order {keys[i - 1].Name} <= {keys[i].Name}");
    Require(keys[0].Key == keys[1].Key, "cover and a queued object before any static tie, resolved by submission");
    Require(keys[2].Key != keys[3].Key && keys[3].Key != keys[4].Key, "a static never ties a queued object");
    // A tile on the next column with the same depth sorts after every slot here.
    Require(WorldDepthKey.Relative(depth, WorldDepthKey.TieAbs(x + 8, y - 8, 0), s, origin, -300, out uint next) && next > keys[^1].Key, "next chunk column after");
    Require(WorldDepthKey.MaxInstancedPerTile == 7, "seven instanced statics per tile");
}

// The relative key never masks: out of range below, above and across the
// tile field all refuse; the top of each field is accepted.
{
    uint depth = WorldDepthKey.DepthAbs(500f);
    uint origin = WorldDepthKey.DepthOrigin(500);
    Require(WorldDepthKey.Relative(depth, WorldDepthKey.TieAbs(250, 250, 15), 500, origin, -300, out uint k1) && k1 < WorldDepthKey.Count, "in range key");
    Require(!WorldDepthKey.Relative(origin - 1, 0, 500, origin, -300, out _), "below the origin refused");
    Require(!WorldDepthKey.Relative(origin + WorldDepthKey.DepthRange, WorldDepthKey.TieAbs(250, 250, 0), 500, origin, -300, out _), "above the depth range refused");
    Require(WorldDepthKey.Relative(origin + WorldDepthKey.DepthRange - 1, WorldDepthKey.TieAbs(250, 250, 0), 500, origin, -300, out uint top) && top >> WorldDepthKey.TieBits == WorldDepthKey.DepthRange - 1, "top of the depth range accepted");
    // 64 chunk columns from the first visible one: the 64th refuses, the 63rd packs.
    int first = WorldDepthKey.FirstColumn(500, -300);
    int xFar = (first + 64) * 8;
    Require(!WorldDepthKey.Relative(depth, WorldDepthKey.TieAbs(xFar, 500 - xFar, 0), 500, origin, -300, out _), "64th chunk column refused");
    int xEdge = (first + 63) * 8;
    Require(WorldDepthKey.Relative(depth, WorldDepthKey.TieAbs(xEdge, 500 - xEdge, 0), 500, origin, -300, out _), "63rd chunk column packs");
    Require(!WorldDepthKey.Relative(depth, WorldDepthKey.TieAbs((first - 1) * 8 + 7, 0, 0), 500, origin, -300, out _) || first == 0, "column before the first refused");
}

// A frame's plan: unique opaque keys stay unordered; blends and every later
// holder of an equal key go to the ordered pass; a decreasing key refuses.
{
    uint[] keys = { 1, 2, 2, 2, 5, 5, 9 };
    bool[] blended = { false, false, false, false, false, true, true };
    bool[] ordered = new bool[keys.Length];
    Require(WorldDepthOrder.Plan(keys, blended, ordered), "plan accepts non-decreasing keys");
    Require(ordered.SequenceEqual(new[] { false, false, true, true, false, true, true }), "plan: first holder unordered, later holders and blends ordered");
    uint[] bad = { 3, 2 };
    Require(!WorldDepthOrder.Plan(bad, new[] { false, false }, ordered), "plan refuses a decreasing key");
    Require(ordered.Take(2).All(o => !o), "refused plan leaves no flags");

    // With pages known, a tie inside one page buffer keeps its own order; a
    // tie that leaves the page, or follows an ordered draw, is ordered.
    uint[] pk = { 2, 2, 2, 2, 4, 4, 4, 7, 7 };
    bool[] pb = { false, false, false, false, false, true, false, false, false };
    int[] pp = { 0, 0, 1, 0, 3, 3, 3, 5, 5 };
    bool[] po = new bool[pk.Length];
    Require(WorldDepthOrder.Plan(pk, pb, pp, po), "page plan accepts non-decreasing keys");
    Require(po.SequenceEqual(new[] { false, false, true, true, false, true, true, false, false }),
        "page plan: same-page ties unordered until the run leaves the page or blends");
    Require(!WorldDepthOrder.Plan(bad, new[] { false, false }, new[] { 0, 0 }, po), "page plan refuses a decreasing key");
}

Console.WriteLine($"PASS world depth ordering: {checks} checks; CPU only, no renderer parity claim");
