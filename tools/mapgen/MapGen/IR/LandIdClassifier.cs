using CentrED.MapGen.Data;

namespace CentrED.MapGen.IR;

// Shared LandId classification predicates, so passes (ForestScatter, etc.) can refuse to
// place statics on coast/transition tiles without duplicating magic number ranges.
//
// Interior sets come from TileTables.BuiltIn (the canonical tables; tile-tables.default.json
// is asserted identical by the tests). The ranges below cover the edge/blend tiles the
// tables do not list; names checked against tiledata.mul.
public static class LandIdClassifier
{
    // Sand: 0x16-0x19 walkable interior (the Beach/Desert table) plus 0x1A-0x1D, the
    // sloped/edge sand variants (all named "sand" in tiledata).
    public static bool IsBeach(ushort id) => id >= 0x16 && id <= 0x1D;

    // Dirt: 0x71-0x78 is the road/trail interior (RoadPaint.DirtTiles); 0x16E-0x172 patchy
    // grass-dirt and 0x9E-0xA7 dirt slopes (both named "dirt").
    public static bool IsDirtOrScrub(ushort id) =>
        (id >= 0x71 && id <= 0x78) ||
        (id >= 0x16E && id <= 0x172) ||
        (id >= 0x9E && id <= 0xA7);

    // Convenience: any transition tile a forest tree should not stand on.
    public static bool IsBeachOrTransition(ushort id) => IsBeach(id) || IsDirtOrScrub(id);

    // Plain grass interior: the Grassland table (0x03-0x06). 0x07/0x08 are unused ids and
    // 0x09-0x15 are furrows (farmland), not grass.
    public static bool IsGrass(ushort id) => InTable(BiomeId.Grassland, id);

    // Forest floor: the Forest table (0xC4-0xC7) plus the 0xC8-0xCB forest/grass blend
    // tiles that Felucca uses inside woods.
    public static bool IsForestFloor(ushort id) => InTable(BiomeId.Forest, id) || (id >= 0xC8 && id <= 0xCB);

    // Jungle floor: the Jungle table (0xAC-0xAF).
    public static bool IsJungle(ushort id) => InTable(BiomeId.Jungle, id);

    private static bool InTable(BiomeId biome, ushort id)
        => TileTables.BuiltIn.Land.TryGetValue(biome, out var set) && Array.IndexOf(set, id) >= 0;
}
