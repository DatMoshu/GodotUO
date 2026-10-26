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
        internal readonly List<GameObject> Lights = new();
    }

    // Set by AddTileToRenderList while a chunk is walked.
    private bool _walkDynamic;
    private List<GameObject> _walkLights;

    private ReuseKey CurrentReuseKey(Chunk chunk, in GatherInputs inputs) => new ReuseKey(_offset, _minPixel, _maxPixel,
        _maxZ, chunk.ObjectsVersion, _maxGroundZ, _noDrawRoofs, ProfileManager.CurrentProfile.HideVegetation, inputs);

    /// <summary>True when the chunk's last walk still holds; then its lights are re-added and the walk is skipped.</summary>
    private bool TryReuseChunk(Chunk chunk, in GatherInputs inputs, bool built)
    {
        if (!ChunkReuse || !DepthWorldProbe.Enabled || built || UpdateDrawPosition || chunk.ScreenPositionsStale)
            return false;
        if (chunk.Walk is not { Reusable: true } walk || walk.Key is not ReuseKey key) return false;
        var mesh = chunk.Mesh;
        if (mesh.Land.ContentStamp != walk.LandStamp || mesh.Statics.ContentStamp != walk.StaticStamp
            || mesh.Land.Count != walk.LandCount || mesh.Statics.Count != walk.StaticCount)
            return false;
        if (!key.Same(CurrentReuseKey(chunk, inputs)) || MouseNearChunk(chunk.X, chunk.Y)) return false;
        foreach (var light in walk.Lights)
            AddLight(light, light, light.RealScreenPosition.X + 22, light.RealScreenPosition.Y + 22);
        ChunksReused++;
        return true;
    }

    private void BeginChunkWalk(Chunk chunk)
    {
        _walkDynamic = false;
        chunk.Walk ??= new ChunkWalk();
        chunk.Walk.Lights.Clear();
        _walkLights = chunk.Walk.Lights;
    }

    /// <summary>Records whether this walk may be reused: only meshed objects, and no vertex changed since the stamps before the reset.</summary>
    private void EndChunkWalk(Chunk chunk, in GatherInputs inputs, int landStamp, int staticStamp, bool built)
    {
        var walk = chunk.Walk;
        var mesh = chunk.Mesh;
        walk.Reusable = !_walkDynamic && !built && mesh.Land.ContentStamp == landStamp && mesh.Statics.ContentStamp == staticStamp;
        walk.LandStamp = mesh.Land.ContentStamp; walk.StaticStamp = mesh.Statics.ContentStamp;
        walk.LandCount = mesh.Land.Count; walk.StaticCount = mesh.Statics.Count;
        walk.Key = CurrentReuseKey(chunk, inputs);
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
