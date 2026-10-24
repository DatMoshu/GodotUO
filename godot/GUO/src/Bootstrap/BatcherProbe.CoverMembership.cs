// SPDX-License-Identifier: BSD-2-Clause
using System;
using System.Collections.Generic;
using GUO.Assets;
using GUO.Game;
using GUO.Game.GameObjects;
using GUO.Game.Map;
using GUO.Game.Scenes;

namespace GUO.Host;

public partial class BatcherProbe
{
    private void VerifyCoverMembershipStability()
    {
        var world = FirstLandFixture<World>();
        var chunks = new List<Chunk>();
        var caches = new List<CoverCandidates>();
        long scans = Chunk.FirstLandScans, lookups = Chunk.FirstLandLookups, nodes = Chunk.FirstLandNodes;
        long hits = CoverCandidates.Hits, misses = CoverCandidates.Misses, builds = CoverCandidates.Builds,
            rejected = CoverCandidates.Rejected, checks = CoverCandidates.DependencyChecks;
        object map = new();
        var key = new CoverCandidates.Key(0, 0, 4, -1);
        void Check(string name, bool ok) => CheckThat("cover membership " + name, ok,
            "raw linked-list identity and certification oracle");
        Chunk MakeChunk()
        {
            var chunk = new Chunk(world);
            chunks.Add(chunk);
            return chunk;
        }
        Land MakeLand(sbyte z = -5)
        {
            var land = FirstLandFixture<Land>();
            land.Z = z;
            return land;
        }
        FirstLandMobile MakeMobile(sbyte z = 10)
        {
            var mobile = FirstLandFixture<FirstLandMobile>();
            mobile.Z = z;
            return mobile;
        }
        Land Oracle(Chunk chunk, int x)
        {
            for (GameObject obj = chunk.GetHeadObject(x, 0); obj != null; obj = obj.TNext)
                if (obj is Land land) return land;
            return null;
        }
        CoverCandidates Build(Chunk chunk)
        {
            var cache = new CoverCandidates();
            caches.Add(cache);
            cache.TryGet(map, 1, key, out _, 1);
            // Include null and duplicate occurrences, not only a single Land.
            foreach (int x in new[] { 0, 1, 2, 0 })
                cache.Observe(chunk, x, 0, chunk.GetFirstLand(x, 0), x);
            cache.Complete(1, 1);
            Check("fixture admitted", cache.Count == 1);
            return cache;
        }
        void Hit(string name, CoverCandidates cache, Chunk chunk)
        {
            bool hit = cache.TryGet(map, 1, key, out var found, 1);
            int at = 0;
            bool same = hit;
            foreach (int x in new[] { 0, 1, 2, 0 })
            {
                Land live = Oracle(chunk, x);
                if (live == null) continue;
                same &= at < found.Length && ReferenceEquals(found[at].Land, live) && found[at].Column == x;
                at++;
            }
            Check(name, same && at == found.Length);
        }
        try
        {
            var chunk = MakeChunk();
            Land anchor = MakeLand();
            chunk.AddGameObject(anchor, 0, 0); // Land IS a legal legacy anchor.
            chunk.AddGameObject(MakeLand(), 2, 0);
            var cache = Build(chunk);
            long version = chunk.CoverMembershipVersion;
            var mobile = MakeMobile();
            chunk.AddGameObject(mobile, 0, 0);
            Hit("non-anchor mobile insertion retains ordered plan", cache, chunk);
            Check("non-Land insertion preserves version", version == chunk.CoverMembershipVersion);
            // XYZ is deliberately changed BEFORE unlink, matching SetInWorldTile.
            mobile.X = 82; mobile.Y = 88; mobile.Z = -10;
            mobile.AddToTile(chunk, 2, 0);
            Hit("non-anchor XYZ-before-move retains both cell identities", cache, chunk);
            mobile.RemoveFromTile();
            Hit("non-anchor direct unlink retains ordered plan", cache, chunk);
            Check("multiple mutations preserve version", version == chunk.CoverMembershipVersion);
            chunk.InvalidateFirstLand(0, 0);
            long beforeScans = Chunk.FirstLandScans;
            long allocated = GC.GetAllocatedBytesForCurrentThread();
            long validated = chunk.CoverMembershipVersion;
            long validatedAgain = chunk.CoverMembershipVersion;
            long bytes = GC.GetAllocatedBytesForCurrentThread() - allocated;
            Check("pending validation scans once and allocates zero", validated == version
                && validatedAgain == version && bytes == 0 && Chunk.FirstLandScans == beforeScans + 1);

            // Reading GetFirstLand before dependency validation must not erase
            // the pending comparison or accidentally bless an old plan.
            var lower = MakeLand(-20);
            chunk.AddGameObject(lower, 0, 0);
            Check("first-Land read sees replacement", ReferenceEquals(chunk.GetFirstLand(0, 0), lower));
            Check("replacement invalidates despite earlier cache read", !cache.TryGet(map, 1, key, out _, 1));
            cache = Build(chunk);
            var upper = MakeLand(20);
            chunk.AddGameObject(upper, 0, 0);
            Hit("non-first Land insertion retains identity", cache, chunk);
            chunk.RemoveGameObject(upper, 0, 0);
            Hit("non-first Land removal retains identity", cache, chunk);
            lower.RemoveFromTile();
            Check("first Land unlink rejects old plan", !cache.TryGet(map, 1, key, out _, 1));

            cache = Build(chunk);
            var emptyAnchor = MakeMobile();
            chunk.AddGameObject(emptyAnchor, 1, 0);
            Hit("certified null remains null after non-Land insertion", cache, chunk);
            emptyAnchor.RemoveFromTile();
            Check("same null but detached anchor loses certification", !cache.TryGet(map, 1, key, out _, 1));
            chunk.RemoveGameObject(emptyAnchor, 1, 0); // exercise production anchor-removing API

            var landAnchorChunk = MakeChunk();
            Land landAnchor = MakeLand();
            landAnchorChunk.AddGameObject(landAnchor, 0, 0);
            var landAnchorCache = Build(landAnchorChunk);
            landAnchor.RemoveFromTile();
            Check("Land anchor same raw identity still rejects after detach",
                ReferenceEquals(Oracle(landAnchorChunk, 0), landAnchor)
                && !landAnchorCache.TryGet(map, 1, key, out _, 1));
            // Same-chunk existing-anchor return restores membership; old plan
            // cannot be revived after its certification was already lost.
            landAnchorChunk.AddGameObject(landAnchor, 0, 0);
            landAnchorCache = Build(landAnchorChunk);
            landAnchorChunk.AddGameObject(landAnchor, 0, 0);
            Hit("same Land anchor detach/relink resolves to certified identity", landAnchorCache, landAnchorChunk);

            // Repeated invalidation while pending must preserve the OLD pointer.
            var pending = MakeLand(-40);
            landAnchorChunk.AddGameObject(pending, 0, 0);
            landAnchorChunk.InvalidateFirstLand(0, 0);
            landAnchorChunk.InvalidateFirstLand(0, 0);
            Check("repeated pending invalidations cannot erase changed identity",
                !landAnchorCache.TryGet(map, 1, key, out _, 1));

            // A benign mutation during discovery is safe; a changed identity
            // during discovery is covered by the existing fixture's new action.
            var benign = new CoverCandidates();
            caches.Add(benign);
            benign.TryGet(map, 1, key, out _, 1);
            benign.Observe(landAnchorChunk, 0, 0, landAnchorChunk.GetFirstLand(0, 0), 0);
            var neighbor = MakeMobile();
            landAnchorChunk.AddGameObject(neighbor, 0, 0);
            benign.Complete(1, 1);
            Check("unchanged identity during discovery may publish", benign.Count == 1
                && benign.TryGet(map, 1, key, out var found, 1) && found.Length == 1
                && ReferenceEquals(found[0].Land, Oracle(landAnchorChunk, 0)));

            anchor.AlphaHue = 0; anchor.Z = 31; anchor.IsStretched = true; anchor.YOffsets.Top = 124;
            cache = Build(chunk);
            Hit("in-place visual mutation remains live", cache, chunk);
            IndexMap invalid = IndexMap.Invalid;
            chunk.Load(null, ref invalid);
            Check("full invalidation remains unconditional", !cache.TryGet(map, 1, key, out _, 1));
        }
        catch (Exception ex) { Check("unexpected exception " + ex, false); }
        finally
        {
            foreach (var cache in caches) cache.Clear();
            foreach (var chunk in chunks) { chunk.ClearForReload(); chunk.Mesh.Clear(); }
            Chunk.FirstLandScans = scans; Chunk.FirstLandLookups = lookups; Chunk.FirstLandNodes = nodes;
            CoverCandidates.Hits = hits; CoverCandidates.Misses = misses; CoverCandidates.Builds = builds;
            CoverCandidates.Rejected = rejected; CoverCandidates.DependencyChecks = checks;
        }
    }
}
