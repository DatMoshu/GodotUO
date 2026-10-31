// SPDX-License-Identifier: BSD-2-Clause
using System;
using System.Runtime.CompilerServices;
using Godot;

namespace GUO.Renderer;

// Exact native command retention, not scene/visibility caching. Main-thread only.
// Native resources remain owned by the existing renderer; these are RID values.
internal sealed class RetainedCanvasCommands
{
    internal sealed class Budget
    {
        internal const int Limit = 65536;
        internal int Capacity;
    }

    internal enum Kind : byte { Rect, Mesh, Affine }

    internal readonly struct Command
    {
        internal readonly Kind Type;
        internal readonly Rid Texture, Mesh;
        internal readonly Rect2 Destination, Source;
        internal readonly Color Modulate;
        internal readonly Transform2D Transform;

        internal Command(Kind type, Rid texture, Rid mesh, Rect2 destination,
            Rect2 source, Color modulate, Transform2D transform)
        {
            Type = type; Texture = texture; Mesh = mesh;
            Destination = destination; Source = source;
            Modulate = modulate; Transform = transform;
        }

        internal bool Same(in Command other) => Type == other.Type && Texture == other.Texture
            && Mesh == other.Mesh && Exact(Modulate.R, other.Modulate.R)
            && Exact(Modulate.G, other.Modulate.G) && Exact(Modulate.B, other.Modulate.B)
            && Exact(Modulate.A, other.Modulate.A)
            && (Type == Kind.Mesh || (Same(Destination, other.Destination) && Same(Source, other.Source)))
            && (Type == Kind.Rect || (Same(Transform.X, other.Transform.X)
                && Same(Transform.Y, other.Transform.Y) && Same(Transform.Origin, other.Transform.Origin)));

        private static bool Same(Rect2 a, Rect2 b) => Same(a.Position, b.Position) && Same(a.Size, b.Size);
        private static bool Same(Vector2 a, Vector2 b) => Exact(a.X, b.X) && Exact(a.Y, b.Y);
        [MethodImpl(MethodImplOptions.AggressiveInlining)]
        private static bool Exact(float a, float b) => BitConverter.SingleToInt32Bits(a) == BitConverter.SingleToInt32Bits(b);
    }

    internal interface ISink
    {
        void Clear(Rid item);
        void Emit(Rid item, in Command command);
    }

    private sealed class NativeSink : ISink
    {
        public void Clear(Rid item) => RenderingServer.CanvasItemClear(item);
        public void Emit(Rid item, in Command c)
        {
            switch (c.Type)
            {
                case Kind.Rect:
                    RenderingServer.CanvasItemAddTextureRectRegion(item, c.Destination, c.Texture, c.Source, c.Modulate, false, false);
                    break;
                case Kind.Mesh:
                    RenderingServer.CanvasItemAddMesh(item, c.Mesh, c.Transform, c.Modulate, c.Texture);
                    break;
                case Kind.Affine:
                    RenderingServer.CanvasItemAddSetTransform(item, c.Transform);
                    RenderingServer.CanvasItemAddTextureRectRegion(item, c.Destination, c.Texture, c.Source, c.Modulate, false, false);
                    RenderingServer.CanvasItemAddSetTransform(item, Transform2D.Identity);
                    break;
            }
        }
    }

    internal static readonly ISink Native = new NativeSink();
    internal static long Compared, Reused, Submitted, Clears, Uncached;
    private readonly Budget _budget;
    private readonly ISink _sink;
    private readonly Rid _item;
    private Command[] _commands = Array.Empty<Command>();
    private int _count, _cursor;
    private bool _dirty, _uncached;
    internal bool HasCommands => _count != 0 && !_uncached;

    internal RetainedCanvasCommands(Budget budget, ISink sink, Rid item)
    {
        _budget = budget; _sink = sink; _item = item;
    }

    internal void Begin()
    {
        _cursor = 0;
        _dirty = false;
        _uncached = false;
    }

    // The owner cleared the native item (disabled mode, or an uncached frame).
    internal void Reset()
    {
        _count = _cursor = 0;
        _dirty = _uncached = false;
    }

    internal void Add(in Command command)
    {
        if (_uncached) { Emit(command); return; }
        Compared++;
        if (!_dirty && _cursor < _count && _commands[_cursor].Same(command))
        {
            _cursor++;
            Reused++;
            return;
        }
        RewritePrefix();
        if (_cursor == _commands.Length)
        {
            int capacity = Math.Max(8, _commands.Length * 2);
            int increase = capacity - _commands.Length;
            if (capacity > 8192 || _budget.Capacity + increase > Budget.Limit)
            {
                // Prefix is already native. Keep drawing without retaining;
                // never drop a command when the memory budget is exhausted.
                _uncached = true;
                _count = 0;
                Uncached++;
                Emit(command);
                return;
            }
            Array.Resize(ref _commands, capacity);
            _budget.Capacity += increase;
        }
        _commands[_cursor++] = command;
        Emit(command);
    }

    // Before a native command not represented here (e.g. arbitrary triangles).
    internal void Bypass()
    {
        if (_uncached) return;
        RewritePrefix();
        _uncached = true;
        _count = 0;
        Uncached++;
    }

    internal void End(bool reclaimEmpty = false)
    {
        if (_uncached) return;
        // A matching but shorter stream must remove the previous suffix.
        if (_cursor != _count) RewritePrefix();
        _count = _cursor;
        // Only a finalized cacheable empty stream may return its backing slots.
        // RewritePrefix above must clear the old native suffix first. Hidden
        // nonempty owners and the uncached early return keep their warm storage.
        if (reclaimEmpty && _count == 0 && _commands.Length != 0)
        {
            _budget.Capacity -= _commands.Length;
            _commands = Array.Empty<Command>();
        }
    }

    private void RewritePrefix()
    {
        if (_dirty) return;
        if (_count != 0)
        {
            _sink.Clear(_item);
            Clears++;
            for (int i = 0; i < _cursor; i++) Emit(_commands[i]);
        }
        _dirty = true;
    }

    private void Emit(in Command command)
    {
        _sink.Emit(_item, command);
        Submitted++;
    }
}
