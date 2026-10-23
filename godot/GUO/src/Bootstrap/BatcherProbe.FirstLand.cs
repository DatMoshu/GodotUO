// SPDX-License-Identifier: BSD-2-Clause

namespace GUO.Host;

using System;
using System.Collections.Generic;
using System.Reflection;
using System.Runtime.CompilerServices;
using GUO.Assets;
using GUO.Game;
using GUO.Game.GameObjects;
using GUO.Game.Map;

public partial class BatcherProbe
{
    // Synthetic, main-thread checks. Bypass data-loading constructors rather
    // than reading land/tiledata or changing Client.Game in this no-install probe.
    private static T FirstLandFixture<T>() where T : class =>
        (T)RuntimeHelpers.GetUninitializedObject(typeof(T));

    private sealed class FirstLandMobile : Mobile
    {
        private FirstLandMobile() : base(null) { }
        public override void Destroy()
        {
            RemoveFromTile();
            IsDestroyed = true;
        }
    }

    private void VerifyFirstLandCache()
    {
        // Preserve the existing pool and diagnostic even when this probe is
        // embedded by another harness; no test chunk or fake world escapes.
        var pool = (Queue<Chunk>)typeof(Chunk).GetField("_pool",
            BindingFlags.NonPublic | BindingFlags.Static).GetValue(null);
        Chunk[] savedPool = pool.ToArray();
        long savedScans = Chunk.FirstLandScans;
        long savedLookups = Chunk.FirstLandLookups, savedNodes = Chunk.FirstLandNodes;
        var chunks = new List<Chunk>();
        World world = FirstLandFixture<World>();
        pool.Clear();
        try
        {
            Chunk MakeChunk()
            {
                var chunk = new Chunk(world);
                chunks.Add(chunk);
                return chunk;
            }

            Land MakeLand(sbyte z = 0)
            {
                var land = FirstLandFixture<Land>();
                land.Z = z;
                return land;
            }

            // This is the original LandAt implementation, not the cache's helper.
            Land Oracle(Chunk chunk, int x, int y)
            {
                for (GameObject obj = chunk.GetHeadObject(x, y); obj != null; obj = obj.TNext)
                {
                    if (obj is Land land) return land;
                }
                return null;
            }

            void Expect(string name, Chunk chunk, int x = 0, int y = 0, long scans = 1)
            {
                long before = Chunk.FirstLandScans;
                Land actual = chunk.GetFirstLand(x, y);
                CheckThat("first-land " + name, ReferenceEquals(actual, Oracle(chunk, x, y)),
                    "cached identity equals original head/TNext traversal");
                CheckThat("first-land " + name + " scans", Chunk.FirstLandScans - before == scans,
                    $"expected {scans}, got {Chunk.FirstLandScans - before}");
            }

            Chunk a = MakeChunk();
            Expect("empty miss", a);
            Expect("empty hit", a, scans: 0);
            var mobile = FirstLandFixture<FirstLandMobile>();
            a.AddGameObject(mobile, 0, 0); // Empty insertion branch, non-land anchor.
            Expect("non-land invalidates cached null", a);
            Expect("non-land null hit", a, scans: 0);
            Land upper = MakeLand(3);
            a.AddGameObject(upper, 0, 0);
            Expect("land before non-land anchor", a);
            Expect("unchanged land hit", a, scans: 0);
            CheckThat("first-land direct answer", ReferenceEquals(a.GetFirstLand(0, 0), upper), "upper Land");

            Land lower = MakeLand(-3);
            a.AddGameObject(lower, 0, 0);
            Expect("multiple lands ordered by chain", a);
            CheckThat("first-land lower sorts first", ReferenceEquals(a.GetFirstLand(0, 0), lower), "lower Land");
            a.RemoveGameObject(lower, 0, 0);
            Expect("Chunk.RemoveGameObject invalidates", a);
            lower.Z = 8;
            a.AddGameObject(lower, 0, 0);
            Expect("land inserted after existing land", a);
            lower.RemoveFromTile();
            Expect("standalone unlink invalidates", a);
            lower.Z = 3;
            a.AddGameObject(lower, 0, 0);
            Expect("equal-priority insertion keeps original tie order", a);
            lower.Destroy();
            Expect("destroyed land invalidates", a);

            upper.AlphaHue = 0;
            upper.PriorityZ = 17;
            upper.IsStretched = true;
            upper.YOffsets.Top = 120;
            Expect("live fields do not invalidate identity", a, scans: 0);
            CheckThat("first-land live fields visible", a.GetFirstLand(0, 0).AlphaHue == 0
                && a.GetFirstLand(0, 0).YOffsets.Top == 120, "no alpha/corner snapshot");

            Land unrelated = MakeLand();
            a.AddGameObject(unrelated, 7, 7);
            Expect("other cell starts cold", a, 7, 7);
            Expect("other cell does not invalidate original", a, scans: 0);
            Chunk b = MakeChunk();
            Expect("move destination cached null", b, 3, 4);
            upper.X = 83; upper.Y = 92; upper.Z = -6;
            // Same order as SetInWorldTile: XYZ changes before AddToTile removes
            // old membership. Explicit chunk avoids any global map/file manager.
            upper.AddToTile(b, 3, 4);
            Expect("XYZ-before-move invalidates old cell", a);
            Expect("XYZ-before-move invalidates destination", b, 3, 4);
            Expect("unrelated cell stays warm after move", a, 7, 7, 0);

            // Detaching the original Tiles anchor does NOT fix Tiles upstream.
            // Re-link it into another chunk, then mutate that other chain.
            mobile.RemoveFromTile();
            CheckThat("first-land detached anchor retained", ReferenceEquals(a.Tiles[0, 0], mobile),
                "original anchor semantics unchanged");
            Expect("detached anchor oracle fallback", a);
            Expect("detached anchor cannot be cached", a);
            b.AddGameObject(mobile, 3, 4);
            Expect("old anchor aliases destination chain", a);
            Expect("destination chain warms before alias insertion", b, 3, 4);
            Land aliasInserted = MakeLand(-30);
            a.AddGameObject(aliasInserted, 0, 0);
            Expect("insertion through old alias invalidates actual chain", b, 3, 4);
            Expect("mixed-owner chain never caches", b, 3, 4);
            aliasInserted.RemoveFromTile();
            Expect("alias unlink invalidates neighboring owner", b, 3, 4);
            Expect("homogeneous destination can cache again", b, 3, 4, 0);
            Land below = MakeLand(-20);
            b.AddGameObject(below, 3, 4);
            Expect("alias observes subsequent destination insertion", a);
            below.RemoveFromTile();
            Expect("alias observes subsequent destination unlink", a);
            b.RemoveGameObject(mobile, 3, 4);
            Expect("removed destination anchor invalidates destination", b, 3, 4);

            // A previously warm, non-null entry must invalidate when its
            // anchor migrates. Bulk cleanup through that foreign alias must
            // invalidate the destination without any extra ownership check on hits.
            for (int destroy = 0; destroy < 2; destroy++)
            {
                Chunk source = MakeChunk(), destination = MakeChunk();
                var sourceAnchor = FirstLandFixture<FirstLandMobile>();
                var destinationAnchor = FirstLandFixture<FirstLandMobile>();
                Land sourceLand = MakeLand(-3), destinationLand = MakeLand(-10);
                source.AddGameObject(sourceAnchor, 0, 0); source.AddGameObject(sourceLand, 0, 0);
                destination.AddGameObject(destinationAnchor, 0, 0); destination.AddGameObject(destinationLand, 0, 0);
                Expect("warm source before anchor migration", source);
                Expect("warm destination before anchor migration", destination);
                Expect("source hot before anchor migration", source, scans: 0);
                Expect("destination hot before anchor migration", destination, scans: 0);
                sourceAnchor.X = 83; sourceAnchor.Y = 92;
                sourceAnchor.AddToTile(destination, 0, 0);
                Expect("warm non-null source follows migrated anchor", source);
                Expect("migrated anchor remains uncached", source);
                CheckThat("first-land migrated anchor changes identity", ReferenceEquals(source.GetFirstLand(0, 0), destinationLand), "destination Land, not old source Land");
                Expect("destination invalidated by migrated anchor", destination);
                Expect("destination hot before foreign bulk detach", destination, scans: 0);
                if (destroy == 0) source.ClearForReload(); else source.Destroy();
                Expect("foreign bulk detach invalidates warm destination", destination);
                Expect("foreign bulk detach leaves original unowned anchor", destination);
                sourceLand.Destroy();
            }

            // Exercise the existing-anchor early return in AddGameObject.
            Chunk anchorChunk = MakeChunk();
            Land anchorLand = MakeLand();
            anchorChunk.AddGameObject(anchorLand, 0, 0);
            Expect("land anchor insertion", anchorChunk);
            anchorChunk.AddGameObject(anchorLand, 0, 0);
            Expect("existing-anchor early return invalidates", anchorChunk);
            Expect("existing-anchor can cache again", anchorChunk, scans: 0);

            // Load's exact invalid-index early return is shared with this
            // synthetic entry point; no client/file reader is involved.
            IndexMap invalid = IndexMap.Invalid;
            anchorChunk.Load(null, ref invalid);
            Expect("invalid-index Load invalidates even retained chain", anchorChunk);
            anchorChunk.ClearForReload();
            Expect("ClearForReload drops land identity", anchorChunk);
            Land replacement = MakeLand();
            anchorChunk.AddGameObject(replacement, 0, 0);
            Expect("reload replacement invalidates cached null", anchorChunk);
            CheckThat("first-land reload replacement is new", ReferenceEquals(anchorChunk.GetFirstLand(0, 0), replacement)
                && !ReferenceEquals(replacement, anchorLand), "replacement, not destroyed prior Land");

            // Bulk loops intentionally preserve the Player but must forget its
            // membership before raw TNext/TPrevious resets. No Player methods run.
            var player = FirstLandFixture<PlayerMobile>();
            typeof(World).GetProperty(nameof(World.Player)).SetValue(world, player);
            Chunk preserved = MakeChunk();
            preserved.AddGameObject(player, 2, 2);
            Expect("player cell warms", preserved, 2, 2);
            preserved.ClearForReload();
            CheckThat("first-land reload forgets preserved player", !player.IsDestroyed
                && !player.HasTileMembership(preserved, 2, 2), "player survives without old ownership");
            Expect("preserved-player cell clears", preserved, 2, 2);
            preserved.AddGameObject(player, 2, 2);
            preserved.Destroy();
            CheckThat("first-land Destroy forgets preserved player", !player.IsDestroyed
                && !player.HasTileMembership(preserved, 2, 2), "same invariant in Destroy");
            // Isolate pool-reuse proof from the just-destroyed fixture.
            pool.Clear();
            typeof(World).GetProperty(nameof(World.Player)).SetValue(world, null);

            Chunk pooled = MakeChunk();
            pooled.AddGameObject(MakeLand(), 0, 0);
            Expect("pool fixture warms", pooled);
            pooled.Destroy();
            Expect("Destroy invalidates land", pooled);
            Expect("destroyed chunk null can be cached", pooled, scans: 0);
            Chunk reused = Chunk.Create(world, 90, 91);
            CheckThat("first-land pool reuses exact instance", ReferenceEquals(pooled, reused)
                && reused.X == 90 && reused.Y == 91 && !reused.IsDestroyed, "same Chunk, different block");
            Expect("Create invalidates even warmed pooled null", reused);
            reused.AddGameObject(MakeLand(-4), 0, 0);
            Expect("reused chunk sees new land", reused);
            reused.Clear();
            Expect("Clear delegates invalidation to Destroy", reused);
        }
        catch (Exception ex)
        {
            CheckThat("first-land unexpected exception", false, ex.ToString());
        }
        finally
        {
            typeof(World).GetProperty(nameof(World.Player)).SetValue(world, null);
            foreach (Chunk chunk in chunks)
            {
                // ClearForReload does not enqueue; shared pool is restored below.
                chunk.ClearForReload();
                chunk.Mesh.Clear();
            }
            pool.Clear();
            foreach (Chunk chunk in savedPool) pool.Enqueue(chunk);
            Chunk.FirstLandScans = savedScans;
            Chunk.FirstLandLookups = savedLookups; Chunk.FirstLandNodes = savedNodes;
        }
    }
}
