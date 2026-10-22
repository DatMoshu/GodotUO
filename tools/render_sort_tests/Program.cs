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

Console.WriteLine($"[render_sort_tests] OK: {checks} checks, key order == comparison order");

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
