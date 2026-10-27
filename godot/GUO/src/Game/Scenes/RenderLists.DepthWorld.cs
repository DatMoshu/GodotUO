// SPDX-License-Identifier: BSD-2-Clause
using System;
using System.Collections.Generic;
using GUO.Game.GameObjects;
using GUO.Game.Map;
using GUO.Renderer;

namespace GUO.Game.Scenes;

internal partial class RenderLists
{
    private UltimaBatcher2D _recoveryBatcher;
    private List<Chunk> _recoveryChunks;
    private int _recoveryX, _recoveryY;
    private sbyte _recoveryMaxZ;
    private Action _recoverCanvas;

    // PORT DEVIATION (GUO): retain source owners until the frame ticket retires.
    // Successful frames do not walk, pack or submit the resident statics again.
    private void BindDepthWorldRecovery(UltimaBatcher2D batcher, List<Chunk> chunks, int x, int y, sbyte maxZ)
    {
        _recoveryBatcher = batcher; _recoveryChunks = chunks;
        _recoveryX = x; _recoveryY = y; _recoveryMaxZ = maxZ;
        _recoverCanvas ??= ReplayDepthWorldCanvas;
        batcher.SetOrderedRecovery(_recoverCanvas);
    }

    private Dictionary<int, int> _recoveryOriginalGroups;

    private RenderLists CompleteCanvasList(List<Chunk> chunks, sbyte maxZ)
    {
        var plan = new RenderLists();
        plan.Clear();
        plan._recoveryOriginalGroups = new Dictionary<int, int>();
        var dynamic = new Dictionary<GameObject, List<Drawable>>();
        foreach (var d in _world)
        {
            if (!dynamic.TryGetValue(d.Object, out var draws))
                dynamic.Add(d.Object, draws = new List<Drawable>(2));
            draws.Add(d);
        }
        var visited = new HashSet<GameObject>();
        void AddDynamic(GameObject obj)
        {
            if (!visited.Add(obj) || !dynamic.TryGetValue(obj, out var draws)) return;
            foreach (var d in draws)
            {
                int seq = plan._world.Count;
                plan._recoveryOriginalGroups.Add(seq, d.Seq + 1);
                plan._world.Add(new Drawable(d.Object, d.Depth, seq, d.Mesh, d.Pass, d.Slot));
            }
            plan.CoverFromBelow(obj, draws[0].Depth, forceCanvas: true);
        }
        // Match WalkChunkTiles' chunk/x/y/linked-object order. Appending every
        // resident after the dynamic list changes equal-depth canvas ties.
        foreach (var chunk in chunks)
        {
            var layer = chunk.Mesh?.Statics;
            if (layer == null) continue;
            for (int x = 0; x < 8; x++)
            for (int y = 0; y < 8; y++)
            for (var obj = chunk.GetHeadObject(x, y); obj != null; obj = obj.TNext)
            {
                if (dynamic.ContainsKey(obj)) { AddDynamic(obj); continue; }
                int i = obj.MeshSpriteIndex;
                if (obj is Land || !obj.InChunkMesh || i < 0 || i >= layer.Count
                    || !layer.Visible[i] || obj.Z > maxZ) continue;
                float depth = obj.CalculateDepthZ();
                plan._world.Add(new Drawable(obj, depth, plan._world.Count, layer));
                plan.CoverFromBelow(obj, depth, forceCanvas: true);
            }
        }
        // Non-tile additions retain their original insertion order.
        foreach (var d in _world) AddDynamic(d.Object);
        return plan;
    }

    // Before any GameObject.Draw: known admission failures use the ordinary
    // complete queue, without another visibility walk or advancing fades twice.
    internal void FinishDepthWorldAdmission(UltimaBatcher2D batcher, List<Chunk> chunks, sbyte maxZ)
    {
        if (!DepthWorldProbe.ResidentEnabled) return;
        if (batcher.OrderedWorldRecording) return;
        var plan = CompleteCanvasList(chunks, maxZ);
        _world.Clear(); _world.AddRange(plan._world);
        _coverStamp = plan._coverStamp; _coverOwner = plan._coverOwner;
        DepthWorldProbe.FrameAdmitted = false;
    }

    internal static long RecoveryPlans, RecoveryMeshCovers, RecoveryFlatCovers;

    private void ReplayDepthWorldCanvas()
    {
        RecoveryPlans++;
        var batcher = _recoveryBatcher;
        var plan = CompleteCanvasList(_recoveryChunks, _recoveryMaxZ);
        try
        {
            batcher.BeginOrderedRecovery();
            batcher.OrderedRecoveryGroup(-1, -1); // All mesh land, including unvisited suffix.
            foreach (var chunk in _recoveryChunks)
            {
                var land = chunk.Mesh?.Land;
                if (land == null) continue;
                for (int i = 0; i < land.Count; i++)
                    batcher.ReplayOrderedMesh(land, i, false, _recoveryX, _recoveryY, -1);
            }
            batcher.OrderedRecoveryGroup(0, 0);
            batcher.ReplayOrderedGroup(0);
            var keys = new ulong[plan._world.Count];
            for (int i = 0; i < keys.Length; i++) keys[i] = DepthKey.Of(plan._world[i].Depth, i);
            WorldDepthSort.SortQueueKeys(keys, keys.Length, ref plan._sortScratch);
            for (int order = 0; order < keys.Length; order++)
            {
                var d = plan._world[DepthKey.Seq(keys[order])];
                bool captured = plan._recoveryOriginalGroups.TryGetValue(d.Seq, out int group);
                if (!captured) group = _world.Count + d.Seq + 1;
                batcher.OrderedRecoveryGroup(group, order + 1);
                if (captured) batcher.ReplayOrderedGroup(group);
                else if (d.Mesh != null)
                    batcher.ReplayOrderedMesh(d.Mesh, d.Object.MeshSpriteIndex, true, _recoveryX, _recoveryY, group);
                else if (d.Object is Land land)
                {
                    var chunk = land.InChunkMesh && land.MeshSpriteIndex >= 0
                        ? land.World?.Map?.GetChunk(land.X, land.Y, false) : null;
                    if (chunk != null) {
                        RecoveryMeshCovers++;
                        batcher.ReplayOrderedMesh(chunk.Mesh.Land, land.MeshSpriteIndex, false, _recoveryX, _recoveryY, group);
                    }
                    else if (!land.IsStretched) { RecoveryFlatCovers++; land.ReplayFlatCover(batcher, group); }
                }
            }
            batcher.OrderedRecoveryGroup(int.MaxValue, int.MaxValue);
            batcher.ReplayOrderedGroup(int.MaxValue);
        }
        finally { plan._coverOwner.Release(); }
    }
}
