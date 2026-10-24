// SPDX-License-Identifier: BSD-2-Clause
using System;
using System.Collections.Generic;
using System.Reflection;
using GUO.Assets;
using GUO.Game;
using GUO.Game.GameObjects;
using GUO.Game.Map;
using GUO.Game.Scenes;

namespace GUO.Host;

public partial class BatcherProbe
{
    private void VerifyCoverCandidates()
    {
        var world = FirstLandFixture<World>();
        var a = new Chunk(world);
        var b = new Chunk(world);
        var cache = new CoverCandidates();
        object map = new();
        long epoch = 1;
        var key = new CoverCandidates.Key(0, 0, 4, -1);
        var first = FirstLandFixture<Land>();
        var second = FirstLandFixture<Land>();
        first.Z = -2;
        second.Z = -1;
        a.AddGameObject(first, 0, 0);
        b.AddGameObject(second, 0, 0);
        void Check(string label, bool ok) => CheckThat("cover candidates " + label, ok, "ordered identity/lifetime oracle");
        void Observe(Chunk chunk, int x, int y, int column) =>
            cache.Observe(chunk, x, y, chunk.GetFirstLand(x, y), column);
        void Build()
        {
            Check("cold start", !cache.TryGet(map, epoch, key, out _));
            Observe(a, 0, 0, -1);
            Observe(b, 0, 0, 1);
            Observe(a, 0, 0, 3); // repeats must retain occurrence order
            Observe(a, 7, 7, 7); // cached null is a dependency too
            cache.Observe(null, 0, 0, null, 0); // absent map slot
            cache.Complete(epoch);
        }
        try
        {
            Build();
            Check("warm", cache.TryGet(map, epoch, key, out var candidates));
            Check("exact order and duplicates", candidates.Length == 3
                && ReferenceEquals(candidates[0].Land, first) && candidates[0].Column == -1
                && ReferenceEquals(candidates[1].Land, second) && candidates[1].Column == 1
                && ReferenceEquals(candidates[2].Land, first) && candidates[2].Column == 3);
            first.AlphaHue = 0;
            second.Z = 20;
            second.IsStretched = true;
            second.YOffsets.Top = 140;
            Check("live alpha height corners", cache.TryGet(map, epoch, key, out candidates)
                && candidates[0].Land.AlphaHue == 0 && candidates[1].Land.Z == 20
                && candidates[1].Land.YOffsets.Top == 140);
            a.LastAccessTime = b.LastAccessTime = -123;
            cache.TryGet(map, epoch, key, out _);
            Check("lookup residency touched", a.LastAccessTime == Time.Ticks && b.LastAccessTime == Time.Ticks);
            long allocated = GC.GetAllocatedBytesForCurrentThread();
            bool allHit = true;
            for (int i = 0; i < 1000; i++) allHit &= cache.TryGet(map, epoch, key, out _);
            long hitBytes = GC.GetAllocatedBytesForCurrentThread() - allocated;
            Check("1000 warm hits allocate zero", allHit && hitBytes == 0);

            // Compare emitted identity/depth/Seq and counters with the original
            // predicate, including duplicate occurrences and deferred overlap.
            var lists = new RenderLists();
            var queue = (Action<Land, float, int, int>)typeof(RenderLists)
                .GetMethod("QueueCover", BindingFlags.NonPublic | BindingFlags.Instance)
                .CreateDelegate(typeof(Action<Land, float, int, int>), lists);
            var suffix = new[] { (first, 0), (second, 2), (first, 0), (second, 0) };
            bool oldCull = RenderLists.CoverCull;
            try
            {
                first.X = second.X = 10; first.Y = second.Y = 10;
                first.PriorityZ = second.PriorityZ = 0;
                foreach (bool cull in new[] { false, true })
                foreach (byte alpha in new byte[] { 0, 128, 255 })
                foreach (float depth in new[] { -10f, first.CalculateDepthZ(), float.NaN })
                {
                    first.AlphaHue = alpha; second.AlphaHue = 128;
                    lists.Clear(); RenderLists.CoverCull = cull;
                    var seen = new HashSet<Land>();
                    var want = new List<(Land Land, float Depth)>();
                    int queued = 0, overlapping = 0;
                    foreach (var (land, column) in suffix)
                    {
                        if (land != null && land.AlphaHue != 0 && !seen.Contains(land))
                        {
                            float landDepth = land.CalculateDepthZ();
                            if (landDepth > depth)
                            {
                                bool overlaps = Math.Abs(column) * 22 < 22 + 10;
                                queued++;
                                if (overlaps) overlapping++;
                                if (overlaps || !cull) { seen.Add(land); want.Add((land, landDepth)); }
                            }
                        }
                        queue(land, depth, column, 10);
                    }
                    var actual = (System.Collections.IList)typeof(RenderLists)
                        .GetField("_world", BindingFlags.NonPublic | BindingFlags.Instance).GetValue(lists);
                    bool same = actual.Count == want.Count && RenderLists.CoverQueued == queued
                        && RenderLists.CoverOverlapping == overlapping;
                    for (int i = 0; same && i < actual.Count; i++)
                    {
                        object draw = actual[i]; Type type = draw.GetType();
                        same &= ReferenceEquals(type.GetField("Object").GetValue(draw), want[i].Land)
                            && (float)type.GetField("Depth").GetValue(draw) == want[i].Depth
                            && (int)type.GetField("Seq").GetValue(draw) == i;
                    }
                    Check($"live ordered oracle cull={cull} alpha={alpha} depth={depth}", same);
                }
            }
            finally { lists.Clear(); RenderLists.CoverCull = oldCull; }

            first.X = 50; first.Y = 60;
            first.RemoveFromTile();
            Check("direct unlink despite changed XYZ", !cache.TryGet(map, epoch, key, out _));
            a.AddGameObject(first, 0, 0);
            cache.Clear(); Build();
            var added = FirstLandFixture<Land>();
            a.AddGameObject(added, 7, 7);
            Check("cached null insertion invalidates", !cache.TryGet(map, epoch, key, out _));
            cache.Clear(); Build();
            IndexMap invalid = IndexMap.Invalid;
            b.Load(null, ref invalid);
            Check("invalid-index reload invalidates", !cache.TryGet(map, epoch, key, out _));
            cache.Clear(); Build();
            b.IsDestroyed = true;
            Check("destroyed dependency rejected", !cache.TryGet(map, epoch, key, out _));
            b.IsDestroyed = false;
            cache.Clear(); Build();
            b.ClearForReload();
            Check("bulk reload invalidates", !cache.TryGet(map, epoch, key, out _));
            b.AddGameObject(FirstLandFixture<Land>(), 0, 0);
            cache.Clear(); Build();
            Check("absent slot appears/table changes", !cache.TryGet(map, ++epoch, key, out _));
            cache.Clear(); Build();
            Check("different map instance", !cache.TryGet(new object(), epoch, key, out _));
            cache.Clear(); Build();
            Check("height-dependent rows change", !cache.TryGet(map, epoch, key with { Rows = 5 }, out _));
            Check("graphic-dependent width change", !cache.TryGet(map, epoch, key with { Half = 22 }, out _));
            cache.Clear();

            // Keep the original detached anchor behavior; it cannot certify
            // persistent candidates even if its current Land looks usable.
            first.RemoveFromTile();
            Check("detached fixture", ReferenceEquals(a.Tiles[0, 0], first));
            cache.TryGet(map, epoch, key, out _);
            Observe(a, 0, 0, 0);
            cache.Complete(epoch);
            Check("unowned chain is not retained", cache.Count == 0);
            b.AddGameObject(first, 0, 0);
            cache.TryGet(map, epoch, key, out _);
            Observe(a, 0, 0, 0);
            cache.Complete(epoch);
            Check("foreign anchor is not retained", cache.Count == 0);
            a.Tiles[0, 0] = null; // fixture cleanup, no production anchor repair

            cache.TryGet(map, epoch, key, out _);
            Observe(b, 0, 0, 0);
            b.InvalidateFirstLand(0, 0);
            cache.Complete(epoch);
            Check("mutation during discovery rejects publication", cache.Count == 0);
            cache.TryGet(map, epoch, key, out _);
            Observe(b, 0, 0, 0);
            cache.Complete(epoch + 1);
            Check("table changes during discovery reject publication", cache.Count == 0);
            cache.TryGet(map, epoch, key, out _);
            for (int i = 0; i <= CoverCandidates.MaxPlan; i++) Observe(b, 0, 0, i);
            cache.Complete(epoch);
            Check("oversized discovery stays uncached", cache.Count == 0 && cache.RetainedReferences == 0);
            for (int i = 0; i < CoverCandidates.MaxEntries + 2; i++)
            {
                cache.TryGet(map, epoch, key with { X = i }, out _);
                Observe(b, 0, 0, 0);
                cache.Complete(epoch);
            }
            Check("entry and reference bounds", cache.Count <= CoverCandidates.MaxEntries
                && cache.RetainedReferences <= CoverCandidates.MaxReferences);
            Check("capacity preserves earlier warm entries", cache.TryGet(map, epoch, key, out _));
            // The preceding hit intentionally retains the first entry.
            for (int i = 0; i < 120; i++) cache.NextFrame();
            Check("old travel entries expire", cache.Count == 0 && cache.RetainedReferences == 0);
            cache.TryGet(map, epoch, key, out _); Observe(b, 0, 0, 0); cache.Complete(epoch);
            cache.Clear();
            Check("release drops all retained identities", cache.Count == 0 && cache.RetainedReferences == 0);
        }
        catch (Exception ex) { CheckThat("cover candidates unexpected exception", false, ex.ToString()); }
        finally
        {
            cache.Clear();
            a.ClearForReload(); b.ClearForReload();
            a.Mesh.Clear(); b.Mesh.Clear();
        }
    }
}
