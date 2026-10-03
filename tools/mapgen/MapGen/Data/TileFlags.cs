namespace CentrED.MapGen.Data;

// Hardcoded UO water-tile classification.
// Source: CentrED# CoastlineTool (tools/CentrED/CentrED/Tools/CoastlineTool.cs:56-57)
// and ModernUO TileFlag.Wet usage. We don't yet load tiledata.mul into MapGen, so
// these ranges are the cheap stand-in. Replace with TileDataProvider lookup once
// the miner needs full flag access.
public static class TileFlags
{
    // Land tile IDs that are water (flat-water rule applies).
    public static bool IsWaterLandId(ushort id)
    {
        return (id >= 0x00A8 && id <= 0x00AB)   // ocean
            || (id == 0x0136)                   // shallow
            || (id == 0x0137);                  // shallow
    }

    // Static tile IDs that are water (drop forest/road placement).
    public static bool IsWaterStaticId(ushort id)
    {
        return id == 0x1559
            || (id >= 0x1796 && id <= 0x17B2);   // water surface + shore/edge water pieces
    }
}
