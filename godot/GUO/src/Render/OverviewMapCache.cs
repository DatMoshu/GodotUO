// SPDX-License-Identifier: BSD-2-Clause
// GUO addition: bounded, incremental overview tiles (ADR-0045). Main thread only.
using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Runtime.InteropServices;
using Godot;

namespace GUO.Renderer;

internal interface IOverviewBlocks
{
    void ReadBlock(int x, int y, Span<uint> colors);
}

internal sealed class OverviewMapCache : IDisposable
{
    internal const int TileSize = 256;
    internal const int Capacity = 128;
    internal const int BlocksPerTile = TileSize * TileSize / 64;
    internal const int FrameBlocks = 128;
    internal const int FrameMilliseconds = 4;
    internal sealed class Tile
    {
        internal readonly Vector2I Key;
        internal byte[] Pixels;
        internal ImageTexture Texture;
        internal int Blocks;
        internal long Used;
        internal bool Dirty;
        internal Tile(Vector2I key) { Key = key; }
        internal void Dispose() { Texture?.Dispose(); Texture = null; Pixels = null; }
    }
    private readonly Dictionary<Vector2I, Tile> _tiles = new();
    private readonly List<Tile> _visible = new(Capacity);
    private readonly uint[] _block = new uint[64];
    private long _frame;
    internal IReadOnlyList<Tile> Visible => _visible;
    internal int Count => _tiles.Count;
    internal long PixelBytes { get; private set; }
    internal int LastBlocks { get; private set; }
    internal int LastUploads { get; private set; }

    // Caller orders nearest first. Pin all existing hits before allocating misses.
    internal void SetView(List<Vector2I> nearest)
    {
        _frame++;
        _visible.Clear();
        int count = Math.Min(Capacity, nearest.Count);
        for (int i = 0; i < count; i++)
            if (_tiles.TryGetValue(nearest[i], out var tile)) tile.Used = _frame;
        for (int i = 0; i < count; i++)
        {
            var key = nearest[i];
            if (!_tiles.TryGetValue(key, out var tile))
            {
                if (_tiles.Count == Capacity)
                {
                    Tile victim = null;
                    foreach (var candidate in _tiles.Values)
                        if (candidate.Used != _frame && (victim == null || candidate.Used < victim.Used)) victim = candidate;
                    if (victim == null) break;
                    _tiles.Remove(victim.Key);
                    if (victim.Pixels != null) PixelBytes -= victim.Pixels.Length;
                    victim.Dispose();
                }
                _tiles.Add(key, tile = new Tile(key));
            }
            tile.Used = _frame;
            _visible.Add(tile);
        }
    }

    // Hard block/upload counts plus a soft elapsed-time deadline between blocks.
    // A synchronous file read cannot be preempted. No tasks survive a view change.
    internal int Prepare(IOverviewBlocks source, int maxBlocks, long deadline)
    {
        LastBlocks = 0;
        int changed = 0;
        foreach (var tile in _visible)
        {
            if (tile.Blocks == BlocksPerTile) continue;
            if (changed == 2 || LastBlocks >= maxBlocks || Stopwatch.GetTimestamp() >= deadline) break;
            if (tile.Pixels == null)
            {
                tile.Pixels = new byte[TileSize * TileSize * 4];
                PixelBytes += tile.Pixels.Length;
            }
            int before = tile.Blocks;
            while (tile.Blocks < BlocksPerTile && LastBlocks < maxBlocks && Stopwatch.GetTimestamp() < deadline)
            {
                int bx = tile.Blocks % (TileSize / 8), by = tile.Blocks / (TileSize / 8);
                Array.Clear(_block);
                source.ReadBlock(tile.Key.X * (TileSize / 8) + bx, tile.Key.Y * (TileSize / 8) + by, _block);
                var pixels = MemoryMarshal.Cast<byte, uint>(tile.Pixels.AsSpan());
                for (int y = 0; y < 8; y++)
                    _block.AsSpan(y * 8, 8).CopyTo(pixels.Slice((by * 8 + y) * TileSize + bx * 8, 8));
                tile.Blocks++;
                LastBlocks++;
            }
            if (tile.Blocks != before) { tile.Dirty = true; changed++; }
        }
        return LastBlocks;
    }

    internal void Upload()
    {
        LastUploads = 0;
        foreach (var tile in _visible)
        {
            if (!tile.Dirty || LastUploads == 2) continue;
            using var image = Image.CreateFromData(TileSize, TileSize, false, Image.Format.Rgba8, tile.Pixels);
            if (tile.Texture == null) tile.Texture = ImageTexture.CreateFromImage(image);
            else tile.Texture.Update(image);
            tile.Dirty = false;
            LastUploads++;
        }
    }

    public void Dispose()
    {
        foreach (var tile in _tiles.Values) tile.Dispose();
        _tiles.Clear(); _visible.Clear(); PixelBytes = 0;
        LastBlocks = LastUploads = 0;
    }
}
