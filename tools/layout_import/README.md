# CDDA / Project Zomboid layouts → native UO

Optional authoring pipeline for CDDA streets/parcels and local Zomboid building
interiors. Source geometry is converted through shared semantic layouts into
native UO materials, furniture groups, functional doors, multis and world map
patches. The tool is independent of the GUO runtime and uses canonical native
writers. Generated data can belong to a GUO pack or a private server project.

```powershell
python tools/layout_import/test_layout_import.py
python tools/layout_import/run.py scan --source <CDDA_CHECKOUT> --db build/layout_import/catalogue.sqlite
python tools/layout_import/run.py dependencies --db build/layout_import/catalogue.sqlite --snapshot <HASH> --mods <MOD_ID> <MOD_ID>
python tools/layout_import/run.py coverage --db build/layout_import/catalogue.sqlite --profile <HASH> --out build/layout_import/coverage.json
```

`scan` reports both the snapshot and default core profile IDs. `--mods` accepts
ordered MOD_INFO IDs, not directory names. Dependencies precede their requesting
mods, with core `dda` first. Missing or duplicate mod metadata, dependency cycles,
and JSON parse errors block the profile (exit 1). Bad arguments/output failures
exit 2. A successful census exits 0 even when individual layouts are blocked:
read the coverage ledger before selecting work.

The scanner reads every top-level typed record under `data/json`, `data/mods`
and top-level `mods`. It preserves all types, including non-building definitions.
It records every file hash and parse failure, JSON pointer, namespace, selector,
raw record, canonical record hash and authored weight. Implementation/docs hashes
and the source license are part of the snapshot identity. A caller-supplied
`--version-claim` is explicitly unverified; no Git cleanliness is asserted.
Repeat scans insert no duplicates; changed content creates a new snapshot and
preserves old records. SQLite migrations use `user_version`, transactions and
foreign keys. The old handoff database is never modified or migrated implicitly.

Literal palette, inheritance, nested chunk and predecessor references get an
indexed candidate set for each profile. All duplicate IDs and variants survive;
load-order ranks do **not** select a winning override. Dynamic expressions and
missing literals have explicit diagnostics. Candidate cycles are flagged. Other
reference kinds (terrain/furniture values, embedded mapgen, linked OMT groups,
updates and parameter expansions) still need adapters. The ledger covers every
indexed definition, with unselected records excluded and mapgen eligibility
explicitly unclassified. It is not an exhaustive semantic dependency resolver.

Unicode row lengths are codepoint observations only. CDDA uses display-width
checks; these columns must never drive geometry conversion. No symbols are
assumed to mean wall/door. No rooms or building counts are inferred from filenames.

The catalogue is separate from `tools/decorate/db.py`, whose `mine` rebuilds its
database. Semantic rooms reuse `tools/decorate/rooms.py`; durable templates,
source provenance, native builds, validation evidence and usage stay here.
No CDDA ID is presented as a UO item ID. Generated databases, raw imported records
and previews stay in ignored `build/` or explicitly chosen private output paths.
Source checkouts and retail installations are read only. Staging writes isolated
native output, and gameplay proof uses this checkout's owned private shard.
Keep source attribution when deriving layouts; theme renaming does not
erase provenance. Distribution of imported layouts remains a separate decision.

## Engine bake and semantic geometry

```powershell
python tools/layout_import/run.py engine-bake --db <DB> --profile <PROFILE> --source <CDDA_CHECKOUT> --binary <HEADLESS_EXE> --seed <SEED> --bounds <X0> <Y0> <X1> <Y1> --out build/layout_import/engine
python tools/layout_import/run.py resolve --db <DB> --profile <PROFILE> --bake build/layout_import/engine/bake --bounds <X0> <Y0> <X1> <Y1> --levels 0 1 --name <NAME> --out build/layout_import/resolved
```

Geometry comes from the inspected `cdda-submap-export` 1.0.0 exporter. The
wrapper probes capabilities, verifies source file hashes against the selected
snapshot, hashes the binary, uses an isolated user directory and records seed,
mods and bounds. Reuse requires identical inputs. Binary/source correspondence
is explicitly unverified without a reproducible build. Use the supplied levels,
including basements and roofs. Missing levels, invalid RLE/indices, duplicate
OMTs, escaped paths and inconsistent seed/mod inputs fail. Multi-OMT cells retain
their seams. The exporter does not provide a selected mapgen/parameter trace;
sampled bakes do not prove exhaustive definition or variant coverage.

## Zomboid authored interiors

```powershell
python tools/layout_import/run.py zomboid-scan --source <PZ_INSTALL> --map "Muldraugh, KY" --out build/layout_import/zomboid
python tools/layout_import/run.py zomboid-resolve --source <PZ_INSTALL> --map "Muldraugh, KY" --header <CELL>.lotheader --building <ID> --name <NAME> --db <DB> --out build/layout_import/pz_resolved
```

The local B42 reader indexes authored room rectangles/names, building membership
and levels, then reads lotpack stacks and core tile properties in load order.
Its format contract was checked against installed game readers. Unknown versions,
bad references and missing tile metadata fail. Supported maps have 8×8 chunks
and 256×256 cells; older 10×10 chunk maps are not supported.

An explicit 2:1 grid adaptation converts walls between tiles into shared cell
geometry, with one native opening per door/window edge. Authored room masks,
names, functional zones, source properties and coordinates survive. Connected
multipart furniture shares semantic groups. A flat UO roof over the authored
footprint is a recorded adaptation. Detail tiles without functional mappings
remain counted in `source_coverage`. Authored B/M/T stair triplets connect floors;
actual upper floor holes survive, and UO flights reserve the landing and preserve
adjacent walls. Any landing fill is recorded. Cross-cell buildings, incomplete
triplets and connections beyond supplied authored floors block approval.
No source artwork is read, copied or included in generated UO archives.

## Native buildings, districts and hybrid selection

Mine UO families with `python tools/multi/run.py mine --no-statics`, and run the
existing decor miner before building. Configuration supplies the UO installation.

```powershell
python tools/layout_import/run.py build --db <DB> --profile <PROFILE> --layout <LAYOUT.json> --catalogue build/multi/catalogue --decor-db build/decorate/decor.sqlite --out build/multi/built/<NAME>
python tools/layout_import/run.py district --db <DB> --profile <CDDA_PROFILE> --bake <BAKE_DIR> --bounds <X0> <Y0> <X1> <Y1> --origin <UO_X> <UO_Y> --name <NAME> --catalogue build/multi/catalogue --decor-db build/decorate/decor.sqlite --out build/layout_import/districts/<NAME>
python tools/layout_import/run.py hybrid --district <VALID_DISTRICT> --source <PZ_INSTALL> --zomboid-catalogue build/layout_import/zomboid/catalogue.json --db <DB> --catalogue build/multi/catalogue --decor-db build/decorate/decor.sqlite --seed <SEED> --count 1 --name <NAME> --out build/layout_import/districts/<NAME>
```

Default `uo-classic` uses stone walls, wooden floors, slate roofs, stone steps
and learned UO decor. `--theme <JSON>` selects a versioned material/role profile.
Source locked doors become functional unlocked pilot doors with their adaptation
recorded. Actual art, tiledata flags/heights, room circulation and furnishings
determine validity. Unplaceable interior groups fail. Outdoor pending mappings
belong to district composition, not completed interior claims.

CDDA stairs become full UO flights with reserved landings/headroom and recorded
relocation or partition cuts. Basements require `excavate_basements:true` and a
world project; a multi cannot replace solid land. `build --scene` partitions large
footprints into disjoint multis within shard capacity and visibility limits.
Partitioning does not bypass geometry checks; impossible stairs remain blocked.

Districts export block-aligned native terrain/statics/excavation alongside multis
and real doors. Street sockets, public frontage and actual target-height routes
are checked. `hybrid` deterministically selects eligible PZ houses for vacant
CDDA field parcels, tries at most eight candidates per parcel and records both
accepted and rejected attempts. `combine --district <DIR> --building <DIR>
--offset X Y --name <NAME> --out <DIR>` supports explicit validated placement.
The default seeded pool contains ground-floor houses; `hybrid --max-storeys 2`
also admits houses with contiguous authored ground and upper floors. Each
candidate still has to pass native staircase, footprint and movement checks.

## Staging, editor and gameplay evidence

On Windows, repeat the unchanged full GUO smoke and editor reload checks with:

```powershell
python tools/layout_import/run.py acceptance --out build/layout_import/acceptance/<NEW_RUN> --repeats 2
```

Use a new output directory to preserve earlier reports. Each child process gets
`UseSharedCompilation=false`: otherwise Godot's [Windows console wrapper](https://github.com/godotengine/godot/blob/ed1daf0bf/platform/windows/console_wrapper_windows.cpp#L150) can keep waiting
for a persistent Roslyn compiler server after the engine has exited. The setting
does not change the caller's environment or shared configuration. Each smoke
exit and report is retained; abnormal exits remain failures. This command does
not replace or replay the existing district gameplay proof.

```powershell
python tools/layout_import/run.py district-stage --built <DISTRICT_DIR> --stage build/uodata/<NAME>
python tools/layout_import/run.py district-prove --built <DISTRICT_DIR> --stage build/uodata/<NAME> --out build/layout_import/proof/<NAME> --clip build/layout_import/proof/<NAME>/walk.mp4
python tools/layout_import/run.py evidence --db <DB> --build <BUILD_ID> --kind gameplay --report <REPORT.json> --artifacts <FRAME.png> <CLIP.mp4>
```

Canonical multi/world/uodata writers allocate IDs, encode and verify readback,
including unchanged retail files. New component bytes need a new name/stage;
writers append instead of overwriting an existing allocation. Idempotent staging
preserves both world and multi override layers.

Proof uses this checkout's private shard on nonshared ports with a free-memory
check, loads world plus multis, places real doors and walks short paths through
rooms. Reports retain actual x/y/z, screenshots, video and a negative wall target.
`--only-part <NAME>` scopes the walked tour to one part while loading the scene;
the report records this scope and cannot claim a whole-district walkthrough.
Teleporting a room stop cannot count as proof. Per-map shard names avoid stale
cached terrain. The proof stops its shard/client and removes only its new cache.

Editor inspection uses the pinned `godot-console --headless --editor --path
<PROJECT> -- --guo-editor-smoke <OUT> --guo-editor-data-stage <STAGE>
--guo-editor-multi-inspect <ID> --guo-editor-multi-max-z <Z>`. This scoped check
loads the archive through GUO and writes full/cut composites; it is separate from
the full editor suite.

SQLite schema 3 stores immutable snapshots/profiles, semantic templates and
instances, levels/cells/rooms/openings/groups, themes, builds, mappings, hashed
artifacts and validation evidence. `usage --db <DB> --event <JSON>` accepts
`id`, `build`, `project`, `region`, `placement`, `state` (`placed`, `removed`,
`superseded`) and `timestamp`; events are immutable and idempotent.

Source gameplay extras (loot, traps, spawns, vehicles and fields) are retained
without conversion to shard mechanics. Successful native pilots prove selected
layouts; they do not imply that all source objects/buildings/variants are converted.
Source attribution remains required, and this tool grants no redistribution
rights to proprietary source layouts or UO artwork.

## Licensing of sources (read before publishing anything)

- Cataclysm: DDA content is CC-BY-SA 3.0. The importer reads the user's own
  checkout at runtime (`--source`); no CDDA data is committed. Layouts derived
  from it carry attribution and share-alike obligations, so keep them in ignored
  `build/` and do not publish them without meeting those terms.
- Project Zomboid content is proprietary. The importer reads the user's own
  install at runtime only. Nothing derived from its files (maps, building
  definitions, tile names) may be committed, published or redistributed.
- The tests use synthetic, hand-built fixtures only.
