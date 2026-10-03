namespace CentrED.MapGen.IR;

// Bitset of which dense IR fields a pass reads or writes. Used by the runner to
// (a) validate a toggleable pipeline (no downstream pass reads a field nobody wrote),
// (b) drive incremental re-run caching.
[Flags]
public enum IrFields : uint
{
    None = 0,
    Height = 1 << 0,
    Moisture = 1 << 1,
    Temperature = 1 << 2,
    Slope = 1 << 3,
    Biome = 1 << 4,
    LandId = 1 << 5,
    StaticOps = 1 << 6,
    Rivers = 1 << 7,
    Roads = 1 << 8,
    Pois = 1 << 9,
    DungeonZone = 1 << 10,
}
