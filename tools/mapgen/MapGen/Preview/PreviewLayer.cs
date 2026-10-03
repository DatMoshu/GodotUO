namespace CentrED.MapGen.Preview;

[Flags]
public enum PreviewLayer
{
    None = 0,
    Height = 1 << 0,
    Moisture = 1 << 1,
    Temperature = 1 << 2,
    Biome = 1 << 3,
    LandIdRadar = 1 << 4,   // hooks RadarMap colour table when available
    Statics = 1 << 5,
    Pois = 1 << 6,
    QuadrantGrid = 1 << 7,
    Slope = 1 << 8,
    Roads = 1 << 9,
    Rivers = 1 << 10,
    Mountains = 1 << 11,    // mountain biome mask only
    LandId = 1 << 12,       // tile-id range fallback (no radar palette dependency)
}
