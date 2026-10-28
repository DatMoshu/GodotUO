// SPDX-License-Identifier: BSD-2-Clause
// The L4 paging core (godot/GUO/src/Labs/Paging: manifest, verifier,
// residency session), compiled on its own and run with dotnet. No engine,
// no client data, no shard. Also the paging doctor's manifest check and a
// synthetic two-page fixture writer.
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
            int w = 64, h = 64, rows = 1, cols = 2, first = 32;
            for (int a = 2; a + 1 < args.Length; a += 2)
            {
                var parts = args[a + 1].Split('x');
                switch (args[a])
                {
                    case "--size": w = int.Parse(parts[0]); h = int.Parse(parts[1]); break;
                    case "--grid": rows = int.Parse(parts[0]); cols = int.Parse(parts[1]); break;
                    case "--first-facet": first = int.Parse(args[a + 1]); break;
                    default: Console.Error.WriteLine("unknown option " + args[a]); return 2;
                }
            }
            string manifest = PageFixture.WriteGrid(args[1], w, h, rows, new Dictionary<int, string>(),
                Enumerable.Range(first, rows * cols).ToArray());
            Console.WriteLine($"[paging] fixture written: {manifest}");
            return Validate(manifest);
        }
        if (args.Length != 0)
        {
            Console.Error.WriteLine("usage: PagingTests [--validate <manifest.json> | --fixture <dir> [--size WxH] [--grid RxC] [--first-facet N]]");
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

        var ok = PageCatalog.Parse(PageFixture.Manifest(PageFixture.Page("first", 32, east: "last"), PageFixture.Page("last", 79, west: "first")), path);
        Check(ok.Pages.Count == 2 && ok.ByFacet(32).Id == "first" && ok.ByFacet(79).Id == "last", "facets 32 and 79 accepted");
        Check(ok.ByFacet(32).Neighbors["east"] == "last", "neighbors kept");
        Check(ok.ByFacet(32).MapPath == Path.Combine(dir, "map32.mul"), "files resolve beside the manifest");

        foreach (int facet in new[] { 0, 5, 31, 80, 126, 127, 255, -1 })
            Refused(PageFixture.Manifest(PageFixture.Page("p", facet)), path, $"facet {facet} refused");

        Refused(PageFixture.Manifest(PageFixture.Page("a", 40), PageFixture.Page("b", 40)), path, "duplicate facet refused");
        Refused(PageFixture.Manifest(PageFixture.Page("a", 40), PageFixture.Page("a", 41)), path, "duplicate page id refused");
        Refused(PageFixture.Manifest(PageFixture.Page("a", 40, width: 60)), path, "width not a multiple of 8 refused");
        Refused(PageFixture.Manifest(PageFixture.Page("a", 40, height: 0)), path, "zero height refused");
        Refused(PageFixture.Manifest(PageFixture.Page("a", 40, width: 0x8008)), path, "width beyond packet range refused");
        Refused(PageFixture.Manifest(PageFixture.Page("a", 40, height: 0x4008)), path, "height beyond packet range refused");
        Refused(PageFixture.Manifest(PageFixture.Page("a", 40, map: "../map40.mul")), path, "file leaving the pack refused");
        Refused(PageFixture.Manifest(PageFixture.Page("a", 40, map: Path.Combine(root, "map40.mul"))), path, "rooted file refused");
        Refused(PageFixture.Manifest(PageFixture.Page("a", 40, sha: "zz")), path, "bad sha refused");
        Refused(PageFixture.Manifest(PageFixture.Page("a", 40, east: "nowhere")), path, "unknown neighbor refused");
        Refused(PageFixture.Manifest(PageFixture.Page("a", 40, east: "a")), path, "self neighbor refused");
        Refused(PageFixture.Manifest(PageFixture.Page("a", 40, version: "")), path, "empty version refused");
        Refused(PageFixture.Manifest(PageFixture.Page("bad id", 40)), path, "bad page id refused");
        Refused("{\"kind\":\"guo-page-manifest\",\"format\":2,\"pages\":[]}", path, "unknown format refused");
        Refused("{\"kind\":\"other\",\"format\":1,\"pages\":[]}", path, "wrong kind refused");
        Refused("{\"kind\":\"guo-page-manifest\",\"format\":1,\"pages\":[]}", path, "empty manifest refused");
        Refused("{not json", path, "invalid JSON refused");
        Refused(PageFixture.Manifest(PageFixture.Page("a", 40)).Replace("\"north\"", "\"up\""), path, "unknown direction refused");
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
        string manifest = PageFixture.WriteTwoPages(dir, 16, 24);
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
        string manifest = PageFixture.WritePages(dir, 16, 16, 32, 33, 34, 35);
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
        string revised = PageFixture.WritePages(dir, 16, 16, new Dictionary<int, string> { [34] = "v2", [32] = "v2" }, 32, 33, 34, 35);
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

        string grid = PageFixture.WriteGrid(Path.Combine(root, "grid"), 16, 16, 2, new Dictionary<int, string>(), 40, 41, 42, 43);
        var g = PageCatalog.Load(grid);
        Check(g.ByFacet(40).Neighbors["east"] == "p41" && g.ByFacet(40).Neighbors["south"] == "p42" &&
              g.ByFacet(43).Neighbors["north"] == "p41" && g.ByFacet(43).Neighbors["west"] == "p42" &&
              !g.ByFacet(40).Neighbors.ContainsKey("north"), "2x2 grid neighbours");
        var loop = new PagingSession(g, new FakeBackend(), cap: 2, run: runner.Run);
        foreach (int f in new[] { 40, 41, 43, 42, 40 }) Check(loop.Enter(f, out _), $"grid loop enters {f}");
        Check(loop.Resident.Count == 2 && loop.Resident.First() == 40 && loop.Resident.Contains(42) && loop.Evictions == 3,
            "a corner loop through four pages keeps two");

        var one = new PagingSession(PageCatalog.Load(manifest), new FakeBackend(), cap: 0, run: runner.Run);
        Check(one.Cap == 1, "cap clamps to at least one page");
        var many = new PagingSession(PageCatalog.Load(manifest), new FakeBackend(), cap: 99, run: runner.Run);
        Check(many.Cap == PagingSession.MaxCap, "cap clamps to the maximum");
        one.Enter(32, out _);
        one.Enter(33, out _);
        Check(one.Resident.Count == 1 && one.Resident.First() == 33, "cap 1 keeps only the current page");
        one.Clear();
        Check(one.Resident.Count == 0 && one.Current == -1, "clear drops every page");

        // World map views: loaded within the cap, pinned while rendering.
        var view = new PagingSession(g, new FakeBackend(), cap: 2, run: runner.Run);
        Check(view.Enter(40, out _) && view.Enter(41, out _) && view.Enter(43, out _), "view: enter 40, 41, 43");
        Check(!view.Resident.Contains(40), "view: 40 evicted before it is viewed");
        Check(view.View(40, out error) && view.Resident.Contains(40) && view.Resident.Contains(43) && view.Resident.Count == 2,
            "view loads an evicted page within the cap, keeping the current one");
        Check(view.IsPinned(40) && view.Current == 43, "a viewed page is pinned and does not become current");
        Check(view.Enter(42, out _) && view.Resident.Contains(40) && view.Resident.Contains(42) && view.Resident.Count == 2,
            "a crossing while the view renders evicts around the pinned page");
        Check(view.Enter(41, out _) && view.Resident.Count == 2 && view.Resident.Contains(40) && !view.Resident.Contains(42),
            "the next crossing evicts the unpinned page, not the viewed one");
        view.Unpin(40);
        Check(view.Enter(43, out _) && !view.Resident.Contains(40), "unpinned, the viewed page is evicted like any other");
        Check(view.View(43, out _) && view.Resident.Count == 2, "viewing the current page loads nothing");
        view.Unpin(43);

        var tight = new PagingSession(g, new FakeBackend(), cap: 1, run: runner.Run);
        Check(tight.Enter(40, out _), "cap 1: enter 40");
        Check(!tight.View(41, out error) && error.Contains("not loaded") && !tight.Resident.Contains(41) && tight.Resident.Count == 1,
            "cap 1 refuses a view of another page and changes nothing");
        Check(tight.View(40, out _), "cap 1 still views the current page");
        Check(tight.Enter(41, out _) && tight.Resident.Count == 2 && tight.Resident.Contains(40),
            "a page pinned by a render may hold the cap over by one");
        tight.Unpin(40);
        tight.Trim();
        Check(tight.Resident.Count == 1 && tight.Resident.First() == 41, "unpinned, the extra page goes at the next trim");

        var bad = g.ByFacet(42);
        File.AppendAllText(bad.MapPath, "x");
        Check(!view.View(42, out error) && error.Contains("map file") && !view.Resident.Contains(42) && view.Resident.Contains(43),
            "a corrupt page is refused for the world map and the current page stays");
        File.AppendAllText(g.ByFacet(43).MapPath, "x");
        Check(view.View(43, out _) && view.Resident.Contains(43), "a view of the current page never re-verifies or drops it");
        view.Unpin(43);
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
