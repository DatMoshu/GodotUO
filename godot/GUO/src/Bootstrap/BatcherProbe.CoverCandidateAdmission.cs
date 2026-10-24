// SPDX-License-Identifier: BSD-2-Clause
using System;
using System.Collections;
using System.Collections.Generic;
using System.Reflection;
using GUO.Assets;
using GUO.Game;
using GUO.Game.GameObjects;
using GUO.Game.Map;
using GUO.Game.Scenes;
using GameMap = GUO.Game.Map.Map;

namespace GUO.Host;

public partial class BatcherProbe
{
    private void VerifyCoverCandidateAdmission()
    {
        var pool = (Queue<Chunk>)typeof(Chunk).GetField("_pool", BindingFlags.NonPublic | BindingFlags.Static).GetValue(null);
        Chunk[] savedPool = pool.ToArray();
        long scans = Chunk.FirstLandScans, lookups = Chunk.FirstLandLookups, nodes = Chunk.FirstLandNodes;
        long hits = CoverCandidates.Hits, misses = CoverCandidates.Misses, builds = CoverCandidates.Builds,
            rejected = CoverCandidates.Rejected, checks = CoverCandidates.DependencyChecks;
        var chunks = new List<Chunk>();
        var caches = new List<CoverCandidates>();
        var world = FirstLandFixture<World>();
        var key = new CoverCandidates.Key(0, 0, 4, -1);
        object map = new();
        Chunk MakeChunk()
        {
            var chunk = new Chunk(world);
            chunks.Add(chunk);
            return chunk;
        }
        CoverCandidates MakeCache()
        {
            var cache = new CoverCandidates();
            caches.Add(cache);
            return cache;
        }
        Land MakeLand(sbyte z = -5)
        {
            var land = FirstLandFixture<Land>();
            land.Z = z;
            return land;
        }
        FirstLandMobile MakeMobile(sbyte z)
        {
            var mobile = FirstLandFixture<FirstLandMobile>();
            mobile.Z = z;
            return mobile;
        }
        void Observe(CoverCandidates cache, Chunk chunk, int column = 0) =>
            cache.Observe(chunk, 0, 0, chunk.GetFirstLand(0, 0), column);
        void Check(string label, bool ok) => CheckThat("cover admission " + label, ok, "identity, epoch and admission regression");
        void Build(CoverCandidates cache, CoverCandidates.Key k, Chunk chunk, long epoch = 1, long identity = 1, bool missing = false)
        {
            Check("build starts cold", !cache.TryGet(map, epoch, k, out _, identity));
            if (chunk != null) Observe(cache, chunk);
            if (missing) cache.Observe(null, 0, 0, null, 0);
            cache.Complete(epoch, identity);
        }
        pool.Clear();
        try
        {
            VerifyCoverCandidateVerifier(world);
            var resident = MakeChunk();
            Land land = MakeLand();
            resident.AddGameObject(land, 0, 0);
            var missingKey = key with { X = 1 };
            foreach (bool missingFirst in new[] { false, true })
            {
                var cache = MakeCache();
                Build(cache, key, resident);
                Build(cache, missingKey, resident, missing: true);
                Check("two plans exact references", cache.Count == 2 && cache.RetainedReferences == 4);
                bool residentHit, missingHit;
                if (missingFirst)
                {
                    missingHit = cache.TryGet(map, 2, missingKey, out _, 1);
                    residentHit = cache.TryGet(map, 2, key, out _, 1);
                }
                else
                {
                    residentHit = cache.TryGet(map, 2, key, out _, 1);
                    missingHit = cache.TryGet(map, 2, missingKey, out _, 1);
                }
                Check($"slot churn query order missing-first={missingFirst}", residentHit && !missingHit
                    && cache.Count == 1 && cache.RetainedReferences == 2);
                Build(cache, missingKey, resident, 2, missing: true);
                Check("missing plan rebuild uses new slot epoch", cache.TryGet(map, 2, missingKey, out var found, 1)
                    && found.Length == 1 && ReferenceEquals(found[0].Land, land));
                Check("later slot churn still preserves resident", cache.TryGet(map, 3, key, out _, 1)
                    && !cache.TryGet(map, 3, missingKey, out _, 1));
                Build(cache, missingKey, resident, 3, missing: true);
                Check("hard lifetime fixture contains both entry kinds", cache.Count == 2 && cache.RetainedReferences == 4);
                Check("hard lifetime drops all resident references", !cache.TryGet(map, 3, key, out _, 2)
                    && cache.Count == 0 && cache.RetainedReferences == 0);
            }

            var empty = MakeChunk();
            var distinction = MakeCache();
            Build(distinction, key, empty);
            Build(distinction, missingKey, null, missing: true);
            Check("empty chunk has dependency, missing-only plan has none",
                distinction.Count == 2 && distinction.RetainedReferences == 1);
            Check("non-null empty survives slot churn", distinction.TryGet(map, 2, key, out var noLand, 1) && noLand.Length == 0);
            Check("missing-only plan invalidates", !distinction.TryGet(map, 2, missingKey, out _, 1));
            empty.AddGameObject(MakeLand(), 0, 0);
            Check("land inserted into empty dependency invalidates without slot churn", !distinction.TryGet(map, 2, key, out _, 1)
                && distinction.Count == 0 && distinction.RetainedReferences == 0);

            foreach (bool missing in new[] { false, true })
            foreach (bool hard in new[] { false, true })
            {
                var cache = MakeCache();
                cache.TryGet(map, 10, key, out _, 20);
                Observe(cache, resident);
                if (missing) cache.Observe(null, 0, 0, null, 0);
                long before = CoverCandidates.Rejected;
                cache.Complete(hard ? 10 : 11, hard ? 21 : 20);
                Check($"mixed discovery rejects missing={missing} hard={hard}", cache.Count == 0
                    && cache.RetainedReferences == 0 && CoverCandidates.Rejected == before + 1);
                Build(cache, key, resident, 11, 21);
                Check("fresh discovery after rejection works", cache.TryGet(map, 11, key, out _, 21));
            }

            // Fill with genuinely empty plans. Neither scratch list has ever
            // held a reference, so the FIRST rejected observation detects lazy
            // allocation as well as steady-state retained-reference leaks.
            var full = MakeCache();
            bool allCold = true;
            long buildsBefore = CoverCandidates.Builds;
            for (int i = 0; i < CoverCandidates.MaxEntries; i++)
            {
                allCold &= !full.TryGet(map, 1, key with { X = i }, out _, 1);
                full.Complete(1, 1);
            }
            Check("empty plans fill exact entry bound", allCold && full.Count == CoverCandidates.MaxEntries
                && full.RetainedReferences == 0 && CoverCandidates.Builds == buildsBefore + CoverCandidates.MaxEntries);
            long allocated = GC.GetAllocatedBytesForCurrentThread();
            bool warm = true;
            for (int i = 0; i < 1000; i++) warm &= full.TryGet(map, 1, key, out _, 1);
            long bytes = GC.GetAllocatedBytesForCurrentThread() - allocated;
            Check("full warm hits allocate zero", warm && bytes == 0);
            var fresh = key with { X = CoverCandidates.MaxEntries + 1 };
            long rejectedBefore = CoverCandidates.Rejected, dependenciesBefore = CoverCandidates.DependencyChecks;
            buildsBefore = CoverCandidates.Builds;
            Land liveLand = resident.GetFirstLand(0, 0);
            allocated = GC.GetAllocatedBytesForCurrentThread();
            bool cold = !full.TryGet(map, 1, fresh, out _, 1);
            full.Observe(resident, 0, 0, liveLand, 0);
            full.Observe(null, 0, 0, null, 1);
            full.Complete(1, 1);
            bytes = GC.GetAllocatedBytesForCurrentThread() - allocated;
            Check("first full miss does not allocate or retain", cold && bytes == 0
                && full.Count == CoverCandidates.MaxEntries && full.RetainedReferences == 0
                && CoverCandidates.Rejected == rejectedBefore + 1 && CoverCandidates.Builds == buildsBefore
                && CoverCandidates.DependencyChecks == dependenciesBefore);
            foreach (string fieldName in new[] { "_building", "_dependencies" })
            {
                object scratch = typeof(CoverCandidates).GetField(fieldName, BindingFlags.NonPublic | BindingFlags.Instance).GetValue(full);
                Check("full miss never grows " + fieldName, ((IList)scratch).Count == 0
                    && (int)scratch.GetType().GetProperty("Capacity").GetValue(scratch) == 0);
            }
            for (int i = 0; i < 120; i++) full.NextFrame();
            Check("expiration frees exact entry/reference totals", full.Count == 0 && full.RetainedReferences == 0);
            Build(full, fresh, resident, missing: true);
            Check("expiration re-admits previously rejected key", full.Count == 1 && full.RetainedReferences == 2
                && full.TryGet(map, 1, fresh, out var admitted, 1) && admitted.Length == 1);
            for (int i = 0; i < 120; i++) full.NextFrame();
            Check("expiration releases positive reference totals", full.Count == 0 && full.RetainedReferences == 0);

            // One stale resident must be removed BEFORE the full-cache guard.
            var stale = MakeCache();
            Build(stale, key, resident);
            for (int i = 1; i < CoverCandidates.MaxEntries; i++)
            {
                stale.TryGet(map, 1, key with { X = i }, out _, 1);
                stale.Complete(1, 1);
            }
            IndexMap invalid = IndexMap.Invalid;
            resident.Load(null, ref invalid);
            rejectedBefore = CoverCandidates.Rejected;
            Check("stale key removal frees admission slot while full", !stale.TryGet(map, 1, key, out _, 1)
                && stale.Count == CoverCandidates.MaxEntries - 1 && stale.RetainedReferences == 0);
            Observe(stale, resident);
            stale.Complete(1, 1);
            Check("stale key rebuild restores exact totals", stale.Count == CoverCandidates.MaxEntries
                && stale.RetainedReferences == 2 && CoverCandidates.Rejected == rejectedBefore
                && stale.TryGet(map, 1, key, out _, 1));

            var refs = MakeCache();
            int planLands = CoverCandidates.MaxPlan - 1; // plus one dependency
            int plans = CoverCandidates.MaxReferences / CoverCandidates.MaxPlan;
            for (int i = 0; i < plans; i++)
            {
                refs.TryGet(map, 1, key with { X = i }, out _, 1);
                for (int j = 0; j < planLands; j++) Observe(refs, resident, j);
                refs.Complete(1, 1);
            }
            Check("reference bound reached exactly", refs.Count == plans && refs.RetainedReferences == CoverCandidates.MaxReferences);
            rejectedBefore = CoverCandidates.Rejected;
            Build(refs, key with { X = plans }, resident);
            Check("reference overflow rejects exactly once", refs.Count == plans
                && refs.RetainedReferences == CoverCandidates.MaxReferences && CoverCandidates.Rejected == rejectedBefore + 1);
            Build(refs, key with { X = plans + 1 }, null);
            Check("zero-reference plan still admitted at reference limit", refs.Count == plans + 1
                && refs.RetainedReferences == CoverCandidates.MaxReferences);
            refs.Clear();
            Check("clear releases exact reference accounting", refs.Count == 0 && refs.RetainedReferences == 0);

            var partial = MakeCache();
            partial.TryGet(map, 1, key, out _, 1);
            for (int i = 0; i < CoverCandidates.MaxPlan; i++) Observe(partial, resident, i);
            rejectedBefore = CoverCandidates.Rejected;
            partial.Complete(1, 1);
            Check("oversized partial plan rejects exactly once", partial.Count == 0 && partial.RetainedReferences == 0
                && CoverCandidates.Rejected == rejectedBefore + 1);
            Build(partial, key, resident);
            Check("partial rejection clears scratch references and flags", partial.TryGet(map, 2, key, out var clean, 1)
                && clean.Length == 1 && partial.RetainedReferences == 2);

            var pooled = MakeChunk();
            pooled.AddGameObject(MakeLand(), 0, 0);
            var aba = MakeCache();
            Build(aba, key, pooled);
            long version = pooled.CoverMembershipVersion;
            pooled.Destroy();
            long destroyedVersion = pooled.CoverMembershipVersion;
            Chunk reused = Chunk.Create(world, 90, 91);
            Check("pool fixture reuses exact live object", ReferenceEquals(pooled, reused) && !reused.IsDestroyed
                && reused.X == 90 && reused.Y == 91 && destroyedVersion > version
                && reused.CoverMembershipVersion > destroyedVersion);
            Check("pool ABA misses before new Land is inserted", !aba.TryGet(map, 1, key, out _, 1)
                && aba.Count == 0 && aba.RetainedReferences == 0);

            // M remains Tiles' anchor after unlink. N separates it from Land,
            // so the mutation touches only non-Land owners/neighbors.
            var source = MakeChunk();
            var anchor = MakeMobile(10);
            var between = MakeMobile(5);
            Land sourceLand = MakeLand();
            source.AddGameObject(anchor, 0, 0);
            source.AddGameObject(sourceLand, 0, 0);
            source.AddGameObject(between, 0, 0);
            var detach = MakeCache();
            Build(detach, key, source);
            Check("non-Land detach starts with warm Land", detach.TryGet(map, 1, key, out var prior, 1)
                && prior.Length == 1 && ReferenceEquals(prior[0].Land, sourceLand)
                && ReferenceEquals(anchor.TPrevious, between));
            anchor.RemoveFromTile();
            Check("non-Land anchor detach changes live answer and invalidates warm cover",
                ReferenceEquals(source.Tiles[0, 0], anchor) && source.GetFirstLand(0, 0) == null
                && !detach.TryGet(map, 1, key, out _, 1));
            var destination = MakeChunk();
            var destinationAnchor = MakeMobile(10);
            Land destinationLand = MakeLand(-10);
            destination.AddGameObject(destinationAnchor, 0, 0);
            destination.AddGameObject(destinationLand, 0, 0);
            destination.AddGameObject(anchor, 0, 0);
            var foreign = MakeCache();
            Build(foreign, key, destination);
            Check("destination warm before foreign non-Land insertion", foreign.TryGet(map, 1, key, out _, 1));
            source.AddGameObject(MakeMobile(0), 0, 0); // follows migrated anchor
            Check("foreign non-Land loses certification and invalidates warm destination",
                ReferenceEquals(destination.GetFirstLand(0, 0), destinationLand) && !destination.HasCertifiedFirstLand(0, 0)
                && !foreign.TryGet(map, 1, key, out _, 1));
            Observe(foreign, destination);
            foreign.Complete(1, 1);
            Check("foreign discovery remains unretained", foreign.Count == 0 && foreign.RetainedReferences == 0);

            foreach (bool destroy in new[] { false, true })
            {
                var aliasSource = MakeChunk();
                var aliasDestination = MakeChunk();
                var moving = MakeMobile(10);
                aliasSource.AddGameObject(moving, 0, 0);
                aliasSource.AddGameObject(MakeLand(), 0, 0);
                aliasDestination.AddGameObject(MakeMobile(10), 0, 0);
                aliasDestination.AddGameObject(MakeLand(-10), 0, 0);
                var oldSource = MakeCache();
                Build(oldSource, key, aliasSource);
                moving.X = 80; moving.Y = 88;
                moving.AddToTile(aliasDestination, 0, 0);
                Check("non-Land migration invalidates warm source despite changed XYZ", !oldSource.TryGet(map, 1, key, out _, 1));
                var oldDestination = MakeCache();
                Build(oldDestination, key, aliasDestination);
                Check("destination warm before foreign bulk detach", oldDestination.TryGet(map, 1, key, out _, 1));
                if (destroy) aliasSource.Destroy(); else aliasSource.ClearForReload();
                Check($"foreign bulk detach invalidates warm destination destroy={destroy}",
                    !oldDestination.TryGet(map, 1, key, out _, 1));
            }
            VerifyCoverCandidateMapReplacement(world);
        }
        catch (Exception ex) { CheckThat("cover admission unexpected exception", false, ex.ToString()); }
        finally
        {
            try
            {
                foreach (var cache in caches) cache.Clear();
                foreach (var chunk in chunks) { chunk.ClearForReload(); chunk.Mesh.Clear(); }
            }
            finally
            {
                pool.Clear();
                foreach (var chunk in savedPool) pool.Enqueue(chunk);
                Chunk.FirstLandScans = scans; Chunk.FirstLandLookups = lookups; Chunk.FirstLandNodes = nodes;
                CoverCandidates.Hits = hits; CoverCandidates.Misses = misses; CoverCandidates.Builds = builds;
                CoverCandidates.Rejected = rejected; CoverCandidates.DependencyChecks = checks;
            }
        }
    }

    private sealed class CoverVerifierChunkSource(Chunk chunk) : IChunkSource<Chunk>
    {
        public Chunk ChunkAt(int x, int y) => x == 0 && y == 0 ? chunk : null;
    }

    private void VerifyCoverCandidateVerifier(World world)
    {
        long plans = CoverCandidateVerifier.Plans, references = CoverCandidateVerifier.References,
            mismatches = CoverCandidateVerifier.Mismatches;
        long scans = Chunk.FirstLandScans, lookups = Chunk.FirstLandLookups, nodes = Chunk.FirstLandNodes;
        int queued = RenderLists.CoverQueued, overlapping = RenderLists.CoverOverlapping;
        var chunk = new Chunk(world);
        var source = new CoverVerifierChunkSource(chunk);
        var a = FirstLandFixture<Land>();
        var b = FirstLandFixture<Land>();
        var head = FirstLandFixture<FirstLandMobile>();
        head.Z = -20;
        a.Z = b.Z = -5;
        void Check(string name, bool ok) => CheckThat("cover verifier " + name, ok, "raw linked-list identity/order oracle");
        void MustReject(string name, CoverCandidates.Candidate[] candidates, int visited)
        {
            long beforePlans = CoverCandidateVerifier.Plans, beforeRefs = CoverCandidateVerifier.References,
                beforeMismatch = CoverCandidateVerifier.Mismatches;
            bool threw = false;
            try { CoverCandidateVerifier.Check(source, 0, 0, 1, 1, 1, -1, candidates); }
            catch (InvalidOperationException ex) when (ex.Message == "Retained covering-land candidates differ from the live linked-list traversal.")
            {
                threw = true;
            }
            Check(name, threw && CoverCandidateVerifier.Plans == beforePlans + 1
                && CoverCandidateVerifier.References == beforeRefs + visited
                && CoverCandidateVerifier.Mismatches == beforeMismatch + 1);
        }
        try
        {
            chunk.AddGameObject(head, 1, 0);
            chunk.AddGameObject(a, 1, 0);
            chunk.AddGameObject(b, 0, 1);
            // Deliberate alias: the live walk must preserve duplicate Land
            // occurrences even when certification would prohibit retention.
            chunk.Tiles[1, 1] = a;
            a.AlphaHue = 0; b.AlphaHue = 128;
            a.CoverStamp = 123; b.CoverStamp = 456;
            var expected = new[]
            {
                new CoverCandidates.Candidate(a, 1),
                new CoverCandidates.Candidate(b, -1),
                new CoverCandidates.Candidate(a, 0),
            };
            Check("fixture includes non-Land head", ReferenceEquals(chunk.GetHeadObject(1, 0), head));
            CoverCandidateVerifier.Check(source, 0, 0, 1, 1, 1, -1, expected);
            Check("matching identities, columns and duplicate order pass", CoverCandidateVerifier.Plans == plans + 1
                && CoverCandidateVerifier.References == references + 3 && CoverCandidateVerifier.Mismatches == mismatches);
            MustReject("missing all candidates throws", Array.Empty<CoverCandidates.Candidate>(), 1);
            MustReject("missing trailing duplicate throws", new[] { expected[0], expected[1] }, 3);
            MustReject("missing middle candidate throws", new[] { expected[0], expected[2] }, 2);
            MustReject("wrong order throws", new[] { expected[1], expected[0], expected[2] }, 1);
            MustReject("wrong identity with correct column throws", new[]
                { new CoverCandidates.Candidate(b, 1), expected[1], expected[2] }, 1);
            MustReject("wrong column with correct identity throws", new[]
                { new CoverCandidates.Candidate(a, 0), expected[1], expected[2] }, 1);
            MustReject("extra cached occurrence throws", new[] { expected[0], expected[1], expected[2], expected[2] }, 3);
            // Retry after failures so a poisoned cursor/index cannot hide behind
            // expected exception assertions. No queue, stamps or identity cache.
            long before = CoverCandidateVerifier.Mismatches;
            CoverCandidateVerifier.Check(source, 0, 0, 1, 1, 1, -1, expected);
            Check("matching retry after failures passes", CoverCandidateVerifier.Mismatches == before);
            Check("does not consult first-Land cache or queue cover", Chunk.FirstLandScans == scans
                && Chunk.FirstLandLookups == lookups && Chunk.FirstLandNodes == nodes
                && RenderLists.CoverQueued == queued && RenderLists.CoverOverlapping == overlapping
                && a.CoverStamp == 123 && b.CoverStamp == 456);
        }
        finally
        {
            try
            {
                chunk.Tiles[1, 1] = null; // remove only the synthetic duplicate alias
                chunk.ClearForReload();
                chunk.Mesh.Clear();
            }
            finally
            {
                CoverCandidateVerifier.Plans = plans; CoverCandidateVerifier.References = references;
                CoverCandidateVerifier.Mismatches = mismatches;
                Chunk.FirstLandScans = scans; Chunk.FirstLandLookups = lookups; Chunk.FirstLandNodes = nodes;
            }
        }
    }

    private void VerifyCoverCandidateMapReplacement(World world)
    {
        // Use the REAL Map constructor with tiny synthetic dimensions. The
        // controller is never added to a tree, loaded, or connected to a shard.
        // Refuse to borrow a live Client.Game; this probe normally runs standalone.
        if (Client.Game != null)
        {
            CheckThat("cover auxiliary Map fixture requires standalone probe", false,
                "Client.Game is already owned; refusing to replace a live/embedded controller");
            return;
        }
        const BindingFlags statics = BindingFlags.NonPublic | BindingFlags.Static;
        var tableField = typeof(GameMap).GetField("_terrainChunks", statics);
        var accessField = typeof(GameMap).GetField("_blockAccessList", statics);
        var slotEpoch = typeof(GameMap).GetProperty(nameof(GameMap.CoverTableVersion), statics);
        var hardEpoch = typeof(GameMap).GetProperty(nameof(GameMap.CoverTableIdentityVersion), statics);
        object savedTable = tableField.GetValue(null);
        var access = (bool[])accessField.GetValue(null);
        bool[] savedAccess = (bool[])access.Clone();
        object savedSlot = slotEpoch.GetValue(null), savedHard = hardEpoch.GetValue(null);
        var cache = new CoverCandidates();
        var chunk = new Chunk(world);
        using var controller = new GameController(embedded: true);
        try
        {
            var files = FirstLandFixture<UOFileManager>();
            var maps = FirstLandFixture<MapLoader>();
            typeof(MapLoader).GetProperty(nameof(MapLoader.MapBlocksSize)).SetValue(maps, new int[,] { { 1, 1 }, { 2, 1 } });
            files.Maps = maps;
            typeof(UltimaOnline).GetProperty(nameof(UltimaOnline.FileManager)).SetValue(controller.UO, files);
            Client.AttachEmbedded(controller);
            tableField.SetValue(null, null);
            var original = new GameMap(world, 0);
            var originalTable = (Chunk[])tableField.GetValue(null);
            originalTable[0] = chunk;
            Land land = FirstLandFixture<Land>();
            chunk.AddGameObject(land, 0, 0);
            var key = new CoverCandidates.Key(0, 0, 1, -1);
            long slot = GameMap.CoverTableVersion, hard = GameMap.CoverTableIdentityVersion;
            cache.TryGet(original, slot, key, out _, hard);
            cache.Observe(chunk, 0, 0, chunk.GetFirstLand(0, 0), 0);
            cache.Complete(slot, hard);
            bool warm = cache.TryGet(original, slot, key, out var candidates, hard)
                && candidates.Length == 1 && ReferenceEquals(candidates[0].Land, land);
            long membership = chunk.CoverMembershipVersion;
            var auxiliary = new GameMap(world, 1); // same operation as WorldMapGump.ChangeMap
            CheckThat("cover auxiliary constructor really replaces shared table", warm && auxiliary.BlocksCount == 2
                && !ReferenceEquals(originalTable, tableField.GetValue(null)) && original.GetChunk(0) == null
                && !chunk.IsDestroyed && chunk.CoverMembershipVersion == membership,
                "original Map and dependency stay alive while table identity changes");
            CheckThat("cover real auxiliary constructor invalidates resident cover",
                !cache.TryGet(original, GameMap.CoverTableVersion, key, out _, GameMap.CoverTableIdentityVersion)
                && cache.Count == 0 && cache.RetainedReferences == 0, "hard epoch rejects stranded non-null dependency");
            // Reattach a synthetic loaded slot so subsequent lifetime checks
            // start from a dependency reachable by the real current Map again.
            ((Chunk[])tableField.GetValue(null))[0] = chunk;
            cache.Observe(chunk, 0, 0, chunk.GetFirstLand(0, 0), 0);
            cache.Complete(GameMap.CoverTableVersion, GameMap.CoverTableIdentityVersion);
            CheckThat("cover auxiliary replacement fixture rebuilds before lifetime checks",
                cache.TryGet(original, GameMap.CoverTableVersion, key, out _, GameMap.CoverTableIdentityVersion),
                "avoid a vacuous lifetime miss");
            object enlargedTable = tableField.GetValue(null);
            _ = new GameMap(world, 1);
            CheckThat("cover same-capacity real constructor is a hard boundary",
                ReferenceEquals(enlargedTable, tableField.GetValue(null))
                && !cache.TryGet(original, GameMap.CoverTableVersion, key, out _, GameMap.CoverTableIdentityVersion),
                "construction invalidates even when the array is reused");
            cache.Observe(chunk, 0, 0, chunk.GetFirstLand(0, 0), 0);
            cache.Complete(GameMap.CoverTableVersion, GameMap.CoverTableIdentityVersion);
            CheckThat("cover Map Destroy fixture starts warm",
                cache.TryGet(original, GameMap.CoverTableVersion, key, out _, GameMap.CoverTableIdentityVersion),
                "avoid a vacuous Destroy miss");
            auxiliary.Destroy();
            CheckThat("cover real Map Destroy invalidates resident cover",
                !cache.TryGet(original, GameMap.CoverTableVersion, key, out _, GameMap.CoverTableIdentityVersion),
                "Destroy is a hard lifetime boundary even with no used slots");
        }
        finally
        {
            try { cache.Clear(); chunk.ClearForReload(); chunk.Mesh.Clear(); }
            finally
            {
                Client.DetachEmbedded(controller);
                tableField.SetValue(null, savedTable);
                Array.Copy(savedAccess, access, access.Length);
                slotEpoch.SetValue(null, savedSlot);
                hardEpoch.SetValue(null, savedHard);
            }
        }
    }
}
