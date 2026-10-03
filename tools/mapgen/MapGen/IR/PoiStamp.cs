using CentrED.Network;

namespace CentrED.MapGen.IR;

public enum PoiKind : byte
{
    ControlPoint = 0,
    DungeonEntrance = 1,
    Ruin = 2,
    Town = 3,
    Camp = 4,
    Shrine = 5,
}

// Single point of interest in the world. Most kinds are point-only (a single X/Y/Z
// with no extent), but Town carries an axis-aligned Footprint rectangle and a small
// set of Gates — boundary cells where inter-town roads are allowed to terminate.
//
// Footprint + Gates are null for legacy point POIs and are only populated by
// TownSiteFinderPass. The downstream RoadGraphPass + TownRoadPass treat null
// Footprint as "this POI is a single cell" — same as before.
public sealed record PoiStamp(
    int Id,
    PoiKind Kind,
    ushort X,
    ushort Y,
    sbyte Z,
    string? QuadrantId = null,
    string? Tag = null,
    RectU16? Footprint = null,
    IReadOnlyList<(ushort X, ushort Y)>? Gates = null);
