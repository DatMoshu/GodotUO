#if TOOLS
namespace GUO.Editor;

using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Numerics;
using System.Text.Json;
using System.Text.Json.Serialization;

// The JSON value of CentrED#'s land_brushes profile setting.
internal sealed class WorldCoastPair
{
    internal const string Operation = "Coast edges";
    internal sealed record Edge([property: JsonRequired] ushort TileID, [property: JsonRequired] byte Direction);
    internal sealed record Material([property: JsonRequired] ushort[] Tiles,
        [property: JsonRequired] Dictionary<string, Edge[]> To);
    private static readonly JsonSerializerOptions Strict = new() { UnmappedMemberHandling = JsonUnmappedMemberHandling.Disallow };
    private readonly string _fromName, _toName;
    private readonly ushort[] _from, _to;
    private HashSet<ushort> _source, _destination;
    private readonly Edge[] _edges;
    private ushort[][] _matches;
    internal string Label => $"{_fromName} → {_toName}";
    internal ushort BaseTile => _from[0];
    // Keep the selected pair in the preset so moving the original file is safe.
    internal string PresetJson => JsonSerializer.Serialize(new Dictionary<string, Material>
    {
        [_fromName] = new(_from, new() { [_toName] = _edges }), [_toName] = new(_to, new())
    });
    private WorldCoastPair(string source, string destination, ushort[] from, ushort[] to, Edge[] edges)
    {
        _fromName = source;
        _toName = destination;
        _from = from;
        _to = to;
        _edges = edges;
    }

    internal static List<WorldCoastPair> Read(string json)
    {
        var materials = JsonSerializer.Deserialize<Dictionary<string, Material>>(json, Strict);
        if (materials == null || materials.Any(m => string.IsNullOrWhiteSpace(m.Key) || m.Value?.Tiles == null || m.Value.To == null))
            throw new InvalidDataException("Choose a CentrED# land_brushes dictionary with Tiles and To.");
        var pairs = new List<WorldCoastPair>();
        foreach (var (source, material) in materials)
        foreach (var (destination, edges) in material.To)
        {
            if (edges == null || edges.Any(e => e == null || e.Direction == 0))
                throw new InvalidDataException($"Check the edge directions for {source} → {destination}.");
            if (edges.Length == 0) continue;
            if (source == destination || !materials.TryGetValue(destination, out var other) ||
                material.Tiles.Length == 0 || other.Tiles.Length == 0)
                throw new InvalidDataException($"Both materials in {source} → {destination} need distinct names and base tiles.");
            pairs.Add(new(source, destination, material.Tiles, other.Tiles, edges));
        }
        if (pairs.Count == 0) throw new InvalidDataException("The dictionary has no directional pairs.");
        return pairs;
    }

    internal string Check(EditorData data)
    {
        _source ??= _from.Concat(_edges.Select(e => e.TileID)).ToHashSet();
        _destination ??= _to.ToHashSet();
        _matches ??= new ushort[256][];
        if (_source.Overlaps(_destination)) return $"{Label} shares tile IDs across shores. Choose a distinct pair.";
        foreach (ushort id in _source.Concat(_destination))
            if (id >= EditorData.LandCount || data?.HasArt(id) != true) return $"{Label} needs unavailable land art 0x{id:X4}.";
        return null;
    }

    private static readonly (int X, int Y)[] Compass = { (0,-1), (1,-1), (1,0), (1,1), (0,1), (-1,1), (-1,0), (-1,-1) };
    internal ushort Resolve(WorldData data, int x, int y, ushort current, uint hash)
    {
        if (!_source.Contains(current)) return current;
        int mask = 0;
        for (int bit = 0; bit < Compass.Length; bit++)
        {
            int nx = x + Compass[bit].X, ny = y + Compass[bit].Y;
            if (nx >= 0 && ny >= 0 && data.Block(nx >> 3, ny >> 3) is { } block &&
                _destination.Contains(block.LandId[(ny & 7) * 8 + (nx & 7)])) mask |= 1 << bit;
        }
        if (mask == 0) return current;
        if (_matches[mask] == null)
        {
            var best = _edges.Where(e => (e.Direction & mask) == mask)
                .GroupBy(e => BitOperations.PopCount((uint)e.Direction)).MinBy(g => g.Key);
            _matches[mask] = best?.Select(e => e.TileID).ToArray() ?? Array.Empty<ushort>();
        }
        var candidates = _matches[mask];
        return candidates.Length == 0 ? current : candidates[hash % (uint)candidates.Length];
    }
}
#endif
