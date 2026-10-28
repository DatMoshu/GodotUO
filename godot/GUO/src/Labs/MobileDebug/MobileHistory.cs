// SPDX-License-Identifier: BSD-2-Clause
#if GUO_LABS
using System;
using System.Collections.Generic;

namespace GUO.Labs.MobileDebug;

internal sealed record LayerSample(int Layer, uint Item, int Animation, int Action,
    int Direction, int Frame, int Count, string Result);
internal sealed record PacketSample(uint Tick, int X, int Y, int Z, int Direction);
internal sealed record MobileSample(ulong ProcessFrame, uint Tick, uint Serial,
    int X, int Y, int Z, float OffsetX, float OffsetY, float OffsetZ,
    int Steps, int Direction, int AnimIndex, bool Drawn, bool Active,
    bool IdleReduction, int PendingPages, PacketSample Packet,
    float TileScreenX, float TileScreenY, float RenderScreenX, float RenderScreenY,
    LayerSample[] Layers);

// Immutable snapshots; frozen history never silently rolls under the scrubber.
internal sealed class MobileHistory
{
    internal const int Capacity = 300;
    private readonly List<MobileSample> _frames = new(Capacity);
    internal bool Frozen { get; private set; }
    internal int Index { get; private set; } = -1;
    internal int Count => _frames.Count;
    internal MobileSample Current => Index >= 0 ? _frames[Index] : null;
    internal MobileSample[] Snapshot() => _frames.ToArray();
    internal void Add(MobileSample sample)
    {
        if (Frozen) return;
        if (_frames.Count == Capacity) _frames.RemoveAt(0);
        _frames.Add(sample with { Layers = (LayerSample[])sample.Layers.Clone() });
        Index = _frames.Count - 1;
    }
    internal void Freeze() => Frozen = true;
    internal void Live() { Frozen = false; Index = _frames.Count - 1; }
    internal void Seek(int index) { Freeze(); Index = _frames.Count == 0 ? -1 : Math.Clamp(index, 0, _frames.Count - 1); }
    internal void Clear() { _frames.Clear(); Index = -1; Frozen = false; }
}
#endif
