// SPDX-License-Identifier: BSD-2-Clause
#if GUO_LABS
using System;
using System.Runtime.InteropServices;
using Godot;
using GUO.Labs.GpuRenderer;

namespace GUO.Host;

public partial class BatcherProbe
{
    private void VerifyOrderedResidency()
    {
        OrderedWorldResidency.Quad Quad(float x) => new()
        {
            A = new OrderedWorldDevice.Vertex { PositionUv = new OrderedWorldDevice.Float4(x, 0, 0, 0) },
            D = new OrderedWorldDevice.Vertex { PositionUv = new OrderedWorldDevice.Float4(x + 1, 1, 1, 1) }
        };
        var a = Quad(1); var b = Quad(2); var c = Quad(3); var d = Quad(4);
        var cache = new OrderedWorldResidency(3);
        void Prepare(params OrderedWorldResidency.Quad[] quads) => cache.Prepare(MemoryMarshal.Cast<OrderedWorldResidency.Quad, OrderedWorldDevice.Vertex>(quads.AsSpan()));
        int Apply()
        {
            int cursor = 0, total = 0;
            while (cache.NextDirty(ref cursor, out int start, out int count))
            { total += count; cache.AppliedRange(start, count); }
            cache.AppliedOrder();
            return total;
        }
        Prepare(a,b,a);
        CheckThat("resident preserves repeated ordered occurrences",cache.Count==2 && cache.Order.Length==3 && cache.Order[0]==cache.Order[2],"order/cache mismatch");
        CheckThat("resident initial upload",Apply()==2,"expected two dirty quads");
        Prepare(a,b,a);
        CheckThat("resident unchanged order stays applied",!cache.OrderDirty,"redundant order upload");
        CheckThat("resident unchanged geometry stays applied",Apply()==0,"redundant geometry upload");
        Prepare(b,a,b);
        CheckThat("resident reorder is order-only",cache.OrderDirty && Apply()==0,"reorder rebuilt geometry");
        // Change a field omitted by the hash: full comparison must distinguish it.
        var changed = a; changed.C.Normal.Z = 0.75f;
        Prepare(a,changed,a);
        CheckThat("resident hash collision retains both variants",cache.Count==3 && cache.Order[0]!=cache.Order[1] && Apply()==1,"full-record equality lost state");
        // A new quad precedes a later cache hit. Pinning only as we walk would
        // incorrectly let the eviction cursor overwrite that future hit.
        Prepare(c,a,changed);
        CheckThat("resident pins future hits before eviction",cache.Slots[(int)cache.Order[1]].Equals(a) && cache.Slots[(int)cache.Order[2]].Equals(changed) && cache.Evictions==1,"live slot evicted");
        Apply();
        cache.InvalidateGeometry();
        CheckThat("resident growth invalidates all geometry",!cache.OrderDirty && Apply()==3,"new storage missing retained geometry");
        cache.InvalidateOrder();
        CheckThat("resident independent order allocation invalidation",cache.OrderDirty && Apply()==0,"new order allocation incorrectly trusted");
        cache.InvalidateGpu();
        Prepare(c,a,changed);
        CheckThat("resident failed upload retries identical IDs",cache.OrderDirty && Apply()==3,"failed transaction considered applied");
        bool exhausted = false;
        try { Prepare(a,b,c,d); } catch (InvalidOperationException) { exhausted = true; }
        CheckThat("resident cap refuses overwriting current ticket",exhausted,"over-capacity frame accepted");
        Prepare(d,a);
        CheckThat("resident bounded cache recovers after capacity refusal",cache.Count==3 && cache.Slots[(int)cache.Order[0]].Equals(d) && cache.Slots[(int)cache.Order[1]].Equals(a),"cache did not recover");

        cache = new OrderedWorldResidency(3);
        void Records(ulong[] ids, params OrderedWorldResidency.Quad[] quads) =>
            cache.Prepare(MemoryMarshal.Cast<OrderedWorldResidency.Quad, OrderedWorldDevice.Vertex>(quads.AsSpan()), ids);
        Records(new ulong[] { 1, 2, 1 }, a,b,a);
        CheckThat("source records preserve repeated occurrences", cache.Count == 2 && cache.Order[0] == cache.Order[2] && Apply() == 2, "two immutable IDs");
        long comparisons = cache.ContentComparisons;
        Records(new ulong[] { 1, 2, 1 }, a,b,a);
        CheckThat("unchanged source records skip vertex comparison and upload",
            cache.ContentComparisons == comparisons && !cache.OrderDirty && Apply() == 0, "ID-only validation");
        Records(new ulong[] { 2, 1, 2 }, b,a,b);
        CheckThat("source reorder skips vertex hashing and upload",
            cache.ContentComparisons == comparisons && cache.OrderDirty && Apply() == 0, "order changes alone");
        Records(new ulong[] { 1, 3, 2 }, a,changed,b);
        CheckThat("source revision keeps old and new occurrences distinct", cache.Order[0] != cache.Order[1] && Apply() == 1, "new immutable ID for changed packet");
        Records(new ulong[] { 4, 1, 3 }, c,a,changed);
        CheckThat("source eviction pins later records", cache.Slots[(int)cache.Order[1]].Equals(a) && cache.Slots[(int)cache.Order[2]].Equals(changed) && cache.Evictions == 1, "all hits pinned first");
        Apply();
        cache.InvalidateGpu();
        Records(new ulong[] { 4, 1, 3 }, c,a,changed);
        CheckThat("source IDs retry invalidated buffers", cache.OrderDirty && Apply() == 3, "resize and failed upload retry");
        Records(new ulong[] { 0, 1, 0 }, d,a,d);
        CheckThat("dynamic content coexists with source IDs", cache.Order[0] == cache.Order[2] && cache.Slots[(int)cache.Order[1]].Equals(a), "dynamic fallback and source record");
    }
}
#endif
