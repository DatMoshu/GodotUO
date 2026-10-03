namespace CentrED.MapGen.IR;

// Single byte (max 256). Order is stable; values are persisted in IR snapshots.
public enum BiomeId : byte
{
    Unassigned = 0,
    DeepWater = 1,
    ShallowWater = 2,
    Beach = 3,
    Grassland = 4,
    Forest = 5,
    DenseForest = 6,
    Jungle = 7,
    Savanna = 8,
    Desert = 9,
    Tundra = 10,
    Snow = 11,
    Mountain = 12,
    HighMountain = 13,
    Swamp = 14,
    Wetland = 15,
    Lava = 16,
    Cave = 17,
    Road = 18,
    River = 19,
    CaveWall = 20,
}
