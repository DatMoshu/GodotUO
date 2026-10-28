// SPDX-License-Identifier: BSD-2-Clause
using System;
using System.Collections.Generic;
using GUO.Configuration;
using GUO.Game.GameObjects;
using GUO.Game.Map;
using GUO.Renderer;
using GUO.Compat;

namespace GUO.Game.Scenes;

internal partial class GameScene
{
    // PORT DEVIATION (GUO): ADR-0044 Depth World only. A chunk holding only
    // meshed land and statics, whose last walk changed no vertex (no fade,
    // no hue change), keeps that walk's visibility on the GPU when nothing
    // the walk reads has changed: the view, the Z and roof limits, the shared
    // gather inputs, and the chunk's objects (Chunk.ObjectsVersion). Its walk
    // is skipped, its static lights re-added; a chunk under the mouse is
    // always walked, so selection is upstream's. GUO_DW_REUSE=0 turns it off;
    // GUO_DW_REUSE_VERIFY=1 walks reused chunks anyway and counts mismatches.
    internal static readonly bool ChunkReuse = Environment.GetEnvironmentVariable("GUO_DW_REUSE") != "0";
    private static readonly bool VerifyChunkReuse = Environment.GetEnvironmentVariable("GUO_DW_REUSE_VERIFY") == "1";
    internal static long ChunksReused, ChunkReuseMismatches;
    internal static long ChunkWalkRecords, ChunkZScanObjects;

    internal readonly struct ReuseKey
    {
        internal readonly int MaxZ, ObjectsVersion;
        internal readonly sbyte MaxGroundZ;
        internal readonly bool NoDrawRoofs, HideVegetation;
        internal readonly GatherInputs Inputs;

        internal ReuseKey(int maxZ, int objectsVersion,
            sbyte maxGroundZ, bool noDrawRoofs, bool hideVegetation, in GatherInputs inputs)
        {
            MaxZ = maxZ;
            ObjectsVersion = objectsVersion; MaxGroundZ = maxGroundZ; NoDrawRoofs = noDrawRoofs;
            HideVegetation = hideVegetation; Inputs = inputs;
        }

        internal bool Same(in ReuseKey o) => MaxZ == o.MaxZ && ObjectsVersion == o.ObjectsVersion && MaxGroundZ == o.MaxGroundZ
            && NoDrawRoofs == o.NoDrawRoofs && HideVegetation == o.HideVegetation && Inputs.SameForReuse(o.Inputs);
    }

    internal sealed class ChunkWalk
    {
        internal bool Reusable;
        internal int LandStamp, StaticStamp, LandCount, StaticCount;
        // Inline value storage; null still means no completed/current reuse record.
        internal ReuseKey? Key;
        internal ulong DynamicMask, WalkedDynamic;
        // The view offset of the last walk, whether no anchor of the chunk
        // could be culled then, and the tiles skipped since the view moved,
        // whose objects' screen positions are refreshed when next walked.
        internal Point Offset;
        internal Vector2 MinPixel, MaxPixel;
        internal bool FullyInside, OffScreenCleared;
        internal ulong StaleTiles;
        // The Z range of the chunk's anchors (a stretched land's MinZ
        // included): a reused tile holds only meshed land and statics, whose
        // Z changes with a mesh rebuild or a tile walk, both of which widen it.
        internal int ZMin = sbyte.MinValue, ZMax = sbyte.MaxValue;
        internal readonly List<(int Tile, GameObject Light)> Lights = new();
    }

    // Set by AddTileToRenderList while a tile is walked; _walkTile names the tile.
    private bool _walkDynamic;
    private bool _recordChunkWalk;
    private int _walkTile;
    private List<(int Tile, GameObject Light)> _walkLights;
    internal static long TilesReused;

    private ReuseKey CurrentReuseKey(in GatherInputs inputs) => new ReuseKey(_maxZ, 0, _maxGroundZ, _noDrawRoofs, ProfileManager.CurrentProfile.HideVegetation, inputs);

    /// <summary>
    /// The tiles of the chunk that must be walked this frame when its last
    /// walk still holds: those that held anything but meshed land and statics
    /// then (DynamicMask), and those an object entered or left since
    /// (Chunk.DirtyTiles). Null when the chunk must be walked whole. The
    /// other tiles keep their visibility; their static lights are re-added.
    /// </summary>
    private ulong? ReusableTiles(Chunk chunk, in GatherInputs inputs, bool built)
    {
        if (!ChunkReuse || !DepthWorldProbe.ResidentEnabled || built || chunk.ScreenPositionsStale)
            return null;
        if (chunk.Walk is not { Reusable: true } walk || walk.Key is not ReuseKey key) return null;
        // A moved view keeps the walk only of tiles where nothing was or is
        // culled; a static's out-of-range tint follows the player, so that
        // turns it off.
        bool moved = walk.Offset != _offset;
        if (moved && inputs.NoRangeColor)
            return null;
        var mesh = chunk.Mesh;
        if (mesh.Land.ContentStamp != walk.LandStamp || mesh.Statics.ContentStamp != walk.StaticStamp
            || mesh.Land.Count != walk.LandCount || mesh.Statics.Count != walk.StaticCount)
            return null;
        if (!key.Same(CurrentReuseKey(inputs)) || MouseNearChunk(chunk.X, chunk.Y)) return null;
        // PORT DEVIATION (GUO): zoom/peek/resize may change the pixel window
        // without changing world positions. Keep only tiles whose full Z range
        // was inside the old window AND is inside the new one. Testing both
        // offsets against the new window would retain formerly culled tiles
        // on zoom-out. Circle transparency keeps its former full-walk path.
        bool windowChanged = walk.MinPixel != _minPixel || walk.MaxPixel != _maxPixel;
        if (windowChanged && inputs.UseCot) return null;
        bool viewChanged = moved || windowChanged;
        bool fullyInside = walk.FullyInside;
        ulong walkMask = walk.DynamicMask | chunk.DirtyTiles;
        if (viewChanged)
        {
            fullyInside = ChunkFullyInside(chunk.X, chunk.Y, walk.ZMin, walk.ZMax);
            if (!(walk.FullyInside && fullyInside))
            {
                walkMask |= TilesNotInside(chunk.X, chunk.Y, walk.Offset, walk.MinPixel, walk.MaxPixel, walk.ZMin, walk.ZMax)
                    | TilesNotInside(chunk.X, chunk.Y, _offset, _minPixel, _maxPixel, walk.ZMin, walk.ZMax);
                if (walkMask == ulong.MaxValue) return null;
            }
        }
        foreach (var (tile, light) in walk.Lights)
            if ((walkMask & (1UL << tile)) == 0)
            {
                if (moved) light.UpdateRealScreenPosition(_offset.X, _offset.Y);
                AddLight(light, light, light.RealScreenPosition.X + 22, light.RealScreenPosition.Y + 22);
            }
        if (moved) walk.StaleTiles |= ~walkMask;
        // The zero-mask path never calls EndChunkWalk. Advance its view record
        // too, so a later zoom/move uses the bounds paired with this offset.
        walk.Offset = _offset;
        walk.MinPixel = _minPixel;
        walk.MaxPixel = _maxPixel;
        walk.FullyInside = fullyInside;
        if (walkMask == 0) ChunksReused++;
        else TilesReused += 64 - System.Numerics.BitOperations.PopCount(walkMask);
        return walkMask;
    }

    private void WalkChunkTiles(Chunk chunk, bool useHandles, in GatherInputs inputs, ulong? walkMask)
    {
        bool chunkForce = _forcePositions;
        for (var x = 0; x < 8; x++)
        {
            for (var y = 0; y < 8; y++)
            {
                if (walkMask is ulong mask && (mask & (1UL << ((x << 3) | y))) == 0)
                    continue;
                var firstObj = chunk.GetHeadObject(x, y);
                if (firstObj == null || firstObj.IsDestroyed)
                    continue;

                _walkDynamic = false;
                _walkTile = (x << 3) | y;
                ulong bit = 1UL << _walkTile;
                _forcePositions = chunkForce || (chunk.Walk.StaleTiles & bit) != 0;
                chunk.Walk.StaleTiles &= ~bit;
                AddTileToRenderList(firstObj, useHandles, 150, chunk, inputs);
                // A tile with anything but meshed land and statics is walked every frame.
                if (_recordChunkWalk && _walkDynamic) chunk.Walk.WalkedDynamic |= 1UL << _walkTile;
            }
        }
    }

    /// <summary>Partial walk: the tiles about to be walked lose their meshed visibility, as a reset would clear it.</summary>
    private static void ClearTileVisibility(Chunk chunk, ulong walkMask)
    {
        var mesh = chunk.Mesh;
        mesh.Land.VisibilityStamp++;
        mesh.Statics.VisibilityStamp++;
        for (int t = 0; t < 64; t++)
        {
            if ((walkMask & (1UL << t)) == 0) continue;
            for (var obj = chunk.GetHeadObject(t >> 3, t & 7); obj != null; obj = obj.TNext)
                if (obj.InChunkMesh)
                    (obj is Land ? mesh.Land : mesh.Statics).Visible[obj.MeshSpriteIndex] = false;
        }
    }

    // Keep surviving static lights in their original order, including duplicates.
    // RemoveRange clears the vacated references without a captured mask predicate.
    private static void RemoveWalkedLights(List<(int Tile, GameObject Light)> lights, ulong mask)
    {
        int count = lights.Count, kept = 0;
        for (int read = 0; read < count; read++)
        {
            var light = lights[read];
            if ((mask & (1UL << light.Tile)) != 0) continue;
            if (kept != read) lights[kept] = light;
            kept++;
        }
        if (kept != count) lights.RemoveRange(kept, count - kept);
    }

    private void BeginChunkWalk(Chunk chunk, ulong? walkMask)
    {
        chunk.Walk ??= new ChunkWalk();
        var lights = chunk.Walk.Lights;
        // Canvas still needs StaleTiles and the ordinary object walk, but has
        // no consumer of the reuse key, static-light cache or Z-range scan.
        _recordChunkWalk = ChunkReuse && DepthWorldProbe.ResidentEnabled;
        if (!_recordChunkWalk)
        {
            chunk.Walk.Reusable = false;
            chunk.Walk.Key = null;
            lights.Clear();
            _walkLights = null;
            return;
        }
        if (walkMask is ulong mask) RemoveWalkedLights(lights, mask);
        else lights.Clear();
        _walkLights = lights;
        chunk.Walk.WalkedDynamic = 0;
    }

    /// <summary>Records whether this walk may be reused: no vertex changed since the stamps before it.</summary>
    private void EndChunkWalk(Chunk chunk, in GatherInputs inputs, int landStamp, int staticStamp, bool built, ulong? walkMask)
    {
        var walk = chunk.Walk;
        if (_recordChunkWalk)
        {
            ChunkWalkRecords++;
            var mesh = chunk.Mesh;
            walk.Reusable = !built && mesh.Land.ContentStamp == landStamp && mesh.Statics.ContentStamp == staticStamp;
            // A partial walk keeps the skipped tiles' record: they were static.
            walk.DynamicMask = walkMask is ulong mask ? (walk.DynamicMask & ~mask) | walk.WalkedDynamic : walk.WalkedDynamic;
            walk.LandStamp = mesh.Land.ContentStamp; walk.StaticStamp = mesh.Statics.ContentStamp;
            walk.LandCount = mesh.Land.Count; walk.StaticCount = mesh.Statics.Count;
            walk.Key = CurrentReuseKey(inputs);
            walk.Offset = _offset;
            walk.MinPixel = _minPixel;
            walk.MaxPixel = _maxPixel;
            WidenZ(chunk, walk, walkMask);
            walk.FullyInside = ChunkFullyInside(chunk.X, chunk.Y, walk.ZMin, walk.ZMax);
        }
        // A whole walk refreshed every position it had to (see WalkChunkTiles).
        if (walkMask == null) walk.StaleTiles = 0;
        chunk.DirtyTiles = 0;
        _walkLights = null;
    }

    /// <summary>
    /// Depth World: an off-screen chunk whose visibility a whole reset
    /// already cleared, with no mesh rebuilt since, needs no reset again;
    /// the moved-view bookkeeping of the ordinary skip still applies.
    /// </summary>
    private bool SkipClearedOffScreen(Chunk chunk, bool built)
    {
        if (!ChunkReuse || !DepthWorldProbe.ResidentEnabled || built || chunk.Walk is not { OffScreenCleared: true } walk)
            return false;
        if (!RenderLists.SkipOffScreenChunks || !ChunkOffScreen(chunk.X, chunk.Y))
        {
            walk.OffScreenCleared = false;
            return false;
        }
        if (UpdateDrawPosition) chunk.ScreenPositionsStale = true;
        RenderLists.ChunksSkipped++;
        return true;
    }

    /// <summary>After the ordinary off-screen skip: its visibility is clear (if the reset was whole) and its walk record is gone.</summary>
    private static void MarkOffScreenCleared(Chunk chunk, bool wholeReset)
    {
        chunk.Walk ??= new ChunkWalk();
        chunk.Walk.Reusable = false;
        chunk.Walk.OffScreenCleared = wholeReset;
    }

    /// <summary>A whole walk records the chunk's Z range afresh; a partial one widens it by the tiles it walked.</summary>
    private static void WidenZ(Chunk chunk, ChunkWalk walk, ulong? walkMask)
    {
        int zMin = walkMask == null ? int.MaxValue : walk.ZMin, zMax = walkMask == null ? int.MinValue : walk.ZMax;
        for (int t = 0; t < 64; t++)
        {
            if (walkMask is ulong mask && (mask & (1UL << t)) == 0) continue;
            for (var obj = chunk.GetHeadObject(t >> 3, t & 7); obj != null; obj = obj.TNext)
            {
                ChunkZScanObjects++;
                int z = obj.Z;
                if (obj is Land { IsStretched: true } land && land.MinZ < z) z = land.MinZ;
                if (z < zMin) zMin = z;
                if (obj.Z > zMax) zMax = obj.Z;
            }
        }
        if (zMin > zMax) { zMin = 0; zMax = 0; } // an empty chunk has no anchor to cull
        walk.ZMin = zMin; walk.ZMax = zMax;
    }

    /// <summary>The tiles of chunk (cx, cy) with an anchor, at view offset o and Z in [zMin, zMax], outside the pixel window.</summary>
    private static ulong TilesNotInside(int cx, int cy, Point o, Vector2 minPixel, Vector2 maxPixel, int zMin, int zMax)
    {
        ulong mask = 0;
        int x0 = cx << 3, y0 = cy << 3;
        for (int x = 0; x < 8; x++)
            for (int y = 0; y < 8; y++)
            {
                int sx = (x0 + x - (y0 + y)) * 22 - o.X - 22;
                int sy = (x0 + x + y0 + y) * 22 - o.Y - 22;
                if (sx < minPixel.X || sx > maxPixel.X || sy - (zMax << 2) < minPixel.Y || sy - (zMin << 2) > maxPixel.Y)
                    mask |= 1UL << ((x << 3) | y);
            }
        return mask;
    }

    /// <summary>True when every anchor the chunk can have, at Z in [zMin, zMax], is inside the walk's pixel window: no object is culled.</summary>
    private bool ChunkFullyInside(int cx, int cy, int zMin = sbyte.MinValue, int zMax = sbyte.MaxValue)
    {
        int x0 = cx << 3, y0 = cy << 3;
        int minX = (x0 - (y0 + 7)) * 22 - _offset.X - 22;
        int maxX = (x0 + 7 - y0) * 22 - _offset.X - 22;
        int minY = (x0 + y0) * 22 - (zMax << 2) - _offset.Y - 22;
        int maxY = (x0 + y0 + 14) * 22 - (zMin << 2) - _offset.Y - 22;
        return minX >= _minPixel.X && maxX <= _maxPixel.X && minY >= _minPixel.Y && maxY <= _maxPixel.Y;
    }

    /// <summary>Generous screen bounds of the chunk's sprites (tall statics reach far above their anchors).</summary>
    private bool MouseNearChunk(int cx, int cy)
    {
        int x0 = cx << 3, y0 = cy << 3;
        int minX = (x0 - (y0 + 7)) * 22 - _offset.X - 22 - 128;
        int maxX = (x0 + 7 - y0) * 22 - _offset.X - 22 + 128;
        int minY = (x0 + y0) * 22 - (sbyte.MaxValue << 2) - _offset.Y - 22 - 768;
        int maxY = (x0 + y0 + 14) * 22 - (sbyte.MinValue << 2) - _offset.Y - 22 + 128;
        var m = SelectedObject.TranslatedMousePositionByViewport;
        return m.X >= minX && m.X <= maxX && m.Y >= minY && m.Y <= maxY;
    }

    // Verify mode: the reused chunk's visibility, kept to compare with a real walk.
    private bool[] _verifyLand = Array.Empty<bool>(), _verifyStatics = Array.Empty<bool>();

    private void KeepVisibility(Chunk chunk)
    {
        var mesh = chunk.Mesh;
        if (_verifyLand.Length < mesh.Land.Count) _verifyLand = new bool[mesh.Land.Count * 2];
        if (_verifyStatics.Length < mesh.Statics.Count) _verifyStatics = new bool[mesh.Statics.Count * 2];
        Array.Copy(mesh.Land.Visible, _verifyLand, mesh.Land.Count);
        Array.Copy(mesh.Statics.Visible, _verifyStatics, mesh.Statics.Count);
    }

    private void CompareVisibility(Chunk chunk)
    {
        var mesh = chunk.Mesh;
        if (!mesh.Land.Visible.AsSpan(0, mesh.Land.Count).SequenceEqual(_verifyLand.AsSpan(0, mesh.Land.Count))
            || !mesh.Statics.Visible.AsSpan(0, mesh.Statics.Count).SequenceEqual(_verifyStatics.AsSpan(0, mesh.Statics.Count)))
        {
            if (ChunkReuseMismatches++ < 8)
            {
                string what = "";
                for (int t = 0; t < 64 && what.Length == 0; t++)
                    for (var obj = chunk.GetHeadObject(t >> 3, t & 7); obj != null; obj = obj.TNext)
                    {
                        if (!obj.InChunkMesh) continue;
                        bool land = obj is Land;
                        bool now = (land ? mesh.Land : mesh.Statics).Visible[obj.MeshSpriteIndex];
                        bool kept = (land ? _verifyLand : _verifyStatics)[obj.MeshSpriteIndex];
                        if (now == kept) continue;
                        what = $"{obj.GetType().Name} 0x{obj.Graphic:X4} at {obj.X},{obj.Y},{obj.Z} tile {t} reused {kept} walked {now} alpha {obj.AlphaHue} "
                            + $"screen {obj.RealScreenPosition} pixels {_minPixel}-{_maxPixel} maxZ {_maxZ} maxGroundZ {_maxGroundZ} dyn {((chunk.Walk.DynamicMask >> t) & 1)}";
                        break;
                    }
                Godot.GD.Print($"[GUO] chunk reuse mismatch at chunk {chunk.X},{chunk.Y}: {what}");
            }
        }
    }
}
