// The sorted world pass's key sort (DepthKey) against the comparison sort it
// replaced: same order on every input, and how much faster. Run:
//   dotnet run --project tools/render_sort_tests            (proof only, exit 1 on a mismatch)
//   dotnet run --project tools/render_sort_tests -- --bench (proof, then timings)
using System.Diagnostics;
using GUO.Game.Scenes;

bool bench = args.Contains("--bench");
int checks = 0;
void Check(bool condition, string message) { checks++; if (!condition) throw new Exception("FAIL: " + message); }

// Mirrors RenderLists.Drawable: the depth and the queue index, sorted together.
var rng = new Random(20261022);

float Depth(int shape, int i) => shape switch
{
    0 => (float)(rng.NextDouble() * 2000 - 1000),                 // spread, both signs
    1 => (float)Math.Round(rng.NextDouble() * 40 - 20),           // many ties (a pile on one tile)
    2 => rng.Next(4) == 0 ? -0f : rng.Next(4) == 0 ? 0f : i * 0.5f, // signed zeros among a ramp
    3 => (float)(i * 1e-3 + rng.NextDouble() * 1e-6),             // nearly sorted (a quiet frame)
    _ => (float)(rng.NextDouble() < 0.5 ? -rng.NextDouble() * 1e30 : rng.NextDouble() * 1e-30),
};

int[] ByComparison((float Depth, int Seq)[] items)
{
    var list = new List<(float Depth, int Seq)>(items);
    list.Sort(static (a, b) => DepthKey.Compare(a.Depth, a.Seq, b.Depth, b.Seq));
    return list.Select(d => d.Seq).ToArray();
}

int[] ByKey((float Depth, int Seq)[] items)
{
    var keys = new ulong[items.Length];
    for (int i = 0; i < items.Length; i++) keys[i] = DepthKey.Of(items[i].Depth, items[i].Seq);
    Array.Sort(keys);
    return keys.Select(DepthKey.Seq).ToArray();
}

// Proof: every shape, several sizes, the two orders are identical.
foreach (int shape in Enumerable.Range(0, 5))
{
    foreach (int n in new[] { 0, 1, 2, 7, 100, 5000, 40000 })
    {
        var items = Enumerable.Range(0, n).Select(i => (Depth: Depth(shape, i), Seq: i)).ToArray();
        int[] a = ByComparison(items), b = ByKey(items);
        Check(a.SequenceEqual(b), $"shape {shape}, n {n}: key order differs from comparison order");
        for (int i = 1; i < b.Length; i++)
        {
            Check(DepthKey.Compare(items[b[i - 1]].Depth, b[i - 1], items[b[i]].Depth, b[i]) < 0, $"shape {shape}, n {n}: key order is not strictly ascending at {i}");
        }
    }
}

// Signed zero folds into one key; the sequence still breaks the tie.
Check(DepthKey.Of(-0f, 3) == DepthKey.Of(0f, 3), "-0 and +0 must share a key");
Check(DepthKey.Of(-0f, 3) < DepthKey.Of(0f, 4), "sequence breaks the tie between signed zeros");
Check(DepthKey.Of(-1f, int.MaxValue) < DepthKey.Of(0f, 0), "a negative depth sorts before zero whatever its sequence");
Check(DepthKey.Of(float.MinValue, 0) < DepthKey.Of(-1f, 0), "float.MinValue sorts first");
Check(DepthKey.Of(1f, 0) < DepthKey.Of(float.MaxValue, 0), "float.MaxValue sorts last among finite values");
Check(DepthKey.Seq(DepthKey.Of(12.5f, 123456)) == 123456, "the sequence survives the round trip");

// The queue as RenderLists builds it: a mobile is a shadow at d then a body
// at d + 0.5, a meshed static one entry with its layer, a covering land one
// entry at the land's depth, all with Seq == the index they were added at.
// Whatever the mix, the key order is the comparison order, and the key's
// low half finds the entry it was made from.
{
    var queue = new List<(float Depth, int Seq, string Kind)>();
    void Enqueue(float depth, string kind) => queue.Add((depth, queue.Count, kind));
    for (int i = 0; i < 3000; i++)
    {
        float d = (float)(rng.NextDouble() * 400 - 200);
        switch (rng.Next(5))
        {
            case 0: Enqueue(d, "shadow"); Enqueue(d + 0.5f, "body"); break;
            case 1: Enqueue(d, "mesh"); break;
            case 2: Enqueue(d, "static"); for (int c = 0; c < rng.Next(4); c++) Enqueue(d + rng.Next(3), "cover"); break;
            default: Enqueue((float)Math.Round(d), "static"); break;
        }
    }
    var items = queue.Select(q => (q.Depth, q.Seq)).ToArray();
    int[] byComparison = ByComparison(items), byKey = ByKey(items);
    Check(byComparison.SequenceEqual(byKey), "mixed queue: key order differs from comparison order");
    for (int i = 0; i < queue.Count; i++) Check(queue[i].Seq == i, "Seq is not the index the entry was queued at");
    Check(byKey.All(seq => queue[seq].Seq == seq), "a sorted key does not find the entry it was made from");
    Check(byKey.Zip(byKey.Skip(1)).All(p => !(queue[p.First].Kind == "body" && queue[p.Second].Kind == "shadow" && queue[p.Second].Seq == queue[p.First].Seq - 1)),
        "a body was sorted before its own shadow");
}

// CoverStamp: the per-frame "already queued" mark on a land tile.
{
    long fresh = 0;
    var owner = new CoverStamp();
    Check(!owner.Marked(fresh), "a fresh tile reads as marked before any frame");
    Check(owner.Current == 0, "an owner has a stamp before its first frame");
    owner.NextFrame();
    Check(!owner.Marked(fresh), "a fresh tile reads as marked in the first frame");
    long tile = 0;
    owner.Mark(ref tile);
    Check(owner.Marked(tile), "a marked tile does not read as marked");
    owner.Mark(ref tile);
    Check(owner.Marked(tile), "marking twice in a frame (a duplicate cover candidate) is not idempotent");
    owner.NextFrame();
    Check(!owner.Marked(tile), "last frame's mark survived into this frame");
    Check(!owner.Marked(fresh), "a fresh tile reads as marked after a frame");

    // A second owner over the same tiles (a scene recreated, a probe beside
    // the game) never sees the first one's marks, in any interleaving.
    var other = new CoverStamp();
    other.NextFrame();
    owner.Mark(ref tile);
    Check(owner.Marked(tile) && !other.Marked(tile), "the other owner saw this owner's mark");
    other.Mark(ref tile);
    Check(other.Marked(tile) && !owner.Marked(tile), "this owner saw the other owner's mark");
    var late = new CoverStamp();
    late.NextFrame();
    Check(!late.Marked(tile), "a new owner's first frame read an older owner's mark (stamps are not process-wide)");
    for (int f = 0; f < 100000; f++) owner.NextFrame();
    Check(!owner.Marked(tile) && !owner.Marked(fresh) && owner.Current > late.Current, "stamps are not strictly increasing across owners and frames");
}

// ChunkCursor: one lookup per chunk a walk passes through, none for negative tiles.
{
    var source = new CountingChunks();
    var cursor = new ChunkCursor<string>();
    for (int x = 0; x < 16; x++) Check(cursor.At(x, 5, source) == $"{x >> 3},0", $"wrong chunk for tile {x}");
    Check(source.Lookups == 2, $"a row of 16 tiles took {source.Lookups} lookups, not 2");
    for (int y = 0; y < 16; y++) cursor.At(3, y, source);
    Check(source.Lookups == 4, $"a column across an edge took {source.Lookups - 2} more lookups, not 2");
    Check(cursor.At(-1, 3, source) == null && cursor.At(3, -1, source) == null && source.Lookups == 4, "a negative tile cost a lookup or returned a chunk");
    source.Unloaded.Add("5,5");
    for (int x = 40; x < 48; x++) Check(cursor.At(x, 40, source) == null, "an unloaded chunk returned a chunk");
    Check(source.Lookups == 5, $"an unloaded chunk was asked {source.Lookups - 4} times over 8 tiles, not once");
    Check(cursor.At(0, 0, source) == "0,0" && cursor.At(40, 40, source) == null && source.Lookups == 7, "leaving and returning did not look up again");
    Check(new ChunkCursor<string>().At(8, 8, source) == "1,1", "a fresh cursor did not look up");
    Check(new ChunkCursor<string>().At(0, 0, new CountingChunks { Unloaded = { "0,0" } }) == null, "a fresh cursor on an unloaded chunk returned a chunk");
}

Console.WriteLine($"[render_sort_tests] OK: {checks} checks: key order == comparison order, mixed queue, cover stamp, chunk cursor");

if (!bench) return 0;

// Timings: a dense frame (statics + cover land) is 10k-30k drawables.
foreach (int n in new[] { 2000, 10000, 30000 })
{
    var items = Enumerable.Range(0, n).Select(i => (Depth: Depth(0, i), Seq: i)).ToArray();
    const int rounds = 200;
    var sw = new Stopwatch();

    var list = new List<(float Depth, int Seq)>(n);
    Comparison<(float Depth, int Seq)> cmp = static (a, b) => DepthKey.Compare(a.Depth, a.Seq, b.Depth, b.Seq);
    for (int r = 0; r < 5; r++) { list.Clear(); list.AddRange(items); list.Sort(cmp); }
    sw.Restart();
    for (int r = 0; r < rounds; r++) { list.Clear(); list.AddRange(items); list.Sort(cmp); }
    double comparison = sw.Elapsed.TotalMilliseconds / rounds;

    var keys = new ulong[n];
    for (int r = 0; r < 5; r++) { for (int i = 0; i < n; i++) keys[i] = DepthKey.Of(items[i].Depth, i); Array.Sort(keys); }
    sw.Restart();
    for (int r = 0; r < rounds; r++) { for (int i = 0; i < n; i++) keys[i] = DepthKey.Of(items[i].Depth, i); Array.Sort(keys); }
    double key = sw.Elapsed.TotalMilliseconds / rounds;

    Console.WriteLine($"[render_sort_tests] n={n,6}: List.Sort(comparison) {comparison,7:F3} ms, key sort {key,7:F3} ms, x{comparison / key:F1}");
}
return 0;

sealed class CountingChunks : IChunkSource<string>
{
    public int Lookups;
    public HashSet<string> Unloaded = new();
    public string ChunkAt(int chunkX, int chunkY)
    {
        Lookups++;
        string key = $"{chunkX},{chunkY}";
        return Unloaded.Contains(key) ? null : key;
    }
}
