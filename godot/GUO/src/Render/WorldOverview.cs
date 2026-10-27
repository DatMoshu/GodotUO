// SPDX-License-Identifier: BSD-2-Clause
// GUO addition: local terrain/building overview from 15x, ADR-0045.
using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Runtime.InteropServices;
using Godot;
using GUO.Assets;
using GUO.Game;
using GUO.Game.Managers;
using GUO.Utility;
using Rectangle = GUO.Compat.Rectangle;
using Color = GUO.Compat.Color;

namespace GUO.Renderer;

internal sealed class WorldOverview : IDisposable, IOverviewBlocks
{
    private readonly OverviewMapCache _cache = new();
    private readonly List<Vector3I> _keys = new();
    private readonly StaticsBlock[] _statics = new StaticsBlock[256];
    private readonly int[] _heights = new int[64];
    private World _world;
    private MapLoader _maps;
    private int _map = -1, _season = -1, _theme = -1;
    private long _revision = -1;
    private Vector2 _centre;
    private readonly Comparison<Vector3I> _nearest;
    internal OverviewMapCache Cache => _cache;
    internal const float Threshold = 15f;
    internal static bool Enabled(float zoom) => zoom >= Threshold;
    internal int Level { get; private set; }
    internal int RequestedTiles => _keys.Count;
    private readonly uint[] _sampleBlock = new uint[64];

    internal static float FitZoom(int mapWidth, int mapHeight, int viewWidth, int viewHeight)
    {
        float extent = 22f * (mapWidth + mapHeight);
        return Math.Max(Threshold, (float)Math.Ceiling(Math.Max(
            extent / Math.Max(1, viewWidth - 64), extent / Math.Max(1, viewHeight - 64))));
    }

    internal static float WheelZoom(float zoom, bool up, float normalStep)
    {
        if (zoom > Threshold) return up ? Math.Max(Threshold, zoom / 1.2f) : zoom * 1.2f;
        if (zoom == Threshold && !up) return zoom * 1.2f;
        float next = zoom + (up ? -normalStep : normalStep);
        return !up && zoom < Threshold && next >= Threshold - .0001f ? Threshold : next;
    }

    internal static Transform2D MapTransform(Camera camera, GUO.Compat.Point offset,
        int playerZ, int width, int height)
    {
        var transform = camera.ViewTransform * new Transform2D(new Vector2(22, 22), new Vector2(-22, 22),
            new Vector2(-offset.X, -offset.Y - 22 - playerZ * 4));
        float fit = FitZoom(width, height, camera.Bounds.Width, camera.Bounds.Height);
        float t = fit <= Threshold ? 1 : Math.Clamp((camera.Zoom - Threshold) / (fit - Threshold), 0, 1);
        t = t * t * (3 - 2 * t);
        var centre = new Vector2(camera.Bounds.Width, camera.Bounds.Height) * .5f;
        transform.Origin += (centre - transform * new Vector2(width * .5f, height * .5f)) * t;
        return transform;
    }

    // Choose a screen-appropriate power of two, then widen coverage if needed.
    // Every visible tile fits: the cache never truncates the full-facet view.
    internal static int ChooseLevel(float zoom, Vector2 lo, Vector2 hi)
    {
        int level = 0;
        while (level < 12 && (1 << (level + 1)) <= zoom / Threshold) level++;
        while (level < 12)
        {
            int side = OverviewMapCache.TileSize << level;
            long columns = Math.Max(0, (long)Math.Floor(hi.X / side) - (long)Math.Floor(lo.X / side) + 1);
            long rows = Math.Max(0, (long)Math.Floor(hi.Y / side) - (long)Math.Floor(lo.Y / side) + 1);
            if (columns * rows <= OverviewMapCache.Capacity) break;
            level++;
        }
        return level;
    }

    internal WorldOverview() { _nearest = CompareDistance; }
    private int CompareDistance(Vector3I a, Vector3I b) => (new Vector2(a.X, a.Y) - _centre).LengthSquared().CompareTo((new Vector2(b.X, b.Y) - _centre).LengthSquared());

    internal void Draw(World world, Camera camera, GUO.Compat.Point offset, UltimaBatcher2D batcher)
    {
        var maps = Client.Game.UO.FileManager.Maps;
        int map = world.Map.Index;
        if (!ReferenceEquals(maps, _maps) || map != _map || (int)world.Season != _season
            || maps.DataRevision != _revision || ThemeManager.Revision != _theme)
        {
            _cache.Dispose();
            _maps = maps; _map = map; _season = (int)world.Season;
            _revision = maps.DataRevision; _theme = ThemeManager.Revision;
        }
        _world = world;
        // A map tile's top vertex is 22 world pixels above its ground centre.
        int width = maps.MapBlocksSize[map, 0] * 8, height = maps.MapBlocksSize[map, 1] * 8;
        var transform = MapTransform(camera, offset, world.Player.Z, width, height);
        var inverse = transform.AffineInverse();
        var size = new Vector2(camera.Bounds.Width, camera.Bounds.Height);
        Vector2 lo = new(float.MaxValue, float.MaxValue), hi = new(float.MinValue, float.MinValue);
        for (int corner = 0; corner < 4; corner++)
        {
            var tile = inverse * new Vector2((corner & 1) == 0 ? 0 : size.X, (corner & 2) == 0 ? 0 : size.Y);
            lo = lo.Min(tile); hi = hi.Max(tile);
        }
        lo = lo.Max(Vector2.Zero); hi = hi.Min(new Vector2(width - 1, height - 1));
        Level = ChooseLevel(camera.Zoom, lo, hi);
        int side = OverviewMapCache.TileSize << Level;
        int minX = Math.Max(0, (int)Math.Floor(lo.X / side)), minY = Math.Max(0, (int)Math.Floor(lo.Y / side));
        int maxX = Math.Min((width - 1) / side, (int)Math.Floor(hi.X / side));
        int maxY = Math.Min((height - 1) / side, (int)Math.Floor(hi.Y / side));
        _keys.Clear();
        for (int y = minY; y <= maxY; y++)
        for (int x = minX; x <= maxX; x++)
        {
            var p = transform * new Vector2(x * side, y * side);
            var dx = transform.X * side; var dy = transform.Y * side;
            var left = p.Min(p + dx).Min(p + dy).Min(p + dx + dy);
            var right = p.Max(p + dx).Max(p + dy).Max(p + dx + dy);
            if (right.X >= 0 && right.Y >= 0 && left.X <= size.X && left.Y <= size.Y) _keys.Add(new Vector3I(x, y, Level));
        }
        _centre = (inverse * (size * .5f)) / side - new Vector2(.5f, .5f);
        _keys.Sort(_nearest);
        _cache.SetView(_keys);
        _cache.Prepare(this, Level == 0 ? OverviewMapCache.FrameBlocks : OverviewMapCache.CoarseFrameBlocks,
            Stopwatch.GetTimestamp() + Stopwatch.Frequency * OverviewMapCache.FrameMilliseconds / 1000);
        _cache.Upload();

        batcher.Begin();
        batcher.SetSampler(SamplerState.PointClamp);
        batcher.SetCircleOfTransparencyRadius(0);
        batcher.PushUiTransform(transform);
        // Previously cached levels remain visible while the new level fills.
        // Coarse first, fine last; transparent unprepared pixels preserve that preview.
        for (int level = 12; level >= 0; level--)
            if (level != Level)
                foreach (var tile in _cache.Cached)
                    if (tile.Key.Z == level && tile.Texture != null) DrawTile(batcher, tile);
        foreach (var tile in _cache.Visible)
            if (tile.Texture != null) DrawTile(batcher, tile);
        batcher.ClipEnd();
        var player = transform * new Vector2(world.Player.X + .5f, world.Player.Y + .5f);
        batcher.Draw(SolidColorTextureCache.GetTexture(Color.Black), new Rectangle((int)player.X - 4, (int)player.Y - 4, 9, 9), Vector3.Back, 0);
        batcher.Draw(SolidColorTextureCache.GetTexture(Color.White), new Rectangle((int)player.X - 2, (int)player.Y - 2, 5, 5), Vector3.Back, 0);
        batcher.End();
    }

    private static void DrawTile(UltimaBatcher2D batcher, OverviewMapCache.Tile tile) =>
        batcher.Draw(tile.Texture, new Rectangle(tile.Key.X * tile.Side, tile.Key.Y * tile.Side, tile.Side, tile.Side), Vector3.Back, 0);

    public void ReadBlock(int x, int y, int step, Span<uint> colors)
    {
        if (step == 1) { ReadNativeBlock(x, y, colors); return; }
        if (step >= 8)
        {
            int width = _maps.MapBlocksSize[_map, 0] * 8, height = _maps.MapBlocksSize[_map, 1] * 8;
            for (int py = 0; py < 8; py++)
            for (int px = 0; px < 8; px++)
            {
                int tx = x * 8 + px * step, ty = y * 8 + py * step;
                if (tx >= width || ty >= height) continue;
                tx = Math.Min(width - 1, tx + step / 2); ty = Math.Min(height - 1, ty + step / 2);
                Array.Clear(_sampleBlock);
                ReadNativeBlock(tx >> 3, ty >> 3, _sampleBlock);
                colors[py * 8 + px] = _sampleBlock[(ty & 7) * 8 + (tx & 7)];
            }
            return;
        }
        int sampleStride = Math.Min(step, 8), blocks = sampleStride;
        int blockStride = Math.Max(1, step / 8);
        int blockOffset = (step / 2) >> 3, cellOffset = (step / 2) & 7;
        for (int by = 0; by < blocks; by++)
        for (int bx = 0; bx < blocks; bx++)
        {
            Array.Clear(_sampleBlock);
            ReadNativeBlock(x + bx * blockStride + blockOffset, y + by * blockStride + blockOffset, _sampleBlock);
            for (int cy = cellOffset; cy < 8; cy += sampleStride)
            for (int cx = cellOffset; cx < 8; cx += sampleStride)
            {
                int px = bx * (8 / sampleStride) + cx / sampleStride;
                int py = by * (8 / sampleStride) + cy / sampleStride;
                colors[py * 8 + px] = _sampleBlock[cy * 8 + cx];
            }
        }
    }

    private unsafe void ReadNativeBlock(int x, int y, Span<uint> colors)
    {
        if (x < 0 || y < 0 || x >= _maps.MapBlocksSize[_map, 0] || y >= _maps.MapBlocksSize[_map, 1]) return;
        // UltimaLive normally refreshes indices only for rich chunks already loaded.
        // Overview reads distant blocks too; reload their current static index first.
        if (_maps is UltimaLive.ULMapLoader live) live.ReloadBlock(_map, x * _maps.MapBlocksSize[_map, 1] + y);
        ref var index = ref _maps.GetIndex(_map, x, y);
        if (!index.IsValid()) return;
        var hues = Client.Game.UO.FileManager.Hues;
        index.MapFile.Seek((long)index.MapAddress, System.IO.SeekOrigin.Begin);
        var block = index.MapFile.Read<MapBlock>();
        for (int cell = 0; cell < 64; cell++)
        {
            var land = block.Cells[cell];
            ushort original = (ushort)(land.TileID & 0x3fff);
            ushort graphic = ThemeManager.LandVariant(_world, x * 8 + cell % 8, y * 8 + cell / 8, original)
                ?? SeasonManager.GetLandSeasonGraphic(_world.Season, original);
            colors[cell] = HuesHelper.Color16To32(hues.GetRadarColorData(graphic)) | 0xff000000;
            _heights[cell] = land.Z;
        }
        if (index.StaticAddress == 0 || index.StaticCount == 0 || index.StaticFile == null) return;
        index.StaticFile.Seek((long)index.StaticAddress, System.IO.SeekOrigin.Begin);
        uint remaining = index.StaticCount;
        var data = Client.Game.UO.FileManager.TileData.StaticData;
        while (remaining != 0)
        {
            int count = (int)Math.Min((uint)_statics.Length, remaining);
            var span = _statics.AsSpan(0, count);
            index.StaticFile.Read(MemoryMarshal.AsBytes(span));
            foreach (ref var item in span)
            {
                if (item.X >= 8 || item.Y >= 8 || item.Color == 0 || item.Color >= data.Length) continue;
                ushort graphic = ThemeManager.StaticVariant(_world, x * 8 + item.X, y * 8 + item.Y, item.Color)
                    ?? SeasonManager.GetSeasonGraphic(_world.Season, item.Color);
                if (graphic >= data.Length) continue;
                ref var tile = ref data[graphic];
                // Footprints of structure survive; small decorative props do not.
                if (!KeepStatic(tile.IsWall, tile.IsRoof, tile.IsSurface, tile.Height)) continue;
                int cell = item.Y * 8 + item.X;
                int top = item.Z + tile.Height;
                if (top < _heights[cell]) continue;
                _heights[cell] = top;
                ushort color = item.Hue != 0 ? hues.GetColor16(16384, item.Hue) : hues.GetRadarColorData(graphic + 0x4000);
                colors[cell] = HuesHelper.Color16To32(color) | 0xff000000;
            }
            remaining -= (uint)count;
        }
    }
    internal static bool KeepStatic(bool wall, bool roof, bool surface, int height) => wall || roof || surface || height >= 16;
    public void Dispose() { _cache.Dispose(); _world = null; _maps = null; }
}
