# MapGen.Stamps — one loader, one placer

Every tool that reads or places stamps goes through this folder: the MapGen stamp
passes (Stamp Scatter, Road Stamps, Town Stamps), StampCompose.Cli, MapGenDump.Cli and
the CentrED editor (stamp tool, library window). Do not parse `*.stamp.json` or place
stamps anywhere else.

## Loading

```csharp
var lib = StampLoader.Load();                    // mined/stamps in the generator data folder, cached
var lib2 = StampLoader.Load(@"some/other/root"); // relative: cwd first, then repo root
IReadOnlyList<LoadedStamp> forests = lib.GetKind("forest");
IReadOnlyList<LoadedStamp> small = lib.GetKind("town_quarter", row => row.StaticCount <= 60);
LoadedStamp? one = lib.GetByPath("forest/forest-01234-00567.stamp.json") ?? lib.GetById("forest/forest-01234-00567");
StampLoader.InvalidateCache(root);               // after writing files under root
```

- **Cached** per root. The cache is reused until the file count, the newest file time
  or index.json changes.
- **index.json** (PascalCase `{GeneratedAt, Stamps[{Id,Kind,Subtype,Path,Width,Height,StaticCount,LandCount,Tags,Weight}]}`)
  is used when it matches the disk exactly. If it is stale, every file is parsed once and
  a warning is added to `lib.Warnings`.
- **Lazy parsing.** `GetKind` parses only that kind's files, in ordinal path order, so
  results are deterministic.
- **Errors.** Corrupt files go to `lib.Errors` and are skipped. A file whose `kind`
  disagrees with its index row is skipped with a warning.
- **Reserved folders.** Folders starting with `_` are never part of the library, at any
  depth: `_trash`, `_schema`, `_transitions`. Check a path with `StampLoader.IsReservedPath(rel)`.
- `StampLoader.LoadFile(path)` and `StampLoader.Parse(JsonElement)` read a single stamp,
  for example a file being edited.

### Z: v1 absolute, v2 relative

| file | meaning | loader |
|---|---|---|
| `version` 1, or `"z_mode": "absolute"` | z = absolute source-map Z | subtracts the reference Z |
| `version` >= 2, or `"z_mode": "relative"` | z relative to the anchor ground | as-is; `reference_z` is read into `SourceGroundZ` |

The reference Z is `StampLoader.ComputeReferenceZ`:
1. the land Z at the anchor cell (rx=0, ry=0);
2. else the lowest land Z;
3. else the lowest static Z;
4. else 0.

`LoadedStamp.LandZRel` and `StaticZRel` are always relative. Writers (the miner and
MakeStampWindow) emit `version: 2, z_mode: "relative", reference_z: <ground>`.
`MapMiner.Cli --migrate-z` rewrites v1 files; it is a dry run unless `--apply` is given.
Run it on a copy of the library, never on the library itself.

## Placing

```csharp
var opt = new StampPlaceOptions
{
    Grounding = StampGrounding.Rigid,   // Rigid: town/road (flat pad at the mean Z, land rewritten)
                                        // PerTile: forest (each piece follows the ground under it)
    PaintLand = false,                  // paint the stamp's land ids (Original variant only)
    AllowedBiomes = null,               // checked over the WHOLE footprint, not the centre
    MaxLocalSlope = 12, RejectWater = true, SeaLevelZ = ir.SeaLevelZ,
    UseOccupancy = true, ClearSoftStatics = true, BlendEdges = true, Source = "my-tool",
};

// Pure planning (preview, ghost, hit test). Mutates nothing.
StampPlan plan = StampPlacer.Plan(stamp, cx, cy, variant, new GenIrTerrain(ir), opt, biomeAt);
if (!plan.Ok) Console.WriteLine(plan.RejectReason);   // "water", "slope", "biome", "out of bounds", ...
// plan.Land / plan.Statics: absolute (X, Y, Z, Id[, Hue]); plan.Footprint; plan.BaseZ

// Placement into a GenIR (MapGen passes):
if (StampPlacer.TryPlace(ir, stamp, cx, cy, variant, opt, out var placed)) { /* ... */ }
StampPlacer.FinishPass(ir, note => report.Notes.Add(note)); // once per pass
```

To place into something other than a GenIR (the editor's live map), implement
`IStampTerrain { InBounds, LandZ, LandId }`, call `Plan`, and commit `plan.Land` and
`plan.Statics` yourself.

**Static Z rule.** A static goes on the final ground of its reference cell, plus its
height above the stamp's own land there. The reference cell is the anchor of its tree
cluster, or its own cell otherwise.
- Statics never float or sink when the ground differs from the source map.
- A whole tree moves together.
- `ZOffset` is added at the end.

**Variants.** `StampVariantPolicy.Resolve` returns `Original` for every kind except the
orientation-free ones (`forest`). Rotating or mirroring directional art (walls, roads,
buildings) produces broken pieces.
- `StampVariantPolicy.Pick(stamp, rng, useVariants)` picks a variant.
- `StampPlacer.StaticCell` maps a static through a variant.
- `StampPlacer.PickWeighted(stamps, rng)` makes a weighted pick.

## Occupancy

`OccupancyGrid.For(ir)` (stored on `GenIR.Occupancy`) is shared by every stamp and
scatter pass.

| flag | set by | meaning |
|---|---|---|
| `Soft` | tree / scatter passes | replaceable: a stamp may clear it |
| `Hard` | stamps, POI markers | nothing else goes here (`TryPlace` rejects with "overlap") |
| `LandDirty` | stamp land writes | land transitions must be re-blended |
| `Ground` | `RecordGround` | the land Z under a static at placement time |

- `QueueClearSoft` and `FlushClears` remove only the statics that were placed before
  the clear was queued.
- `Placements` records each stamp or group footprint and ops for the validators.

## Seams

`StampSeams.Reblend(ir)` passes the land-dirty mask to
`LandTransitionPass.RunOnMask(GenIR, bool[])` when that method exists (it is looked up
by reflection), then clears the mask. Until the terrain side provides it, the call adds
a note and leaves the mask alone.

## After placement: Static Resnap and checks

`Passes/Scatter/StaticResnapPass` ("Static Resnap") belongs right after "Map Validator".
In order, it:
1. flushes pending clears;
2. removes leftovers of partial scatter groups;
3. moves every recorded static by the change in land Z since it was placed;
4. drops exact duplicates;
5. reports sunk or floating statics and overlapping stamps.

StampCompose.Cli and MapGenDump.Cli insert it themselves. The rules are in
`StaticChecks.cs` (`DuplicateStaticRule`, `StaticLandZRule`, `OverlappingStampsRule`,
`PartialObjectRule`) and are plain `IMapRule`s.

## Determinism

Use `StableHash.Fnv1a64(string)`, `StableHash.Mix(h, v)` and `StableHash.Guid(name)`
wherever a seed or id is derived from text. `string.GetHashCode()` is randomised per
process.
