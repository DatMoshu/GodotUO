// SPDX-License-Identifier: BSD-2-Clause
#if GUO_LABS
using System;
using System.Collections.Generic;
using System.Runtime.InteropServices;
using Godot;
using GUO.Renderer;

namespace GUO.Labs.GpuRenderer;

/// <summary>Bounded source records; only a new content revision receives a new immutable ID.</summary>
internal sealed class OrderedWorldRecords
{
    // Diagnostic control within the existing off-by-default RD experiment.
    internal static bool Enabled = Array.IndexOf(OS.GetCmdlineUserArgs(), "--rd-content-cache") < 0;
    private readonly record struct Key(MeshLayer Layer, int Index, bool Static);
    internal sealed class Record
    {
        internal ulong Id;
        internal long Version, LastFrame;
        internal int OffsetX, OffsetY, Width, Height;
        internal Texture2D Texture;
        internal OrderedWorldResidency.Quad Quad;
        internal MeshQuad SourceQuad;
        internal Color Color0, Color1, Color2, Color3;
        internal Rect2 Destination, Source;
        internal bool Supported;
    }

    private const int Limit = 65536;
    private readonly Dictionary<Key, Record> _records = new();
    private readonly List<Key> _expired = new();
    private long _frame;
    private ulong _nextId;
    internal static long Hits, Packed, DirtyChecks;

    internal void Begin()
    {
        _frame++;
        if (_frame % 60 != 0 && _records.Count < Limit) return;
        _expired.Clear();
        foreach (var pair in _records)
            if (pair.Value.LastFrame < _frame - 2) _expired.Add(pair.Key);
        foreach (Key key in _expired) _records.Remove(key);
        _expired.Clear();
    }

    internal Record Find(MeshLayer layer, int index, bool isStatic,
        int offsetX = 0, int offsetY = 0, int width = 0, int height = 0)
    {
        if (!_records.TryGetValue(new Key(layer, index, isStatic), out Record record)) return null;
        if (!ReferenceEquals(record.Texture, layer.Textures[index])
            || record.OffsetX != offsetX || record.OffsetY != offsetY
            || record.Width != width || record.Height != height) return null;
        long version = layer.QuadRecordVersion(index);
        if (record.Version != version)
        {
            // ResetAlpha then SetVisible can restore the same final fade every
            // frame. Compare only dirty sources; do not repack that transient change.
            DirtyChecks++;
            if (!MemoryMarshal.AsBytes(MemoryMarshal.CreateReadOnlySpan(ref record.SourceQuad, 1))
                .SequenceEqual(MemoryMarshal.AsBytes(MemoryMarshal.CreateReadOnlySpan(ref layer.Vertices[index], 1)))) return null;
            record.Version = version;
        }
        record.LastFrame = _frame;
        Hits++;
        return record;
    }

    internal Record Store(MeshLayer layer, int index, bool isStatic, in MeshQuad quad,
        int offsetX = 0, int offsetY = 0, int width = 0, int height = 0,
        Rect2 destination = default, Rect2 source = default)
    {
        var key = new Key(layer, index, isStatic);
        if (!_records.TryGetValue(key, out Record record))
        {
            if (_records.Count == Limit) return null; // Ordinary exact-content capture still works.
            _records.Add(key, record = new Record());
        }
        record.Id = checked(++_nextId); // Older occurrences keep their copied ID and packet.
        record.Version = layer.QuadRecordVersion(index);
        record.LastFrame = _frame;
        record.Texture = layer.Textures[index];
        record.SourceQuad = layer.Vertices[index];
        record.OffsetX = offsetX; record.OffsetY = offsetY;
        record.Width = width; record.Height = height;
        record.Destination = destination; record.Source = source;
        record.Supported = OrderedWorldCapture.SupportedMode(quad.Hue0) && OrderedWorldCapture.SupportedMode(quad.Hue1)
            && OrderedWorldCapture.SupportedMode(quad.Hue2) && OrderedWorldCapture.SupportedMode(quad.Hue3);
        record.Color0 = UltimaBatcher2D.Encode(quad.Hue0); record.Color1 = UltimaBatcher2D.Encode(quad.Hue1);
        record.Color2 = UltimaBatcher2D.Encode(quad.Hue2); record.Color3 = UltimaBatcher2D.Encode(quad.Hue3);
        record.Quad = new OrderedWorldResidency.Quad
        {
            A = OrderedWorldCapture.PackVertex(quad.Position0, quad.TextureCoordinate0, record.Color0, quad.Normal0, !isStatic, isStatic),
            B = OrderedWorldCapture.PackVertex(quad.Position1, quad.TextureCoordinate1, record.Color1, quad.Normal1, !isStatic, isStatic),
            C = OrderedWorldCapture.PackVertex(quad.Position2, quad.TextureCoordinate2, record.Color2, quad.Normal2, !isStatic, isStatic),
            D = OrderedWorldCapture.PackVertex(quad.Position3, quad.TextureCoordinate3, record.Color3, quad.Normal3, !isStatic, isStatic),
        };
        Packed++;
        return record;
    }

    internal void Clear() { _records.Clear(); _expired.Clear(); }
}
#endif
