# ADR-0052: Property-backed source objects for native layout furnishings

## Status

Proposed. GUO lead reviewed the concrete contract and authorized isolated
implementation on 2026-11-15. Acceptance by the owner remains pending; this
review does not authorize merge or production adoption.

## Date

2026-11-15

## Decision Makers

GUO lead (technical review); Codex (contract and implementation proposal);
owner acceptance pending.

## Context

The PZ adapter currently classifies furniture by substrings from tile IDs and
names, then marks a 2x2 target area per source tile. Native grouping splits those
cells into connected fragments. Bathroom rugs become baths; objects split when
structural walls trim their target masks. Several source objects can also share
one source cell (a sink above a counter), while a single per-cell furniture field
can retain only one of them. Real fixtures must retain strict native fitting.

## Decision

Keep the existing offline Tools layer and native writers. Classify source tiles
before target cell expansion. Structural properties win: Wall*, collide*, door*,
DoorWall*, window*, solidfloor and stairs* cannot become fixtures. For the remaining
tiles require container, IsMoveAble, or a repeated nonempty GroupName in the same
tileset as object evidence. Match whole-word tokens in CustomName first, then
GroupName. Never classify from the tile identifier or tileset name. Explicit
rug/carpet/mat/towel/mirror/poster/curtain evidence denotes detail, not plumbing.
The explicit `MoveType=FloorRug` property also denotes floor detail. Existing
native role tokens cover named storage (drawers, wardrobe, bin), seating (ottoman),
tables (desk), lamps and plants. Property-backed names also map mattress to bed,
microwave to oven, stool to chair, freezer/box to container, display to display,
and radio/computer/terminal to the existing tools role. These are role mappings,
not claims of identical target art or successful native fitting. Switches, clocks
and televisions remain unresolved without a supported role; do not drop them as
detail or map them to generic art merely to clear a build. Ambiguous object evidence is unresolved, with
reasons; do not invent a role. Different source variants may reuse the same group
and facing; missing/variant-ambiguous sprite grids remain unresolved.

Assemble objects on the source grid before walls trim target masks. A multipart
object with SpriteGridPos uses its tileset, GroupName and source-minus-grid anchor
on one floor; incompatible facing, roles, duplicate offsets or missing pieces
remain diagnostic. Separate anchors cannot merge merely because they touch.
For groups without grid offsets, use cardinally adjacent source positions within
the same tileset/GroupName, retaining every source position and tile. Distinguish
separate stack entries; conflicts or unreconstructable fragments cannot silently
become several independently successful fixtures.

### Key Interfaces

Section 32 defines additive `furnishing_contract: pz-source-objects/1` and per-level
`furnishing_groups`. Each group retains id, source_id, role, status, source tile
observations, intended target cells, surviving floor cells and diagnostics.
`role` is a known existing native furnishing role or `unresolved`. Detail-only
observations are counted separately with evidence. Multiple groups may share
source/target cells; native fitting must resolve them independently without
silently dropping either. The old per-cell furniture fields remain room-label
hints, not an authoritative object inventory when this contract is present.
The durable catalogue furnishing_group rows retain each complete declared group,
including unresolved objects and stacked objects sharing cells. Repeated saves
verify that the retained inventory agrees exactly; stale legacy-derived rows
require a new converter profile and are never silently amended or accepted.

Native grouping consumes the complete group list, validates unique IDs/cell
references and rejects unresolved or empty-support objects. Layouts without the
extension keep their legacy CDDA/PZ path. New layouts require the matching native
consumer. The semantic hash includes the extension when present; historical
layouts retain their old hash algorithm. Converter/profile identities bind all
implementation modules. Do not reuse old cached semantic/native results.

Binary definitions prefer `media/tile_definitions` when present, otherwise
`media`, using the seven-file order documented in section 32. Non-patch files
replace whole records. Patch files merge properties into existing records and
retain the existing new-key behavior. Missing definitions fail explicitly.

### Generic example

A two-tile blue bed with offsets (0,0)/(1,0) remains one source object even when a
wall trims its projected floor mask. A neighboring bed with a different anchor
stays separate. A bathroom rug remains detail. A sink and a counter on the same
source cell both remain in the object inventory; failure to fit either is visible.

## Alternatives Considered

- Extend substring exclusions: smaller change, but still confuses names with
  structural/object evidence and leaves split/lost objects.
- Merge target fragments by role: easy to implement, but can join unrelated
  neighboring objects and cannot recover source stack entries that were lost.
- Drop unplaceable fixtures: rejected; it turns missing content into a false pass.

## Consequences

False plumbing classifications disappear and authored object identity survives
projection. More real ambiguity may become an explicit blocker. Additional source
metadata increases semantic JSON size. Grouping remains linear in observed tiles
plus cardinal adjacency; do not compare every source object with every other one.
No gameplay, renderer, tile ID allocation, native encoding or public source-data
redistribution changes are introduced.

## ADR Dependencies

No new client or engine dependency. Existing section16 multi and section9 world
writers remain unchanged. This does not broaden ADR-0022's proposed authoring
scope. Enables faithful source furnishing conversion; production adoption waits
for the contract review and acceptance below.

## Engine Compatibility

Godot 4.7.2 mono is pinned, but this change calls no engine API. Python offline
Tools only. References: local engine VERSION, breaking-changes and deprecated-apis;
none introduces an API dependency here. Runtime movement remains separate proof.

## GDD Requirements Addressed

Foundational authoring decision with no direct game-specific GDD requirement.
Enables faithful room/function preservation in imported native buildings.

## Migration Plan

Document the contract first. Keep implementation isolated on a feature branch.
Use new source profiles/build identities; preserve previous artifacts. Historical
layouts keep legacy grouping. Rollback selects the earlier converter/profile,
never edits an existing build or staged installation in place.

## Validation Criteria

- Original synthetic tdef fixtures: rugs/wall details are not baths; real
  sink/toilet/bath objects remain baths; missing evidence stays unresolved.
- Multipart beds/counters survive structural trimming and mixed names without
  joining separate object anchors. Stacked fixtures cannot disappear.
- A sink and counter on one source cell produce two source groups; each is
  fitted independently and reported, or retains its own explicit failure.
- Flat and nested definition directories have identical effective load order.
- Retained source house preserves five authored rooms, seven doors and eight
  windows; remaining material failures name genuine fixtures or source ambiguity.
- The small control retains 218 component records and two doors. Compare native
  component/sidecar bytes under the same theme; semantic/profile/build IDs must
  change when new semantics or converter implementation changes.
- Existing layout_import tests and full smoke pass before adoption. Native-world
  traversal is not inferred from source topology or an offline component build.

## Related

[Semantic layouts and native builds](../data_formats.md#32-semantic-layouts-and-native-layout-builds-toolslayout_import).
