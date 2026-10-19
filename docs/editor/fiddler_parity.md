# UOFiddler parity: a workbench for UO modders

Phase 1 proposal, 2026-10-19. Awaiting guo-director and exec-fable review.
No implementation, dispatch, sprint edits, publication or licence approval is implied.

GUO can already help you find art, replace a picture in a project, edit a multi,
and preview a world. It cannot yet replace Fiddler for all client-data work.
The biggest remaining jobs are safe replacement of existing records, editable
tiledata, sounds and strings, and the smaller specialist file editors.

## Catalogue and scope

The [feature catalogue](fiddler_parity.csv) has **654 rows**: 59 done in source,
194 partial, 396 missing, and 5 out of scope. These are action counts, **not a
completion percentage**: exporting another image format is much smaller than
writing a map-maintenance engine. One row represents one user operation or
compatibility format, not each button, event handler or internal helper.
Shared controls with the same effect count once. Individual RGB channels,
facet names and flag bits are parameters of an operation, not separate features.

Coverage includes the Start/settings shell, all 21 data pages, five workflow
plugins, the example plugin, the animation editing dialog, and a local
fork's packed-item exchange. Compare's ten domains have separate groups.
The CSV contains source anchors, format names, GUO evidence, limitations,
proposed story families and inspection provenance. Filter `page` to review a
page, `status` to find gaps, or `story` to collect a proposed work package.

Status has a deliberately narrow meaning:

| Status | Meaning in this research |
|---|---|
| done | A corresponding operation is implemented in the inspected GUO source. No new runtime verification or claim of exact Fiddler behavior. |
| partial | A related reader, overlay, tool or editor action exists, but format, scope, save behavior or workflow differs. It may still require Fiddler. |
| missing | No corresponding authoring operation was found in the inspected editor/tool contracts. A runtime loader or Store type alone does not satisfy this. |
| out of scope | Deliberately excluded behavior, with the reason recorded. |

The GUO baseline is commit `96ce0ff25a459584d9d2ca6c3259c68340013331`.
This branch does not contain other lanes' unmerged work. Review against their
latest branches before assigning a story, especially the editor and Store lanes.

### Reference versions and licence

The main source audit uses UOFiddler commit
`2b9e492cd0e52986eaff831e0e828576cad68708`. The local UOWW fork was also
checked at `c35956e5323827130c9d5d0044772a1360847792`; its Items page adds
range-to-sheet export, a numbered guide and sheet reimport. GUO's atlas and
bulk folders do not implement that fork's JSON contract.

The live repository identifies **4.24.0**, released September 13, 2026, at
`610c930833dac8d158f08a0af36da43dd90d08b4`, as its latest release.
The CSV marks its additional operations separately from the local source audit.
Those rows come from the release/PR description, not a full audit of the new
source. The old defrag rows remain as migration history; they are superseded by
the newer options/report workflow. [Release](https://github.com/polserver/UOFiddler/releases/tag/4.24.0),
[repository](https://github.com/polserver/UOFiddler).

The current additions affect clipboard images, bulk tiledata changes, map
maintenance, region handling and save-container selection. Implement against
the newer contracts when taking those stories. A source-level refresh of those
areas is an acceptance prerequisite, not a reason to reproduce the older UI.
[Upstream change](https://github.com/polserver/UOFiddler/pull/179).

UOFiddler's README describes a Beerware licence; inspected controls also carry
a notice requiring its retention. No root LICENSE file was found in the pinned
snapshot. This is source provenance, not blanket clearance for every dependency
or file. **No UOFiddler code is copied by this proposal.** Prefer GUO-owned
implementations using its loaders. Any proposed code reuse needs file-specific
notice/dependency review and director agreement before the owner decides it.
[Upstream README](https://github.com/polserver/UOFiddler#about).

## What a modder can do today

All addon paths below are relative to `godot/GUO/addons/guo_editor/`.
The CSV uses the same shorthand; `tools/` paths are repository-relative.

| Fiddler work | GUO implementation inspected | Useful today / remaining limit |
|---|---|---|
| Find item or land art | `Panels/ArtPanel.cs`, `Panels/GridPanel.cs`, `Search/Providers/UoProviders.cs` | ID/name search, pixel preview and tiledata inspection; no full metadata editing panel. |
| Replace art or gumps | `Panels/AssetActions.cs`, `Overlay/AssetOverlay.cs`, `Art/ArtExchange.cs` | PNG import/export, external image editor exchange and revert in an overlay; not arbitrary deletion or complete MUL/UOP save parity. |
| Batch asset exchange | `Panels/BulkPanel.cs`, `tools/uopack/run.py` | Art/land/gumps/animation/item-tiledata PNG+JSON workflows and stage verification; Fiddler image/CSV/XML matrices differ. |
| Preview/edit hue strips | `Panels/HuePanel.cs`, `Panels/AssetActions.cs:AddHue` | Full/partial hue preview and strip replacement; no complete palette-design UI or Fiddler text exchange. |
| Edit animation working copy | `Animation/AnimationWorkspace.cs`, `Animation/AnimationDocument.cs`, `Panels/AnimationPanel.cs` | Frame centres, playback, sequence/sheet PNG, palette JSON, classic VD and overlay application exist. GIF, arbitrary frame operations, bulk directions and complete save workflow differ. |
| Build/edit a multi | `MultiEdit/MultiEditView.cs`, `MultiDocument.cs`, `MultiCanvas.cs`, `MultiValidator.cs` | Draw/select/erase/line/rectangle, stories, visibility, movement, undo/redo, validation and reserved-ID staging already exist. Certify compatibility; do not rebuild the editor. |
| Exchange multis | `MultiEdit/MultiEditView.Formats.cs`, `tools/multi` | TXT, UOA text/binary, WSC, CSV variants, CentrED and UOX3 are exposed. XML and batch preview export remain gaps. |
| Inspect terrain/statics | `Panels/MapPanel.cs`, `Panels/RadarView.cs`, `World/WorldEditor.cs`, `Overlay/WorldProject.cs` | Facets, coordinates, cell stacks, world editing and project overlays; not every external-dataset or maintenance operation. |
| Export a world project | `tools/world/run.py:export,verify` | Copies/patches map/statics, asset verdata, hues, texmaps and static tiledata. Does not provide all Fiddler page controls. |
| Replace textures | `tools/world/run.py:export_texmaps,verify_texmaps` | PNG texmaps can be exported into copied MUL/IDX; no texture browser/editor. `Panels/ArtSetPanel.cs` extracts texture/light atlases, which is a different output. |
| Find strings/keywords | `Panels/ClilocPanel.cs` | Installed-language strings, ID/text search and phrase-to-speech-keyword matching. No authoring or full speech-table editor. |
| Hear effects/music | `Panels/SoundPanel.cs` | Browse/search/play/stop through game audio; no native sound authoring/export workflow. Recorded voice packs are a different feature. |
| Compare reference decode | `Panels/ParityPanel.cs`, `tools/guoasset` | One install through two decoders. This does **not** compare two client datasets or copy their differences. |
| Package/share authored content | `tools/asset_store/content.py`, editor `Store/` | Typed L2 content envelope exists. Declaring font/light/sound/translation types is not proof their Fiddler editing workflow works. Install remains inert. |

Read [editor plan](../editor_plan.md) and [AI workspace plan](../editor_ai_workspace_plan.md)
as architecture/context, not evidence that planned features shipped.

### File-writing constraints found in code

`tools/guo/uorecord.py:KINDS` currently admits static, land, gump, anim,
tiledata-item, hue and multi. `tools/uodata_write/uodata.py` appends supported
records into a stage, checks ranges and verifies copied originals remain
unchanged. Existing UOP hashes are refused. MUL/IDX replacement is explicit.
Its item-tiledata writer targets the modern layout; its fields do not match
every Fiddler field one for one. Preserve unknown bytes instead of mapping a
field by its label alone.

`tools/world` provides another existing export path: texture replacement and
static metadata written to copied files, and art/gump replacement via verdata.
Integrate these contracts rather than silently adding another writer. A
verdata overlay, a newly allocated ID, replacement of a used record, and full
container conversion are four distinct operations.

[ADR-0022](../architecture/ADR-0022-authoring-uo-data-files.md) is marked
**Proposed in this checkout**. Its existence and code do not settle owner
approval. The director must confirm current decisions before authorizing new
write semantics. Existing installations remain read-only throughout.

## Proposed delivery order

Priority is a workflow hypothesis, not measured Fiddler usage telemetry.
Start with finding an asset, changing it, and getting a verified stage back.
Next remove the need to switch tools for names, flags, colours and sounds.
Certify the multi editor already built before funding another implementation.
Specialist formats and destructive map maintenance follow.

The first five proposed PRs are:

1. **F01a: independent read-back runner.** Fileformat lane; no new writer.
   Read a synthetic stage with a pinned, independently built UOFiddler/ClassicUO
   reader and compare expected records, unknown bytes and untouched IDs. Run
   without GUI interaction or bundling proprietary data. Produce a dependency
   doctor and a machine-readable mismatch report. This unlocks credible parity.
2. **F02a: explicit existing-record stage replacement.** Fileformat lane,
   after F01a and director/owner agreement on the staged-save contract. One
   domain first: static art. Replace one occupied MUL or UOP ID, preserving the
   rest and refusing install destinations. No general converter in this PR.
3. **F06a: item metadata document.** Editor lane with fileformat review,
   after F01a. Edit name, flags and height in a working copy with undo/revert;
   preserve all other fields. A following F06b PR stages that document after
   the write decision. No land records or mass edits in this first PR.
4. **F03a: batch item PNG replacement preview.** Editor lane, after F02a.
   Interpret a folder's IDs, show collisions/errors before commit, then apply
   a verified stage batch. Cancel/refusal must leave the current stage intact.
   Land and gumps follow as separate PRs, not more scope in this one.
5. **F15a: localized-string working document.** Localization lane with editor
   integration, after F01a. Edit/add/delete strings in one selected language,
   keep file flags/encoding, undo and reload a project document. F15b adds
   staged binary output; F15c handles CSV compatibility separately.

These are proposals, not ready-to-launch assignments. Sound editing is next
unless the director's modder feedback moves it ahead of localization.

## Story families and required splits

CSV `Fxx` values are grouping keys. They are **not** permission to put the
whole family in one PR. Each listed suffix/parameter below becomes a separate
story brief after review; do not dispatch a family row as one story. Every PR
has one user behavior, aims below 1,500 lines / 20 files, and splits again if
the implementation estimate exceeds that ceiling. Reuse existing operations.

Proof codes in the table refer to the acceptance contracts below. Dependencies
name the preceding contract, not necessarily every UI row in a family.

| Family | Independent proposed stories / boundary | Owner lane | Depends on | Proof |
|---|---|---|---|---|
| F01 | a independent reader runner; b parity fixture/report adapter per domain | fileformat | pinned reader/build doctor | R |
| F02 | a occupied static replacement; b occupied gump replacement; c multi replacement; d save-container choice per art/gump/sound/multi/map | fileformat | F01; approved write policy | R,S |
| F03 | folder-to-stage preview/apply, separately for items, land, gumps; separate deletion/tombstone design before deletion implementation | editor + fileformat | F02 for replacements; F01 | R,S,U |
| F04 | one single-image export format at a time; batch export separately per kind/format | editor | existing PNG export; format codec doctor if external | I,U |
| F05 | linked asset navigation per relation; gump tag catalogue import; gump tag filtering; preview preferences each separate | editor | relevant target panel | U |
| F06 | a item metadata document; b stage modern item record; c CSV import; d CSV export; e remaining field mapping after byte-layout review | editor + fileformat | F01; write decision for b | R,S,U |
| F07 | a land metadata document; b land record writer; c old-layout item support; d old-layout land support | fileformat + editor | F06; F01 | R,S,U |
| F08 | a hue palette document/name; b gradient operation; c inverse/range operation; d text import; e text export; f stage integration | editor | hue overlay; F01 | R,I,U |
| F09 | a texture browser/search; b texture replacement document; c bridge existing world export; d slot/removal support only after policy | editor + fileformat | texmap export; F01 | R,I,S,U |
| F10 | a radar-colour document/writer; b colour panel; c per-tile average; d selection/range averaging; e CSV import; f CSV export | fileformat then editor | F01; write decision | R,S,U |
| F11 | a animation catalogue/alias certification; b frame bounds; c GIF export; d remaining image formats individually | editor + fileformat | existing animation workspace | R,I,U |
| F12 | a insert/remove/replace frame; b all-direction sequence import; c canvas/reference-point import; d palette text; e stage save; KR conversion separate | animation/editor | existing VD workspace; F01/F02 for stage | R,I,S,U |
| F13 | a animdata document/writer; b timing/frame editor; c JSON export; d JSON conflict-aware import; e GIF export | fileformat then editor | F01; F06 animated flag relation | R,S,U |
| F14 | a WAV export; b sound document; c WAV validation/import; d staged sound writer; e sound naming/list CSV; f slot/removal controls | audio + fileformat/editor | F01; F02 container policy | R,A,S,U |
| F15 | a string document; b binary stage writer; c CSV import; d CSV export; e custom-language opening | localization + editor | F01 | R,S,U |
| F16 | a tiledata/cliloc sync preview; b selected sync application | localization + fileformat | F06/F07,F15 | R,S,U |
| F17 | a font/glyph browser; b glyph working document/offsets; c ASCII writer; d Unicode writer; e TIFF exchange; f text preview | fileformat + editor | F01 | R,I,S,U |
| F18 | a light-mask browser/preview; b mask document; c writer; d image exchange by format | render/fileformat + editor | F01; existing light atlas extraction | R,I,S,U |
| F19 | a skill document/writer; b skill editing UI | fileformat + editor | F01 | R,S,U |
| F20 | a group document/writer; b group tree/drag UI | fileformat + editor | F19,F01 | R,S,U |
| F21 | a full keyword browser; b speech document/writer; c editing UI; d CSV import; e CSV export | localization/fileformat + editor | F01 | R,S,U |
| F22 | a patch index inspector; b preview adapter per supported patch type; no generic patch writer in this family | fileformat + editor | existing overlay/decoder; F01 | R,I,U |
| F23 | a offline dress document/body selectors; b equipment/layer/hue/mount editing; c dressed playback; d image export; e frame export; f GIF | animation + editor | F11/F12; equipment mappings | I,A,U |
| F24 | a certify TXT/WSC/UOA/CSV/UOX3 separately; b XML interchange; c one multi preview format; d batch records; e batch preview; UOP structure certification separate | multi architect | existing format APIs; F01/F02 | R,I,S,U |
| F25 | certify existing tool behavior; XML toolbox adapter; minimap navigation; history cleanup each separate; do not rebuild drawing tools | multi architect | existing multi editor | U,I |
| F26 | a facet image export; b marker exchange; c private-client navigation certification; independent PRs | editor/world | existing world project | R,I,U |
| F27 | a statics scan/report without writing; b filter plan/preview; c stage compaction; d cancellation; e independent verifier | fileformat | F01; owner maintenance decision | M,R,S,U |
| F28 | a facet resize plan; b stage resize; c rewrite certification; no shared-install maintenance | fileformat | F01,F27 | M,R,S |
| F29 | a invalid land report; b invisible static report | fileformat | existing map readers | M,R |
| F30 | a block-aligned region selection; b external source copy; c signed-height adjustment; d container support | world/editor + fileformat | world overlay; F01; F02 containers | M,R,S,U |
| F31 | a diff inspection/preview; b bounded stage application; c UOP source support | fileformat + editor | F01; world export | M,R,S,U |
| F32 | a legacy static import parser; b stage placement | fileformat + world/editor | world overlay; F01 | M,R,S,U |
| F33 | a melt-to-terrain plan; b verified stage apply | world/fileformat | F27; owner maintenance decision | M,R,S |
| F34 | a bounded static-clear preview; b verified stage apply | world/editor | world overlay; F01 | M,R,S,U |
| F35 | a XML replacement-rule reader/preview; b verified stage apply | world/fileformat | F01; world overlay | M,R,S,U |
| F36 | a altitude-only certification; b combined mode; c intensity preferences | world/editor | existing world modes | I,U |
| F37 | a multimap reader/browser; b RLE output; c facet image conversion; d image export | fileformat + editor | F01; map dimensions | M,R,I,S,U |
| F38 | two-dataset read-only comparison, **one domain per story**: items, land, textures, gumps, hues, tiledata, radar, animdata, cliloc, map; tiledata rules separate | fileformat + editor | F01; missing domain readers first | R,U |
| F39 | comparison image export, one supported domain/format per story | editor | matching F38 domain | I,U |
| F40 | selected-difference stage copy per domain; all-difference batching separate; added-only copy separate | fileformat + editor | matching F38; writer; F02 | R,S,U |
| F41 | a inert Fiddler XML parser/preview; b import adapter per supported asset kind; no executable manifests | fileformat + editor | F03; corresponding writer | R,S,U |
| F42 | container conversion per art/gump/map/sound/multi type, pack and unpack separate; folder batching later; housing metadata and compression compatibility explicit | fileformat | F01/F02; owner container decision | R,S |
| F43 | design typed private-shard item placement as alternative; arbitrary command/argument injection remains excluded | director + netcompat/editor | explicit product decision; private live link | U; private-shard proof |
| F44 | profile/file-source UX; export filename convention; each display preference separately after user-value triage | editor | existing config/profile rules | U |
| F45 | a local packed-items schema reader; b PNG-sheet/guide export; c validated sheet import into overlay/stage | editor + fileformat | F03; explicit fork format fixture | I,R,S,U |
| F46 | clipboard image exchange per asset kind; dirty-state display separate | editor | corresponding editable document; platform clipboard support | I,U |
| F47 | a multi-select item patch document; b mixed-state UI; c selective paste; d undo; land after F07 | editor + fileformat | F06/F07; F01 | R,S,U |

Family priorities after the first five: F14 sound, F08 hues, F09 textures,
F24/F25 multi certification, F13 item animation timing, then F38 comparison.
F10/F17-F22/F23 and map maintenance follow the director's feedback on actual
modder needs. F04's JPEG/TIFF exports and cosmetic preferences are lower
priority than a verified native-data save loop. Lack of a Fiddler format may
remain visible and documented while a useful PNG/JSON alternative ships.

## Acceptance contracts for every resulting story

Every brief records layer, toggle, parity proof, scenario, doctor and docs
section. Editor controls/documents are **L3**, compiled under `#if TOOLS`;
file codecs and command-line jobs are **Tools**; redistributable authored
content is **L2 typed Store components**. No L0/L1 behavior is proposed.
Manual editing works with AI disabled. Any optional AI later added uses
`AiFeatures`, hides disabled surfaces and cancels its running jobs.

| Code | Required evidence before a story is called working |
|---|---|
| R: independent read-back | Synthetic source -> edit -> staged file -> pinned UOFiddler or upstream ClassicUO reader. Compare raw records and preserved bytes; decode edited images too. The oracle must support the file type; GUO reading its own writer is insufficient. Deliberate corruption must fail. Record oracle commit and format version. |
| S: stage safety | Refuse the install, parent traversal, symlink/junction aliases and nested source/output paths. Hash the source before/after; preserve untouched entries. Validate complete batch before mutation, retain/recover prior stage on interruption, and report collisions. Replacement/deletion require explicit preview/confirmation in the product and their approved policy. |
| I: image/render parity | Nearest sampling; correct transparency/near-black, dimensions, frame centres, palettes and orientations. Reopen export in independent reader and compare quantized pixels. GIF/JPEG cannot be called byte/pixel-identical to lossless source; name expected timing/loss bounds. Capture actual editor UI and inspect it. |
| U: editor workflow | Extend the appropriate `EditorSmokeAssets`, `EditorSmokeArt`, `EditorSmokeMultiEdit`, `EditorSmokeWorldLayers`, or domain smoke stage; prove reload/teardown, undo/revert, cancellation and invalid input. Add a reproducible `tools/scenarios/fiddler-<story>` recipe/fixture specification using the existing scenario schema, then headless reload and authorized windowed evidence. No invented smoke flag is assumed here. |
| A: audio/animation | Check PCM format/sample count and audible playback for sound; frame order, centres, direction and duration for animation. Separate preview speed from persisted timing. Test missing bodies/layers and short/empty clips. |
| M: maps | Test rectangular and short facets, block boundaries, extreme signed Z, duplicate semantics and out-of-range IDs. Identity operations preserve headers and bytes. For intentional filtering, reconcile every removed/changed record with the plan/report; nothing outside selection changes. Include stop/restart behavior. |

For unedited data, require byte identity where the interchange retains raw
payload. For newly encoded data, require the independently decoded intended
record/pixels plus byte-identical untouched records; legal padding or palette
layout differences are reported, not hidden. `tools/uopack/README.md` explains
why a PNG-only loop cannot promise every original padding byte.

New writer kinds extend `uodata_write` and the typed record contract rather
than writing the install or silently reusing an unrelated pack type. Existing
world-export behavior may be called through a shared staged adapter after
review. Imported manifests are inert data; no scripts run at installation.
Before adding a new Store type, check existing `CONTENT_TYPES` and consumers.

Every external oracle/codec gets a doctor launcher pair and helpful failure
text pointing to it. Core hand-editing does not depend on a paid model or an
AI service. Run `launchers/dev/smoke.bat` before each commit. A green broad
smoke does not establish all 654 features; domain proofs remain mandatory.

## Decisions and review handoff

The director and exec-fable should review the catalogue coverage and first
five stories together. Resolve these before dispatch:

1. Confirm which existing ADR-0022 decisions are approved, then route occupied
   record replacement, deletion, container conversion and maintenance semantics
   to Moshu as needed. All destinations remain staged copies.
2. Confirm that this baseline excludes other lanes' pending work and reconcile
   duplicates before assignment. Keep this effort in the editor/tools layers.
3. Choose the independent reader build/version per file type and how its
   notices are supplied. Obtain file-specific approval before any code reuse.
4. Validate workflow priority with modders: metadata/sound/localization versus
   maps and multi exchange. No usage survey was run for this research.
5. Refresh 4.24 source at story pickup; the added rows are release-level
   evidence. Certify MUL, UOP and both tiledata layouts separately.

After plan approval, produce one handoff per approved leaf story and add sprint
rows through the director's process. Do not launch workers or write the shared
sprint in Phase 1. Keep the full CSV as the scope ledger: a postponed row must
retain its gap and destination story rather than disappear into a done count.

## Research evidence and limits

Run ID: `20261019_guo-codex-worker-fiddler_parity-research` in the shared GUO
evidence area. It holds the source-control inventory, catalogue builder and
validation notes; local source locations stay there rather than in this doc.
Source inspection is the evidence for this catalogue. No Fiddler GUI run,
new writer, client-data modification or complete independent round-trip suite
was performed for this planning change. Existing source smoke checks are
reviewed as implementation evidence, not rerun proof of every row.

The required broad smoke was run for this documentation commit. Build,
offline load, asset export/read-back and the final synthetic probes passed;
the command exited 1 because editor AI check
`editor_data_retired_slow_load_no_publication` failed. The log also reports
`splat_lods_present` as a non-blocking failure. Both are outside this two-file
documentation change; neither is presented as a passing editor result.

Phase 1 delivery is these two documents and a director review note. Phase 2
starts with the reviewers' response. Phase 3 remains held until approval.
