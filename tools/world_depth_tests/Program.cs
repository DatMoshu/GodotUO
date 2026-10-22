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
Console.WriteLine($"PASS world depth ordering: {checks} checks; CPU only, no renderer parity claim");
