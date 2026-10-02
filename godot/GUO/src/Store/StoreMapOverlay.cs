// SPDX-License-Identifier: BSD-2-Clause
using System;
using System.Collections.Generic;
using System.IO;
using System.Text.Json;
using GUO.Assets;
using GUO.IO;

namespace GUO.Store;

/// <summary>Authored map blocks compiled into a private, delete-on-close reader.</summary>
internal sealed class StoreMapOverlay : IDisposable
{
    private sealed record Block(int Map, int X, int Y, byte[] Land, byte[] Statics);
    private readonly List<Block> _blocks = new();
    private readonly Dictionary<(int, int, int), IndexMap> _compiled = new();
    private readonly HashSet<(int, int, int)> _coordinates = new();
    private MMFileReader _reader;
    private long _bytes;

    public void Add(int map, byte[] data, MapLoader maps)
    {
        StorePack.Require(map >= 0 && map < MapLoader.MAPS_COUNT, "Unknown map facet");
        using var doc = JsonDocument.Parse(data);
        StoreMapDefinition.Validate(doc.RootElement);
        var blocks = doc.RootElement.GetProperty("blocks");
        StorePack.Require(blocks.GetArrayLength() is > 0 and <= 65536, "Invalid authored map block count");
        foreach (var row in blocks.EnumerateArray())
        {
            int x = row.GetProperty("x").GetInt32(), y = row.GetProperty("y").GetInt32();
            StorePack.Require(x >= 0 && y >= 0 && x < maps.MapsDefaultSize[map, 0] / 8 && y < maps.MapsDefaultSize[map, 1] / 8, "Map block outside facet");
            StorePack.Require(_coordinates.Add((map, x, y)), "Conflicting map blocks");
            var cells = row.GetProperty("land");
            StorePack.Require(cells.GetArrayLength() == 64, "Map block needs 64 row-major land cells");
            using var land = new MemoryStream();
            using (var writer = new BinaryWriter(land, System.Text.Encoding.UTF8, true))
            {
                writer.Write(0u);
                foreach (var cell in cells.EnumerateArray())
                {
                    ushort graphic = cell.GetProperty("graphic").GetUInt16();
                    StorePack.Require(graphic <= 0x3fff, "Invalid land graphic");
                    writer.Write(graphic); writer.Write(cell.GetProperty("z").GetSByte());
                }
            }
            byte[] statics = null;
            if (row.TryGetProperty("statics", out var rows))
            {
                StorePack.Require(rows.GetArrayLength() <= 1024, "Too many map statics");
                using var buffer = new MemoryStream();
                using (var writer = new BinaryWriter(buffer, System.Text.Encoding.UTF8, true))
                    foreach (var item in rows.EnumerateArray())
                    {
                        byte sx = item.GetProperty("x").GetByte(), sy = item.GetProperty("y").GetByte();
                        ushort graphic = item.GetProperty("graphic").GetUInt16(), hue = item.GetProperty("hue").GetUInt16();
                        StorePack.Require(sx < 8 && sy < 8 && graphic is > 0 and < 0xffff && hue <= 0x3fff, "Invalid map static");
                        writer.Write(graphic); writer.Write(sx); writer.Write(sy);
                        writer.Write(item.GetProperty("z").GetSByte()); writer.Write(hue);
                    }
                statics = buffer.ToArray();
            }
            _bytes += land.Length + (statics?.Length ?? 0);
            StorePack.Require(_bytes <= 64 * 1024 * 1024 && _blocks.Count < 65536, "Authored map memory budget exceeded");
            _blocks.Add(new Block(map, x, y, land.ToArray(), statics));
        }
    }

    public void Compile()
    {
        if (_blocks.Count == 0) return;
        var stream = new FileStream(Path.Combine(Path.GetTempPath(), "guo-content-map-" + Guid.NewGuid().ToString("N")),
            FileMode.CreateNew, FileAccess.ReadWrite, FileShare.Read, 4096, FileOptions.DeleteOnClose);
        try
        {
            stream.WriteByte(0); // Static address zero means absent to existing consumers.
            foreach (var block in _blocks)
            {
                var index = new IndexMap { MapAddress = (ulong)stream.Position };
                stream.Write(block.Land);
                if (block.Statics != null)
                {
                    index.StaticAddress = (ulong)stream.Position;
                    index.StaticCount = (uint)block.Statics.Length / 7;
                    stream.Write(block.Statics);
                }
                _compiled.Add((block.Map, block.X, block.Y), index);
            }
            stream.Flush(); stream.Position = 0;
            _reader = new MMFileReader(stream);
        }
        catch { stream.Dispose(); throw; }
    }

    public void Apply(MapLoader maps, int map)
    {
        if (_reader == null || maps.BlockData[map] == null) return;
        foreach (var block in _blocks)
        {
            if (block.Map != map) continue;
            var compiled = _compiled[(map, block.X, block.Y)];
            ref var index = ref maps.BlockData[map][block.X * maps.MapBlocksSize[map, 1] + block.Y];
            index.MapFile = _reader; index.MapAddress = compiled.MapAddress;
            index.OriginalMapAddress = compiled.MapAddress;
            if (block.Statics != null)
            {
                index.StaticFile = _reader; index.StaticAddress = compiled.StaticAddress;
                index.StaticCount = compiled.StaticCount; index.OriginalStaticAddress = compiled.StaticAddress;
                index.OriginalStaticCount = compiled.StaticCount;
            }
        }
    }

    public void Dispose() { _reader?.Dispose(); _reader = null; }
}
