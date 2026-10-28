// SPDX-License-Identifier: BSD-2-Clause
// The L4 paging core (godot/GUO/src/Labs/Paging: manifest, verifier,
// residency session), compiled on its own and run with dotnet. No engine,
// no client data, no shard. Also the paging doctor's manifest check and a
// synthetic two-page fixture writer.
using System.Security.Cryptography;
using System.Text.Json;
using GUO.Labs.Paging;

static class Program
{
    static int _failures;

    static int Main(string[] args)
    {
        if (args.Length == 2 && args[0] == "--validate") return Validate(args[1]);
        if (args.Length >= 2 && args[0] == "--fixture")
        {
            int w = 64, h = 64;
            if (args.Length == 4 && args[2] == "--size")
            {
                var parts = args[3].Split('x');
                w = int.Parse(parts[0]);
                h = int.Parse(parts[1]);
            }
            string manifest = Fixture.WriteTwoPages(args[1], w, h);
            Console.WriteLine($"[paging] fixture written: {manifest}");
            return Validate(manifest);
        }
        if (args.Length != 0)
        {
            Console.Error.WriteLine("usage: PagingTests [--validate <manifest.json> | --fixture <dir> [--size WxH]]");
            return 2;
        }

        string root = Path.Combine(Path.GetTempPath(), "guo-paging-tests-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(root);
        try
        {
            ManifestTests(root);
            VerifierTests(root);
            SessionTests(root);
        }
        finally
        {
            Directory.Delete(root, true);
        }

        Console.WriteLine(_failures == 0 ? "[paging] PASS" : $"[paging] FAIL ({_failures})");
        return _failures == 0 ? 0 : 1;
    }

    static int Validate(string manifest)
    {
        PageCatalog catalog;
        try
        {
            catalog = PageCatalog.Load(manifest);
        }
        catch (PageManifestException e)
        {
            Console.WriteLine($"[paging] manifest refused: {e.Message}");
            return 1;
        }

        int bad = 0;
        foreach (var page in catalog.Pages)
        {
            string problem = PageVerifier.Verify(page);
            Console.WriteLine(problem == null
                ? $"[paging] ok   page {page.Id} facet {page.Facet} {page.Width}x{page.Height} version {page.Version}"
                : $"[paging] FAIL {problem}");
            if (problem != null) bad++;
        }
        Console.WriteLine(bad == 0 ? $"[paging] manifest ok: {catalog.Pages.Count} page(s)" : $"[paging] manifest has {bad} unusable page(s)");
        return bad == 0 ? 0 : 1;
    }

    // --- manifest ----------------------------------------------------------

    static void ManifestTests(string root)
    {
        string dir = Path.Combine(root, "manifest");
        Directory.CreateDirectory(dir);
        string path = Path.Combine(dir, "pages.json");

        var ok = PageCatalog.Parse(Fixture.Manifest(Fixture.Page("first", 32, east: "last"), Fixture.Page("last", 79, west: "first")), path);
        Check(ok.Pages.Count == 2 && ok.ByFacet(32).Id == "first" && ok.ByFacet(79).Id == "last", "facets 32 and 79 accepted");
        Check(ok.ByFacet(32).Neighbors["east"] == "last", "neighbors kept");
        Check(ok.ByFacet(32).MapPath == Path.Combine(dir, "map32.mul"), "files resolve beside the manifest");

        foreach (int facet in new[] { 0, 5, 31, 80, 126, 127, 255, -1 })
            Refused(Fixture.Manifest(Fixture.Page("p", facet)), path, $"facet {facet} refused");

        Refused(Fixture.Manifest(Fixture.Page("a", 40), Fixture.Page("b", 40)), path, "duplicate facet refused");
        Refused(Fixture.Manifest(Fixture.Page("a", 40), Fixture.Page("a", 41)), path, "duplicate page id refused");
        Refused(Fixture.Manifest(Fixture.Page("a", 40, width: 60)), path, "width not a multiple of 8 refused");
        Refused(Fixture.Manifest(Fixture.Page("a", 40, height: 0)), path, "zero height refused");
        Refused(Fixture.Manifest(Fixture.Page("a", 40, width: 0x8008)), path, "width beyond packet range refused");
        Refused(Fixture.Manifest(Fixture.Page("a", 40, height: 0x4008)), path, "height beyond packet range refused");
        Refused(Fixture.Manifest(Fixture.Page("a", 40, map: "../map40.mul")), path, "file leaving the pack refused");
        Refused(Fixture.Manifest(Fixture.Page("a", 40, map: Path.Combine(root, "map40.mul"))), path, "rooted file refused");
        Refused(Fixture.Manifest(Fixture.Page("a", 40, sha: "zz")), path, "bad sha refused");
        Refused(Fixture.Manifest(Fixture.Page("a", 40, east: "nowhere")), path, "unknown neighbor refused");
        Refused(Fixture.Manifest(Fixture.Page("a", 40, east: "a")), path, "self neighbor refused");
        Refused(Fixture.Manifest(Fixture.Page("a", 40, version: "")), path, "empty version refused");
        Refused(Fixture.Manifest(Fixture.Page("bad id", 40)), path, "bad page id refused");
        Refused("{\"kind\":\"guo-page-manifest\",\"format\":2,\"pages\":[]}", path, "unknown format refused");
        Refused("{\"kind\":\"other\",\"format\":1,\"pages\":[]}", path, "wrong kind refused");
        Refused("{\"kind\":\"guo-page-manifest\",\"format\":1,\"pages\":[]}", path, "empty manifest refused");
        Refused("{not json", path, "invalid JSON refused");
        Refused(Fixture.Manifest(Fixture.Page("a", 40)).Replace("\"north\"", "\"up\""), path, "unknown direction refused");
    }

    static void Refused(string json, string path, string name)
    {
        try
        {
            PageCatalog.Parse(json, path);
            Check(false, name);
        }
        catch (PageManifestException)
        {
            Check(true, name);
        }
    }

    // --- verifier ----------------------------------------------------------

    static void VerifierTests(string root)
    {
        string dir = Path.Combine(root, "verify");
        string manifest = Fixture.WriteTwoPages(dir, 16, 24);
        var catalog = PageCatalog.Load(manifest);
        var page = catalog.ByFacet(32);
        Check(PageVerifier.Verify(page) == null, "fixture page verifies");
        Check(PageVerifier.Verify(catalog.ByFacet(33)) == null, "second fixture page verifies");

        Mutate(page.MapPath, b => { b[^1] ^= 1; return b; }, () => Check(PageVerifier.Verify(page)?.Contains("map file hash") == true, "changed map byte refused"));
        Mutate(page.MapPath, b => b[..^1], () => Check(PageVerifier.Verify(page)?.Contains("map file is") == true, "truncated map refused"));
        Mutate(page.StaidxPath, b => b[..^12], () => Check(PageVerifier.Verify(page)?.Contains("staidx file is") == true, "short staidx refused"));
        Mutate(page.StaticsPath, b => b.Append((byte)0).ToArray(), () => Check(PageVerifier.Verify(page)?.Contains("whole number") == true, "ragged statics refused"));
        Mutate(page.StaidxPath, b => { BitConverter.GetBytes(1000u).CopyTo(b, 0); BitConverter.GetBytes(7u).CopyTo(b, 4); return b; },
            () => Check(PageVerifier.Verify(page)?.Contains("outside the statics") == true, "staidx beyond statics refused"));
        File.Move(page.MapPath, page.MapPath + ".moved");
        Check(PageVerifier.Verify(page)?.Contains("missing") == true, "missing map refused");
        File.Move(page.MapPath + ".moved", page.MapPath);
        Check(PageVerifier.Verify(page) == null, "restored page verifies");
        Check(Validate(manifest) == 0, "doctor validation passes on the fixture");
    }

    static void Mutate(string path, Func<byte[], byte[]> change, Action check)
    {
        byte[] original = File.ReadAllBytes(path);
        File.WriteAllBytes(path, change((byte[])original.Clone()));
        check();
        File.WriteAllBytes(path, original);
    }

    // --- residency session -------------------------------------------------

    static void SessionTests(string root)
    {
        string dir = Path.Combine(root, "session");
        string manifest = Fixture.WritePages(dir, 16, 16, 32, 33, 34, 35);
        var catalog = PageCatalog.Load(manifest);
        var backend = new FakeBackend();
        var runner = new DeferredRunner();
        var session = new PagingSession(catalog, backend, cap: 2, run: runner.Run);

        Check(session.Enter(32, out var error) && error == null, "enter 32");
        Check(backend.Log.SequenceEqual(new[] { "load 32" }), "32 loaded once");
        Check(session.Enter(33, out _) && session.Enter(32, out _), "enter 33 then back to 32");
        Check(backend.Log.Count == 2, "re-entering a resident page does not reload it");
        Check(session.Enter(34, out _), "enter 34");
        Check(backend.Log.Last() == "unload 33" && session.Resident.Count == 2, "cap 2 evicts the least recently used page");
        Check(session.Resident.First() == 34 && session.Resident.Contains(32), "current page kept");
        Check(session.Loads == 3 && session.Evictions == 1, "load and eviction counts");

        Check(!session.Enter(40, out error) && error.Contains("not in the page manifest"), "unknown page facet refused");
        Check(!session.Enter(1, out error) && error.Contains("not a page facet"), "stock facet refused by the page session");
        Check(session.Current == 34, "a refused entry keeps the current page");

        // Content versioning: a revised page with the same facet replaces the resident copy.
        string revised = Fixture.WritePages(dir, 16, 16, new Dictionary<int, string> { [34] = "v2", [32] = "v2" }, 32, 33, 34, 35);
        var changed = session.Reload(PageCatalog.Load(revised));
        Check(changed.OrderBy(f => f).SequenceEqual(new[] { 32, 34 }), "reload reports revised resident pages");
        Check(backend.Log.Last() == "unload 32" && !session.Resident.Contains(32), "revised non-current page dropped at once");
        Check(session.Resident.Contains(34) && session.IsStale(34), "revised current page kept until re-entered");
        backend.Log.Clear();
        Check(session.Enter(34, out _) && backend.Log.SequenceEqual(new[] { "unload 34", "load 34" }), "re-entering rebuilds the revised current page");
        Check(!session.IsStale(34), "rebuilt page is current content");

        // A file replaced under a resident page is verified again and refused.
        var page35 = session.Catalog.ByFacet(35);
        Check(session.Enter(35, out _), "enter 35");
        byte[] map = File.ReadAllBytes(page35.MapPath);
        map[10] ^= 0xFF;
        File.WriteAllBytes(page35.MapPath, map);
        File.SetLastWriteTimeUtc(page35.MapPath, DateTime.UtcNow.AddMinutes(1));
        backend.Log.Clear();
        Check(!session.Enter(35, out error) && error.Contains("hash"), "replaced file refused on re-entry");
        Check(backend.Log.SequenceEqual(new[] { "unload 35" }) && !session.Resident.Contains(35), "replaced page no longer resident");

        // Background neighbour verification: kept for its own generation, dropped after a newer entry.
        var fresh = new PagingSession(PageCatalog.Load(manifest), new FakeBackend(), cap: 3, run: runner.Run);
        runner.Pending.Clear();
        Check(fresh.Enter(33, out _), "enter 33 (neighbours 32 and 34)");
        Check(runner.Pending.Count == 2, "two neighbour verifications queued");
        runner.CompleteAll();
        Check(fresh.IsVerified(32) && fresh.IsVerified(34), "neighbours verified in the same generation");

        var late = new PagingSession(PageCatalog.Load(manifest), new FakeBackend(), cap: 3, run: runner.Run);
        runner.Pending.Clear();
        Check(late.Enter(32, out _), "enter 32 (neighbour 33)");
        Check(late.Enter(34, out _), "enter 34 before the neighbour check finishes");
        runner.CompleteAll();
        Check(!late.IsVerified(33) && late.StaleResultsDropped >= 1, "late neighbour result from an older generation dropped");
        Check(late.Resident.Count <= late.Cap, "resident count within the cap");

        var one = new PagingSession(PageCatalog.Load(manifest), new FakeBackend(), cap: 0, run: runner.Run);
        Check(one.Cap == 1, "cap clamps to at least one page");
        var many = new PagingSession(PageCatalog.Load(manifest), new FakeBackend(), cap: 99, run: runner.Run);
        Check(many.Cap == PagingSession.MaxCap, "cap clamps to the maximum");
        one.Enter(32, out _);
        one.Enter(33, out _);
        Check(one.Resident.Count == 1 && one.Resident.First() == 33, "cap 1 keeps only the current page");
        one.Clear();
        Check(one.Resident.Count == 0 && one.Current == -1, "clear drops every page");
    }

    sealed class FakeBackend : IPageBackend
    {
        public readonly List<string> Log = new();
        public void LoadPage(PageEntry page) => Log.Add($"load {page.Facet}");
        public void UnloadPage(int facet) => Log.Add($"unload {facet}");
    }

    sealed class DeferredRunner
    {
        public readonly List<(Func<string> work, TaskCompletionSource<string> done)> Pending = new();

        public Task<string> Run(Func<string> work)
        {
            var done = new TaskCompletionSource<string>(TaskCreationOptions.None);
            Pending.Add((work, done));
            return done.Task;
        }

        public void CompleteAll()
        {
            foreach (var (work, done) in Pending.ToArray()) done.SetResult(work());
            Pending.Clear();
        }
    }

    static void Check(bool condition, string name)
    {
        if (condition) return;
        _failures++;
        Console.WriteLine($"[paging] FAIL {name}");
    }
}

/// <summary>Synthetic page packs: flat land, no statics. Never real map data.</summary>
static class Fixture
{
    const ushort Land = 0x0003; // a grass land tile ID; the pixels come from the user's own install

    internal static string Page(string id, int facet, int width = 64, int height = 64, string version = "v1",
        string map = null, string sha = null, string east = null, string west = null)
    {
        string hash = sha ?? new string('0', 64);
        var neighbors = new Dictionary<string, string> { ["north"] = null, ["east"] = east, ["south"] = null, ["west"] = west };
        return JsonSerializer.Serialize(new Dictionary<string, object>
        {
            ["page"] = id, ["facet"] = facet, ["width"] = width, ["height"] = height, ["version"] = version,
            ["map"] = map ?? $"map{facet}.mul", ["staidx"] = $"staidx{facet}.mul", ["statics"] = $"statics{facet}.mul",
            ["sha256"] = new Dictionary<string, string> { ["map"] = hash, ["staidx"] = hash, ["statics"] = hash },
            ["neighbors"] = neighbors,
        });
    }

    internal static string Manifest(params string[] pages) =>
        "{\"kind\":\"guo-page-manifest\",\"format\":1,\"pages\":[" + string.Join(",", pages) + "]}";

    internal static string WriteTwoPages(string dir, int width, int height)
    {
        return WritePages(dir, width, height, 32, 33);
    }

    internal static string WritePages(string dir, int width, int height, params int[] facets) =>
        WritePages(dir, width, height, new Dictionary<int, string>(), facets);

    /// <summary>A west-to-east row of pages; <paramref name="versions"/> revises chosen pages.</summary>
    internal static string WritePages(string dir, int width, int height, Dictionary<int, string> versions, params int[] facets)
    {
        Directory.CreateDirectory(dir);
        var pages = new List<string>();
        for (int i = 0; i < facets.Length; i++)
        {
            int facet = facets[i];
            string version = versions.TryGetValue(facet, out var v) ? v : "v1";
            // A revised version carries different terrain height, so its hashes change too.
            sbyte z = (sbyte)(version == "v1" ? 0 : 5);
            int blocks = (width >> 3) * (height >> 3);
            var mapBytes = new byte[blocks * 196];
            for (int b = 0; b < blocks; b++)
                for (int c = 0; c < 64; c++)
                {
                    int o = b * 196 + 4 + c * 3;
                    BitConverter.GetBytes(Land).CopyTo(mapBytes, o);
                    mapBytes[o + 2] = (byte)z;
                }
            var idx = new byte[blocks * 12];
            for (int b = 0; b < blocks; b++)
            {
                BitConverter.GetBytes(0xFFFF_FFFFu).CopyTo(idx, b * 12);
                BitConverter.GetBytes(0u).CopyTo(idx, b * 12 + 4);
            }
            var statics = Array.Empty<byte>();
            string suffix = version == "v1" ? "" : "." + version;
            string mapName = $"map{facet}{suffix}.mul", idxName = $"staidx{facet}{suffix}.mul", staName = $"statics{facet}{suffix}.mul";
            File.WriteAllBytes(Path.Combine(dir, mapName), mapBytes);
            File.WriteAllBytes(Path.Combine(dir, idxName), idx);
            File.WriteAllBytes(Path.Combine(dir, staName), statics);

            var neighbors = new Dictionary<string, string>
            {
                ["north"] = null, ["south"] = null,
                ["west"] = i > 0 ? $"p{facets[i - 1]}" : null,
                ["east"] = i + 1 < facets.Length ? $"p{facets[i + 1]}" : null,
            };
            pages.Add(JsonSerializer.Serialize(new Dictionary<string, object>
            {
                ["page"] = $"p{facet}", ["facet"] = facet, ["width"] = width, ["height"] = height, ["version"] = version,
                ["map"] = mapName, ["staidx"] = idxName, ["statics"] = staName,
                ["sha256"] = new Dictionary<string, string>
                {
                    ["map"] = Sha(mapBytes), ["staidx"] = Sha(idx), ["statics"] = Sha(statics),
                },
                ["neighbors"] = neighbors,
            }));
        }
        string name = versions.Count == 0 ? "pages.json" : "pages.revised.json";
        string path = Path.Combine(dir, name);
        File.WriteAllText(path, Manifest(pages.ToArray()));
        return path;
    }

    static string Sha(byte[] bytes) => Convert.ToHexString(SHA256.HashData(bytes)).ToLowerInvariant();
}
