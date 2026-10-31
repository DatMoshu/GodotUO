using System.Reflection;
using Candidate = GUO.Renderer.PixelPicker;
using Frozen = GUO.Renderer.Frozen.PixelPicker;

// Differential contract, not an idealized pixel oracle: preserve the frozen
// implementation's zero/run-boundary and extraRange quirks verbatim.
internal static class PixelPickerTests
{
    internal static void Run()
    {
        var actual = new Candidate();
        var frozen = new Frozen();
        var random = new Random(17153);
        long comparisons = 0;
        int masks = 0;
        int[] ranges = { -2, -1, 0, 1, 2, 3 };

        void Compare(ulong id, int x, int y, int range)
        {
            bool expected = frozen.Get(id, x, y, range);
            bool found = actual.Get(id, x, y, range);
            comparisons++;
            Require(found == expected, $"id={id} x={x} y={y} range={range}: {found} != {expected}");
        }

        // Empty dictionary and absent IDs, including extreme coordinates.
        foreach (int x in new[] { int.MinValue, -1, 0, 1, int.MaxValue })
        foreach (int y in new[] { int.MinValue, -1, 0, 1, int.MaxValue })
        foreach (int range in ranges) Compare(ulong.MaxValue, x, y, range);

        (int Width, int Height)[] dimensions = {
            (0, 0), (1, 1), (2, 2), (7, 5), (127, 2), (128, 2), (129, 3),
            (1, 127), (1, 128), (1, 129), (16383, 1), (16384, 1), (1, 16384)
        };
        ulong id = 0;
        foreach (var (width, height) in dimensions)
        for (int pattern = 0; pattern < 5; pattern++)
        {
            uint[] pixels = new uint[width * height];
            for (int i = 0; i < pixels.Length; i++)
                pixels[i] = pattern switch {
                    0 => 0u,
                    1 => 0xFFFFFFFFu,
                    2 => (i & 1) == 0 ? 0u : 1u,
                    3 => i % 257 < 128 ? 0u : 0xFF000000u,
                    _ => random.Next(3) == 0 ? 0u : (uint)random.Next(1, int.MaxValue)
                };
            actual.Set(id, width, height, pixels);
            frozen.Set(id, width, height, pixels);
            masks++;
            actual.GetDimensions(id, out int aw, out int ah);
            frozen.GetDimensions(id, out int fw, out int fh);
            Require(aw == fw && ah == fh && aw == width && ah == height, "dimension decoding");

            // Exhaust every local coordinate for small masks; sample axes,
            // varint boundaries and deterministic points for larger masks.
            int[] xs = Axis(width, width * height <= 256);
            int[] ys = Axis(height, width * height <= 256);
            foreach (int x in xs)
            foreach (int y in ys)
            foreach (int range in ranges) Compare(id, x, y, range);
            for (int i = 0; i < 64; i++)
                Compare(id, random.Next(-2, width + 3), random.Next(-2, height + 3), ranges[i % ranges.Length]);

            // Set intentionally keeps an existing ID. Do not modernize it to
            // replace the mask; compare after attempts with different dimensions.
            actual.Set(id, 3, 1, new uint[] { 1, 0, 1 });
            frozen.Set(id, 3, 1, new uint[] { 1, 0, 1 });
            actual.GetDimensions(id, out aw, out ah);
            Require(aw == width && ah == height, "repeated Set replaced dimensions");
            foreach (int x in xs)
            foreach (int y in ys) Compare(id, x, y, 0);
            // A populated dictionary still rejects IDs it has never seen.
            foreach (int range in ranges) Compare(ulong.MaxValue, width / 2, height / 2, range);
            id++;
        }
        // Revisit earlier masks after subsequent inserts, including ID zero.
        for (ulong old = 0; old < id; old++)
        foreach (int range in ranges) Compare(old, 0, 0, range);
        actual.GetDimensions(ulong.MaxValue, out int missingWidth, out int missingHeight);
        Require(missingWidth == 0 && missingHeight == 0, "missing dimensions");

        VerifyLookupAvoidance();
        Console.WriteLine($"[pixel_picker_tests] PASS: {comparisons} differential requests, {masks} masks, frozen3560bf1f; negative lookups avoided; no timing claim");
    }

    private static int[] Axis(int size, bool exhaustive)
    {
        var values = new HashSet<int> { int.MinValue, -2, -1, 0, 1, size / 2, size - 1, size, size + 1, int.MaxValue };
        if (exhaustive) for (int i = 0; i < size; i++) values.Add(i);
        foreach (int boundary in new[] { 126, 127, 128, 129, 16382, 16383, 16384 }) values.Add(boundary);
        return values.OrderBy(x => x).ToArray();
    }

    private static void VerifyLookupAvoidance()
    {
        var actual = new Candidate();
        var frozen = new Frozen();
        actual.Set(7, 2, 2, new uint[] { 1, 0, 1, 1 });
        frozen.Set(7, 2, 2, new uint[] { 1, 0, 1, 1 });
        var actualCounter = AttachCounter(actual);
        var frozenCounter = AttachCounter(frozen);
        int requests = 0;
        foreach (ulong id in new ulong[] { 7, 8 })
        foreach (var (x, y) in new[] { (-1, 0), (0, -1), (-1, -1), (int.MinValue, int.MaxValue) })
        foreach (int range in new[] { -1, 0, 2 })
        {
            Require(!actual.Get(id, x, y, range) && !frozen.Get(id, x, y, range), "negative selection");
            requests++;
        }
        Require(actualCounter.HashCalls == 0, "candidate consulted dictionary for a negative coordinate");
        Require(frozenCounter.HashCalls == requests, "frozen lookup witness did not exercise each request");
        actualCounter.HashCalls = frozenCounter.HashCalls = 0;
        Require(actual.Get(7, 1, 1) == frozen.Get(7, 1, 1), "nonnegative lookup changed");
        Require(actualCounter.HashCalls == 1 && frozenCounter.HashCalls == 1, "nonnegative lookup witness");
        Console.WriteLine($"[pixel_picker_tests] lookup witness: {requests} negative requests, frozen {requests}, candidate 0; nonnegative 1/1");
    }

    // Test-only observation of dictionary operations. Production has no counter,
    // reflection, replacement dictionary or instrumentation.
    private static CountingComparer AttachCounter(object picker)
    {
        var field = picker.GetType().GetField("m_IDs", BindingFlags.Instance | BindingFlags.NonPublic)!;
        var original = (Dictionary<ulong, int>)field.GetValue(picker)!;
        var counter = new CountingComparer();
        field.SetValue(picker, new Dictionary<ulong, int>(original, counter));
        counter.HashCalls = 0;
        return counter;
    }

    private sealed class CountingComparer : IEqualityComparer<ulong>
    {
        internal int HashCalls;
        public bool Equals(ulong x, ulong y) => x == y;
        public int GetHashCode(ulong value) { HashCalls++; return value.GetHashCode(); }
    }

    private static void Require(bool ok, string message)
    {
        if (!ok) throw new Exception("[pixel_picker_tests] FAIL: " + message);
    }
}
