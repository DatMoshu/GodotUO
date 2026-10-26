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

    private readonly struct ReuseKey
    {
        internal readonly Point Offset;
        internal readonly Vector2 MinPixel, MaxPixel;
        internal readonly int MaxZ, ObjectsVersion;
        internal readonly sbyte MaxGroundZ;
        internal readonly bool NoDrawRoofs, HideVegetation;
        internal readonly GatherInputs Inputs;

        internal ReuseKey(Point offset, Vector2 minPixel, Vector2 maxPixel, int maxZ, int objectsVersion,
            sbyte maxGroundZ, bool noDrawRoofs, bool hideVegetation, in GatherInputs inputs)
        {
            Offset = offset; MinPixel = minPixel; MaxPixel = maxPixel; MaxZ = maxZ;
            ObjectsVersion = objectsVersion; MaxGroundZ = maxGroundZ; NoDrawRoofs = noDrawRoofs;
            HideVegetation = hideVegetation; Inputs = inputs;
        }

        internal bool Same(in ReuseKey o) => Offset == o.Offset && MinPixel == o.MinPixel && MaxPixel == o.MaxPixel
            && MaxZ == o.MaxZ && ObjectsVersion == o.ObjectsVersion && MaxGroundZ == o.MaxGroundZ
            && NoDrawRoofs == o.NoDrawRoofs && HideVegetation == o.HideVegetation && Inputs.Same(o.Inputs);
    }

    internal sealed class ChunkWalk
    {
        internal bool Reusable;
        internal int LandStamp, StaticStamp, LandCount, StaticCount;
        internal object Key;
        internal ulong DynamicMask, WalkedDynamic;
        internal readonly List<(int Tile, GameObject Light)> Lights = new();
    }

    // Set by AddTileToRenderList while a tile is walked; _walkTile names the tile.
    private bool _walkDynamic;
    private int _walkTile;
    private List<(int Tile, GameObject Light)> _walkLights;
    internal static long TilesReused;

    private ReuseKey CurrentReuseKey(in GatherInputs inputs) => new ReuseKey(_offset, _minPixel, _maxPixel,
        _maxZ, 0, _maxGroundZ, _noDrawRoofs, ProfileManager.CurrentProfile.HideVegetation, inputs);

    /// <summary>
    /// The tiles of the chunk that must be walked this frame when its last
    /// walk still holds: those that held anything but meshed land and statics
    /// then (DynamicMask), and those an object entered or left since
    /// (Chunk.DirtyTiles). Null when the chunk must be walked whole. The
    /// other tiles keep their visibility; their static lights are re-added.
    /// </summary>
    private ulong? ReusableTiles(Chunk chunk, in GatherInputs inputs, bool built)
    {
        if (!ChunkReuse || !DepthWorldProbe.Enabled || built || UpdateDrawPosition || chunk.ScreenPositionsStale)
            return null;
        if (chunk.Walk is not { Reusable: true } walk || walk.Key is not ReuseKey key) return null;
        var mesh = chunk.Mesh;
        if (mesh.Land.ContentStamp != walk.LandStamp || mesh.Statics.ContentStamp != walk.StaticStamp
            || mesh.Land.Count != walk.LandCount || mesh.Statics.Count != walk.StaticCount)
            return null;
        if (!key.Same(CurrentReuseKey(inputs)) || MouseNearChunk(chunk.X, chunk.Y)) return null;
        ulong walkMask = walk.DynamicMask | chunk.DirtyTiles;
        foreach (var (tile, light) in walk.Lights)
            if ((walkMask & (1UL << tile)) == 0)
                AddLight(light, light, light.RealScreenPosition.X + 22, light.RealScreenPosition.Y + 22);
        if (walkMask == 0) ChunksReused++;
        else TilesReused += 64 - System.Numerics.BitOperations.PopCount(walkMask);
        return walkMask;
    }

    private void WalkChunkTiles(Chunk chunk, bool useHandles, in GatherInputs inputs, ulong? walkMask)
    {
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
                AddTileToRenderList(firstObj, useHandles, 150, chunk, inputs);
                // A tile with anything but meshed land and statics is walked every frame.
                if (_walkDynamic) chunk.Walk.WalkedDynamic |= 1UL << _walkTile;
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

    private void BeginChunkWalk(Chunk chunk, ulong? walkMask)
    {
        chunk.Walk ??= new ChunkWalk();
        var lights = chunk.Walk.Lights;
        if (walkMask is ulong mask) lights.RemoveAll(l => (mask & (1UL << l.Tile)) != 0);
        else lights.Clear();
        _walkLights = lights;
        chunk.Walk.WalkedDynamic = 0;
    }

    /// <summary>Records whether this walk may be reused: no vertex changed since the stamps before it.</summary>
    private void EndChunkWalk(Chunk chunk, in GatherInputs inputs, int landStamp, int staticStamp, bool built, ulong? walkMask)
    {
        var walk = chunk.Walk;
        var mesh = chunk.Mesh;
        walk.Reusable = !built && mesh.Land.ContentStamp == landStamp && mesh.Statics.ContentStamp == staticStamp;
        // A partial walk keeps the skipped tiles' record: they were static.
        walk.DynamicMask = walkMask is ulong mask ? (walk.DynamicMask & ~mask) | walk.WalkedDynamic : walk.WalkedDynamic;
        walk.LandStamp = mesh.Land.ContentStamp; walk.StaticStamp = mesh.Statics.ContentStamp;
        walk.LandCount = mesh.Land.Count; walk.StaticCount = mesh.Statics.Count;
        walk.Key = CurrentReuseKey(inputs);
        chunk.DirtyTiles = 0;
        _walkLights = null;
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
            if (ChunkReuseMismatches++ < 5)
                Godot.GD.Print($"[GUO] chunk reuse mismatch at chunk {chunk.X},{chunk.Y}");
        }
    }
}
