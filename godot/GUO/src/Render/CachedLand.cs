// SPDX-License-Identifier: BSD-2-Clause
using System;
using System.Collections.Generic;
using GUO.Game.Map;

namespace GUO.Renderer;

/// <summary>One persistent ordered terrain draw, with the complete original fallback.</summary>
internal sealed class CachedLand : IDisposable
{
    private readonly PersistentLand _persistent = new();
    private readonly MergedLand _fallback = new();
    private readonly List<MeshLayer> _layers = new();
    internal static long TotalRebuilds, TotalRebuiltSprites;
    internal int CachedChunks => _persistent.LiveSlots;
    internal long Rebuilds => _persistent.DirtySlots;
    internal long IndexBytes => _persistent.IndexBytes;
    internal int LastDrawCalls => MergedLand.LastSegments;
    internal string LastFallback { get; private set; }

    internal int Draw(UltimaBatcher2D batcher, List<Chunk> chunks)
    {
        _layers.Clear();
        foreach (Chunk chunk in chunks) _layers.Add(chunk.Mesh.Land);
        if (MergedLand.Array)
        {
            long dirty = _persistent.DirtySlots, sprites = _persistent.DirtySprites;
            bool drawn = _persistent.TryDraw(batcher, _layers, out int count, out string failure);
            TotalRebuilds += _persistent.DirtySlots - dirty;
            TotalRebuiltSprites += _persistent.DirtySprites - sprites;
            LastFallback = failure;
            if (drawn)
            {
                MergedLand.LastSegments = _persistent.LastDrawCalls;
                return count;
            }
        }
        return _fallback.Draw(batcher, _layers);
    }

    public void Dispose()
    {
        _persistent.Dispose();
        _fallback.Dispose();
        _layers.Clear();
    }
}
