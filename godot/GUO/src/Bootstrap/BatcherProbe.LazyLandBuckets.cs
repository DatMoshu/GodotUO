// SPDX-License-Identifier: BSD-2-Clause
using System;
using System.Collections.Generic;
using System.Reflection;
using GUO.Game.GameObjects;
using GUO.Game.Scenes;
using GUO.Renderer;

namespace GUO.Host;

public partial class BatcherProbe
{
    private void VerifyLazyLandBuckets(UltimaBatcher2D batcher)
    {
        const BindingFlags fields = BindingFlags.Instance | BindingFlags.NonPublic;
        var plain = typeof(RenderLists).GetField("_tiles", fields);
        var stretched = typeof(RenderLists).GetField("_stretchedTiles", fields);
        List<GameObject> Plain(RenderLists list) => (List<GameObject>)plain.GetValue(list);
        List<GameObject> Stretched(RenderLists list) => (List<GameObject>)stretched.GetValue(list);
        void Check(string name, bool ok) => CheckThat("lazy land buckets " + name, ok,
            "per-owner lazy allocation, Clear and empty-draw behavior");
        int objectId = batcher.CurrentObjectId;
        int queued = RenderLists.CoverQueued, overlapping = RenderLists.CoverOverlapping;
        var lastCover = RenderLists.LastCover;
        var parent = new RenderLists();
        var child = new RenderLists();
        var plainLand = FirstLandFixture<Land>();
        var stretchedLand = FirstLandFixture<Land>();
        stretchedLand.IsStretched = true;
        try
        {
            Check("fresh parent and child stay lazy", Plain(parent) == null && Stretched(parent) == null
                && Plain(child) == null && Stretched(child) == null);
            parent.AddGumpNoAtlas(static _ => true);
            child.AddGumpNoAtlas(static _ => false);
            Check("UI queue construction does not allocate land buckets", Plain(parent) == null
                && Stretched(parent) == null && Plain(child) == null && Stretched(child) == null);
            child.Clear();
            child.Clear();
            Check("repeated Clear preserves laziness", Plain(child) == null && Stretched(child) == null);

            // Null buckets must retain the old empty-list id-buffer reset.
            var draw = (Func<UltimaBatcher2D, List<GameObject>, sbyte, int>)typeof(RenderLists)
                .GetMethod("DrawRenderList", BindingFlags.Static | BindingFlags.NonPublic)
                .CreateDelegate(typeof(Func<UltimaBatcher2D, List<GameObject>, sbyte, int>));
            batcher.CurrentObjectId = 123;
            int count = draw(batcher, null, sbyte.MaxValue);
            Check("null bucket returns zero and resets object id", count == 0 && batcher.CurrentObjectId == 0);

            child.Add(plainLand);
            Check("first plain Land allocates only plain bucket", Plain(child)?.Count == 1
                && ReferenceEquals(Plain(child)[0], plainLand) && Stretched(child) == null);
            child.Add(stretchedLand);
            Check("first stretched Land allocates separate bucket", Stretched(child)?.Count == 1
                && ReferenceEquals(Stretched(child)[0], stretchedLand));
            var firstPlain = Plain(child);
            var firstStretched = Stretched(child);
            Check("nested owner remains independent", Plain(parent) == null && Stretched(parent) == null);
            child.Clear();
            Check("Clear drops logical references and retains bucket identity", firstPlain.Count == 0
                && firstStretched.Count == 0 && ReferenceEquals(firstPlain, Plain(child))
                && ReferenceEquals(firstStretched, Stretched(child)));
            child.Add(plainLand);
            child.Add(stretchedLand);
            Check("requeue reuses both buckets", ReferenceEquals(firstPlain, Plain(child))
                && ReferenceEquals(firstStretched, Stretched(child)) && firstPlain.Count == 1 && firstStretched.Count == 1);
            batcher.CurrentObjectId = 456;
            var empty = new List<GameObject>();
            Check("allocated empty bucket retains same reset", draw(batcher, empty, sbyte.MaxValue) == 0
                && batcher.CurrentObjectId == 0);
        }
        finally
        {
            parent.Clear(); child.Clear();
            batcher.CurrentObjectId = objectId;
            RenderLists.CoverQueued = queued; RenderLists.CoverOverlapping = overlapping;
            RenderLists.LastCover = lastCover;
        }
    }
}
