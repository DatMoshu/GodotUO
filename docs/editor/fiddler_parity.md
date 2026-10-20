# UOFiddler parity: a workbench for UO modders

## Owner implementation decision, 2026-10-20

Moshu approved implementation on this branch in the guo-worker-fiddler-parity
terminal: independent static-art verification first, then select existing item
art, replace its image, preview it, and export a verified staged copy. Occupied
static-art replacement is approved only in staged copies, with explicit
preview/confirmation, recovery of the prior stage and independent verification.
Item metadata follows, then the recommended sequence below. This supersedes
the earlier F02a owner hold for that narrow static-art workflow; it does not
approve deletion, other-domain replacement, map maintenance, publication or a
merge. The remaining catalogue is a proposed backlog, not one implementation PR.

### First implementation milestone

`tools/fiddler_parity` now contains an independent static-art reader and a
single-record candidate/verify/promote adapter. The existing art inspector
offers verified-stage export after image replacement; its editor-only dialog
requires preparation and a separate Apply click and offers prior-stage recovery.
The source installation remains read-only. This implements F01a and the narrow
static-art F02a workflow; the original catalogue statuses are retained until
operation-specific review records the implementation commit and visual proof.

Run `20261020_guo-worker-fiddler-parity_static-art-verification` contains the
synthetic reports and full-smoke logs. Thirty-four synthetic checks passed for
direct decoding, free insertion, occupied replacement, corruption refusals,
stale inputs, promotion rollback/recovery and stage locking. Both MUL/IDX and
uncompressed, zero-entry-header UOP are covered separately. The .NET editor
build and full headless smoke passed; the latter checks action registration,
initial dialog state and teardown. No real-install replacement, compressed UOP
or windowed visual-certification claim is made.

The follow-on F06a [item metadata working document](item_metadata.md) adds
name, 64-bit flags and height editing to the static-art inspector, with
undo/redo/revert and project JSON save/reload. It preserves the original modern
41-byte record and does not emit binary tiledata or mutate runtime metadata.
F06b native staging and its independent tiledata oracle remain separate work.
The subsequent F03a [batch static PNG replacement](batch_static_replacement.md)
adds folder/ID collision review, per-image source comparison and a whole-batch
candidate/verify/apply workflow. It shares F02a's recovery and rechecks every
selected image before applying. Other domains and deletion remain separate.
F01-UOP subsequently adds opaque per-entry header preservation. The private
`20261020_guo-worker-fiddler-parity_uop-entry-headers` run verifies a disposable
stage from the configured 7.0.107.76 archive: two edited IDs, 42,528 untouched
records and all 42,530 entry headers preserved, with apply/recovery and an
unchanged source installation. Compressed UOP remains outside certification.
The headless asset report passes all 13 metadata checks, including the unsaved
close/discard-cancel path. The editor rebuild/reload run also passed with an
empty top-level failure list; neither result substitutes for windowed visual
review.

Phase 1 proposal, 2026-10-19. guo-director first-pass review received;
catalogue and survey await guo-director and exec-fable sign-off.
F01a has separate director authorization for assignment (inbox #11093);
that narrow read-only brief does not approve the remaining plan.
No implementation, dispatch, sprint edits, publication or licence approval is implied.

## Variant survey: which tools deserve parity coverage first?

Survey observed **2026-10-19 UTC**. The strongest public adoption evidence is
for **polserver/UOFiddler**, followed by **UoFiddlerPixel** among maintained
Fiddler variants. Specialist community builds add important animation and
batch workflows even when their GitHub audience is small. This is a ranking
of **public usage proxies**, not measured active users or market share.

GitHub figures below come from timestamped public API responses: repository
metadata, the default branch's latest commit, and release assets. Download
sums include every asset in the observed releases: repeated downloads,
plugins and old versions can all count. They exclude source archives and
external mirrors. Community resource counters are separate, indexed-page
observations; crawl age varies and some pages disagree. Do not add the two
channels or compare a twelve-year-old listing directly with a new release.

### GitHub ranking and maintained variants

The first five rows rank named Fiddler repositories by stars, breaking the
three-star tie by forks. Later rows identify low-star but relevant specialist
work; their order does not imply more users than the historical repositories.
The adjacent map-editor successor has its own row because it solves a
different part of the job.

| Rank / role | Repository | Stars / forks | Releases / asset downloads observed | Latest default-branch commit | Interpretation |
|---|---|---|---|---|---|
| 1 | [polserver/UOFiddler](https://github.com/polserver/UOFiddler) | 92 / 96 | 76 / 8,612; latest 4.24.0 | 2026-09-13, `610c930` | Primary general-purpose baseline; strongest GitHub and community evidence. |
| 2 | [jedi661/UoFiddlerPixel](https://github.com/jedi661/UoFiddlerPixel) | 12 / 11 | 14 / 456; latest 4.12.12 | 2026-10-16, `10f69b6` | Maintained expanded distribution, including new asset libraries; registered as an independent repository, not a GitHub network fork. |
| 3, historical | [SaschaKP/UOFiddler](https://github.com/SaschaKP/UOFiddler) | 4 / 2 | 0 / 0 published assets | 2019-03-01, `5750fd7` | Older source distribution; no recent maintenance established. |
| 4, historical | [Xen85/UOFiddlerWPF](https://github.com/Xen85/UOFiddlerWPF) | 3 / 5 | Not inspected | Push 2012-08-06; commit not inspected | Old WPF alternative; search metadata only, no modern compatibility claim. |
| 5, historical | [kevin-10/uofiddler](https://github.com/kevin-10/uofiddler) | 3 / 3 | 0 / 0 published assets | 2019-03-02, `7553585` | Fork of the older luciopaiva lineage. |
| Specialist | [elanon1/Gump Creator](https://github.com/elanon1/UOFiddler-Gump-Creator-Plugin) | 1 / 5 | 0 / 0 published assets | 2025-06-24, `7061e5e` | Functional visual layout plugin, including POL and UOX3 source exporters. |
| Specialist | [LCT-Studio/UOFiddler](https://github.com/LCT-Studio/UOFiddler) | 1 / 0 | 0 / 0 published assets | 2023-01-03, `f0e9bcd` | Animation-creator fork, consistent with the dated ServUO distribution below; no binary identity claim. |
| Specialist | [Tald0r/UOFiddler](https://github.com/Tald0r/UOFiddler) | 1 / 0 | 1 / 4 | 2026-06-01, `5146bcf` | Published asset is an empty custom-animation dataset, not a complete editor build. |
| Active, low reach | [Dramoor/UOFiddler](https://github.com/Dramoor/UOFiddler) | 0 / 2 | 15 / 17; latest 5.3.9 | 2026-09-25, `b60f590` | Distinct maintained source: dataset discovery, multi-selection, action remapping and UOP saves. |
| Active, unranked reach | [Kita72/UOFiddlerOnline](https://github.com/Kita72/UOFiddlerOnline) | 0 / 0 | 0 / 0 published assets | 2026-08-19, `e515476` | Activity established; online workflow not source-audited, so no feature inferred from its name. |
| Adjacent successor | [kaczy93/centredsharp](https://github.com/kaczy93/centredsharp) | 82 / 57 | Latest 100 sampled / 4,514; latest 0.6.11.31 | 2026-06-30, `50d23f7` | Significant modern map-editor audience. Release/download figures are a lower bound because pagination was not completed. |

The API search inspected the top 40 repositories named UOFiddler and all 93
returned direct forks of polserver. The repository's 96-fork network counter
is a different measure. This is a selected survey, not an audit of every fork.
Recent `pushed_at` alone is unreliable: giga487 was pushed in October 2026
but its default head is from August 2023. morbitz's head is the same
`2b9e492` already audited here. MulViola, Mass-Graphics-Export and
UOFiddler-Pol all share head `6e8c67c` dated May 2025, with zero stars and no
published releases observed; their names do not establish extra features.
[Fork inventory](https://api.github.com/repos/polserver/UOFiddler/forks?sort=stargazers&per_page=100),
[repository API](https://api.github.com/repos/polserver/UOFiddler),
[release API](https://api.github.com/repos/polserver/UOFiddler/releases?per_page=100).

### Community distribution and use

Among the sampled **ServUO Fiddler listings**, the download-counter order is
4.6 UOP build, Legnes 4.9.17, Legnes 4.9.12, Prapilk UOP, Max's bundle,
then iWerking bulk build. These are resource listings uploaded by community
members, not evidence that ServUO ships or endorses a particular binary.

| Distribution / tool | Observed resource downloads | Dated release or update | What the evidence adds |
|---|---|---|---|
| [Fiddler 4.6 with UOP support](https://www.servuo.dev/archive/categories/tools.31/) | 1,717 | 2018-04-03 update | Largest sampled Fiddler resource counter; historical distribution, not the current compatibility target. |
| [Legnes animation creator 4.9.17](https://www.servuo.dev/archive/uofiddler-4-9-17-for-animation-creators.1872/) | 286 | 2023-01-03 | Male/female references, rider preview, combined image and Russian cliloc editing. |
| [Legnes 4.9.12](https://www.servuo.dev/archive/categories/tools.31/) | 179 | 2022-08-15 | Earlier version of that specialist workflow; do not sum as separate users. |
| [Prapilk UOP v2](https://www.servuo.dev/archive/uofiddler-for-uop.2566/) | 59–63 across indexed pages | 2026-02-15 | Custom-client UOP animation work; author explicitly reports an official-client compatibility problem. |
| [Max UO utils 4.8.8](https://www.servuo.dev/archive/uo-utils.2664/) | 42–43 across indexed listings | 2026-08-09 | Modified Ultima library for UOP animations/multis, bundled region/map editor and UO Architect; description-level evidence. |
| [iWerking bulk build](https://www.servuo.dev/threads/uofiddler-w-bulk-management-controls-shift-to-select-ctrl-to-de-select-batch-delete.17321/) | 29 in tools listing | 2026-04-30 | Persistent/range selection and confirmed deletion; author withdrew the shared build due to a gump-selection bug. |
| [CentrED+](https://www.servuo.dev/archive/centred.187/) | 1,127–1,133 across indexed pages | 2023-10-25 reupload | Shared map/statics editing, tile groups, brushes, multis and arbitrary map dimensions. Reupload is not proof of recent feature development. |
| [Gump Studio modification](https://www.servuo.dev/archive/gump-studio-moddification.214/) | 667 | 2014-08-21 | Cyrillic, ASCII/Unicode selection, partial hue, full font choices, larger canvas and HS item IDs. |
| [Mulpatcher](https://www.servuo.dev/archive/categories/tools.31/?page=2) | 127–128 binary; 66 source | 2015-04-18 listing | Legacy MUL editing and TXT Autopatch contracts remain migration inputs. |
| InsideUO | No comparable counter verified | Historical viewer | Asset inspection, with modern-format limits. [RunUO description](https://runuo.net/wiki/index.php/InsideUO), [2023 Stratics support request](https://community.stratics.com/threads/help-with-insideuo.428963/). |

Community mentions corroborate workflows rather than population counts:
POL's support thread directs users to the maintained polserver releases;
a mod author's install guide uses Fiddler MassImport and UOPacker for classic
shards; a ModernUO-targeted terrain generator recommends Fiddler's map page
for inspection. No separately maintained **official ModernUO Fiddler bundle**
was verified. Dramoor's ModernUO source generator is a fork feature, not such
a bundle. [POL support](https://forums.polserver.com/viewtopic.php?t=2642&start=325),
[mod installation guide](https://corvaeoboro.github.io/ultima_online_mods/ultima_art_mods_install.htm),
[ModernUO community workflow](https://github.com/xor-droid/sosaria-forge#using-the-maps).

For Outlands, verified public evidence is its **GumpOverrides image-folder
contract**, plus OUTLANDSOwyn's published CentrED+ brush resource. Neither
establishes a public Outlands-specific Fiddler editor or support for its
private containers. Catalogue the documented PNG/BMP override and brush
interchange; do not invent private-format parity. ClassicUO is a client,
not a Fiddler replacement. CentrED# is the relevant modern adjacent map
editor, while UOEngine describes a broader editor project without evidence
here sufficient to rank its adoption or completed features.
[Outlands contract](https://wiki.uooutlands.com/Gump_Overrides),
[author's brush listing](https://www.servuo.dev/archive/authors/outlandsowyn.1036/),
[CentrED#](https://github.com/kaczy93/centredsharp),
[UOEngine scope](https://github.com/UOEngine/UOEngine).

### Union of observed features, by page

The CSV preserves original IDs FP-0001–FP-0654 and appends the surveyed
operations. `variants` identifies the inspected origin(s), not a complete
support matrix: a missing variant tag does not mean that variant lacks a
shared inherited feature. `variant_evidence` separates pinned source from
release notes and author/community descriptions. Different document formats
or batch scopes remain separate; cosmetic duplicate buttons do not become
requirements. Source-only operations remain unverified behavior.

| Page or workspace | Observed additions to the POL baseline | Variant origin / destination |
|---|---|---|
| Items and Land tiles | Image transforms, masks, alignment aids, bookmarks, color analysis, gallery; persistent/range selection, selected export/removal, metadata-filtered search and hued output | PIX, DRAM, IWORK; F03/F05/F48 |
| Textures | Texture-to-land conversion, size conversion, color/pattern painter, composition, cutters, isometric slicing | PIX; F09/F48 |
| Gumps | Address-text image exchange, names/marks, image painting; hex-named folder overrides | PIX, OUT; F48/F56 |
| Tiledata, Radar colors | Copy/paste settings, ClassicUO text metadata, default cliloc link, missing-color fill; paired terrain metadata | PIX, DRAM; F06/F10/F16/F47/F53 |
| Animation list/editor | Body/rider reference composition, XML catalogues, action copy/remap/scale, clip editor, sprite sheets and frame XML | PIX, DRAM, LEG; F11/F12/F23/F50 |
| UOP animation | Creature/equipment VD exchange, sequence edits, MainMisc table, BIN diagnostics, compatibility conversion | PIX, PRAP, MAX, MICHEL; F49; author limitations retained |
| Animdata, Cliloc, Fonts, Light, Sounds | Hued GIF, search, substring copy, TTF glyph rasterization, light preview scale, sound loop/counts | PIX, LEG; F13–F18 |
| Hues, Skills, SkillGroups, Speech, Verdata, Dress, Multimap | Original page actions remain in CSV; no separate added editor contract established in the selected variants | POL; original families; not inferred from a tool's name |
| Multis and MultiEditor | XML exchange, contextual preview, UOP multis; source generation is separate | POL, PIX, DRAM, MAX; F24/F25 |
| Maps and region tools | Extra-facet discovery, region presets/copy, map image conversion, terrain/altitude generation; shared brush/multi editing | PIX, DRAM, MAX, CENTRED; F26/F30/F51/F57 |
| Compare | Map diff list/statistics/CSV/screenshot/cancellation; selected batch copy across domains | PIX, DRAM; F38–F40 |
| MassImport / legacy patching | Fiddler XML, Mulpatcher TXT Autopatch, sound/animation replacement inputs | POL, MULP; F41/F56 |
| UOPacker | Single-entry extraction, autofill, container choice and overwrite policy | POL, PIX, DRAM; F42 |
| Art, Gump and Terrain libraries | Independent indexed libraries, tags/categories/favorites, ID transfers, thumbnails and metadata; paired land/texture settings and presets | PIX 4.12.12; F53; Store packaging alone is insufficient |
| Gump layout designers | Visual element editing already exists in GUO; plugin document schemas, POL/UOX3/RunUO emitters and legacy font semantics still differ | GC, GSTUDIO; F55 |
| Empty-data creator | Blank containers, configured capacities, index inspection/patching, directory comparison and validation | PIX; F52; synthetic generation first |
| Settings | Root-folder discovery, custom map/animation XML, single-file overrides and backups | PIX, DRAM; F44 |
| General tools and source helpers | Notebook, clock, media player, network helpers, executable processing, server launching and server script templates are recorded as exclusions | PIX, DRAM; F54; require separate product scope |

Variant keys: **POL** polserver; **PIX** Pixel; **DRAM** Dramoor; **GC**
elanon1 Gump Creator; **LEG** Legnes; **PRAP** Prapilk; **MAX** Max bundle;
**IWORK** iWerking; **MULP** Mulpatcher; **INSIDE** InsideUO; **OUT**
Outlands override contract; **CENTRED** CentrED+ and its published brush
resource; **GSTUDIO** standalone Gump Studio modification/exporter;
**MICHEL** Michelangelo UOP-to-VD utility; **UOWW-local** the local packed-art
fork audited below. Names identify provenance, not licensing approval.

The added pinned sources are PIX `10f69b6`, DRAM `b60f590` and GC `7061e5e`.
Full commit URLs are in each added source row. Pixel identifies Beerware and
Wineware notices, including separately integrated older tools; inspect each
file and dependency before any reuse. API `license: null` is missing
standardized metadata, not permission or a determination of no licence.
No external editor binary, proprietary artwork or private Outlands data was
downloaded or executed for this survey.

GUO can already help you find art, replace a picture in a project, edit a multi,
and preview a world. It cannot yet replace Fiddler for all client-data work.
The biggest remaining jobs are safe replacement of existing records, editable
tiledata, sounds and strings, and the smaller specialist file editors.

## Catalogue and scope

The [feature catalogue](fiddler_parity.csv) has **1,198 rows**: 73 done in source,
222 partial, 875 missing, and 28 out of scope. These are action counts, **not a
completion percentage**: exporting another image format is much smaller than
writing a map-maintenance engine. One row represents one user operation or
compatibility format, not each button, event handler or internal helper.
Shared controls with the same effect count once. Individual RGB channels,
facet names and flag bits are parameters of an operation, not separate features.

Coverage includes the original Start/settings shell, all 21 data pages, five
workflow plugins, the example plugin, the animation editing dialog, a local
fork's packed-item exchange, and the selected variant union above. Compare's
ten baseline domains have separate groups.
The CSV contains source anchors, format names, GUO evidence, limitations,
proposed story families and inspection provenance. Filter `page` to review a
page, `status` to find gaps, or `story` to collect a proposed work package.

### Reading and maintaining the CSV

`id` is a stable operation identifier; preserve it when revising a row.
`page`, `feature` and `formats` describe the operation and its interchange
scope. `status`, `guo_evidence` and `gap_or_limit` describe GUO at the stated
baseline, while `upstream_source`, `baseline`, `variants` and
`variant_evidence` identify the external evidence. The CSV's `baseline` is
the external source revision or release, not GUO's commit. `verification`
states how the row was inspected; `story` routes it to a proposed family.
An out-of-scope row may use `-` instead of an implementation family.

When a story lands, update only the operations its evidence covers and
record the new GUO commit and proof in `guo_evidence`/`verification`.
Retain the external provenance and remaining limits. Recount the statuses
in this document whenever rows or statuses change. Do not promote an entire
family because one format or one container passed.

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
| Design a gump layout | `Gumps/GumpStudio.cs`, `GumpCanvas.cs`, `GumpStudioFiles.cs`, `GumpStudioProperties.cs` | Canvas elements, pages, drag/resize, properties, stacking, history, JSON documents and classic layout/text export exist. Plugin JSON, POL GF/UOX3/RunUO emitters and exact font behavior remain separate compatibility work. |

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
**Proposed on main**, confirmed by guo-director's first-pass review on
2026-10-19. F02a occupied-record replacement waits for Moshu's approval.
F01a adds independent read-back and needs no write-policy decision; it can
proceed through the director's separately authorized read-only brief. All
work stays in the editor/tools layers and all writes target staged copies. Existing
installations remain read-only throughout.

## Proposed delivery order

Priority is a workflow hypothesis, not measured Fiddler usage telemetry.
Start with finding an asset, changing it, and getting a verified stage back.
Next remove the need to switch tools for names, flags, colours and sounds.
Certify the multi editor already built before funding another implementation.
Specialist formats and destructive map maintenance follow.

The first five proposed PRs are:

1. **F01a: independent static-art read-back runner.** Fileformat lane; no new writer.
   Read a synthetic stage with a pinned, independently built UOFiddler/ClassicUO
   reader and compare expected records, unknown bytes and untouched IDs. Run
   without GUI interaction or bundling proprietary data. Produce a dependency
   doctor and a machine-readable mismatch report. Start with static art,
   certifying MUL/IDX and UOP independently where supported by the oracle.
   Split unsupported or oversized container work explicitly; other domains
   need their own F01b certification.
2. **F02a: explicit existing-record stage replacement.** Fileformat lane,
   after F01a and Moshu's approval of occupied-record replacement. One
   domain and container layout first: static art, using the layout certified
   by F01a. Replace one occupied ID, preserving the rest and refusing install
   destinations. Certify the other container separately; no general converter
   in this PR.
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

F01a alone is ready for director assignment under the separately approved
static-art brief (inbox #11093, 2026-10-19). No worker is launched by this
document. The other four remain proposals. Sound editing is next
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
| F48 | Image-workspace operations, **one tool/operation per story**: transform/mask; texture-to-land; cutter/slicer; color/pattern editor; gallery/composition; image batch output separately | art/editor | F03/F09; existing external-editor exchange; source image fixtures | I,U; R/S when applied |
| F49 | a read-only UOP animation/sequence inspector; b BIN diagnostics; c VD adapter per creature/equipment layout; d sequence document; e staged sequence writer; f MainMisc adapter; scale/remap separately | animation + fileformat | F01/F12/F42; approved write semantics; custom versus official client fixtures | R,I,A,S,U |
| F50 | a inert animation-list XML parser; b catalogue editing; c bodyconv/mobtypes consistency preview; d staged metadata edits; XML text conveniences separately | animation + editor | F11; F01 for binary relation; approved metadata contract | R,A,S,U |
| F51 | a transition XML adapter; b terrain/altitude image import; c deterministic map generation; d statics generation; e transition-image authoring; keep existing mapgen as owner | world + multi/art | F01/F30; existing tools/mapgen; dimension/seed fixtures | M,R,I,S,U |
| F52 | a empty container generator **per kind/layout**; b IDX/MUL validator; c bounded hex inspector; raw index mutation separately reviewed; whole-dataset setup last | fileformat | F01; synthetic fixtures; approved stage policy | R,S,M where applicable |
| F53 | a Pixel art-library reader; b gump-library reader plus thumbnail metadata; c paired terrain-library reader; d category/tag migration; e image exchange; f staged transfer per kind; growth/compaction separate | Store + editor/fileformat | F01/F03/F09; schema/version fixtures; existing typed Store; no executable installation | R,I,S,U |
| F54 | Exclusions ledger for general utilities, server code templates, process/network launchers and executable processing; no implementation story without new scope | director | explicit product decision; source/dependency review | Separate acceptance contract if approved |
| F55 | a certify existing UO Gumps canvas against plugin operations; b plugin JSON adapter; c legacy font/partial-hue fixtures; d POL GF export; e UOX3 export; f RunUO exporter contract; one format per PR | UI/editor + localization | existing GumpStudio and document; source export product decision; never execute generated source | R,I,U |
| F56 | a Mulpatcher TXT reader/preview; b staged patch apply per art/gump kind; c Outlands folder export with naming/transparency fixtures | fileformat + editor | F01/F03/F41; no private-container decoding implied | R,I,S,U |
| F57 | a CentrED brush XML contract inventory; b inert brush import; c local brush application; d multi placement certification; multiuser service is a separate owner decision | world + multi architect | existing world editor; F51; published-schema fixtures and asset provenance | M,R,I,S,U |

Family priorities after the first five: F14 sound, F08 hues, F09 textures,
F24/F25 multi certification, F13 item animation timing, then F38 comparison.
F10/F17-F22/F23 and map maintenance follow the director's feedback on actual
modder needs. F04's JPEG/TIFF exports and cosmetic preferences are lower
priority than a verified native-data save loop. Lack of a Fiddler format may
remain visible and documented while a useful PNG/JSON alternative ships.

The usage-proxy survey keeps those first five intact: the strongest evidence
still concerns the general data-edit/save loop. It adds F49's read-only UOP
animation work immediately after the existing F12 animation workspace,
because several independently distributed builds address it. F55 begins with
certifying/reusing UO Gumps. F53 library migration and F56 TXT/override
interchange follow the first reliable staged asset loop. Pixel's broad
convenience toolset is useful scope, but its existence does not move all 442
surveyed operations ahead of safe saving. These are proposed priorities,
pending director and exec review; no dispatch is authorized by a ranking.

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
smoke does not establish all 1,198 features; domain proofs remain mandatory.

## Decisions and review handoff

The director and exec-fable should review the catalogue coverage and first
five stories together. Resolve these before dispatch:

1. Director decision recorded: ADR-0022 remains Proposed on main. Hold F02a
   occupied-record replacement for Moshu; F01a independent read-back requires
   no write-policy decision and has separate authorization for director
   assignment under inbox #11093. Route deletion,
   container conversion and maintenance semantics for their own decisions.
   All work remains editor/tools only, with writes to staged copies.
2. Confirm that this baseline excludes other lanes' pending work and reconcile
   duplicates before assignment. Keep this effort in the editor/tools layers.
3. Choose the independent reader build/version per file type and how its
   notices are supplied. Obtain file-specific approval before any code reuse.
4. Validate workflow priority with modders: metadata/sound/localization versus
   maps and multi exchange. Public popularity proxies were surveyed; no
   modder interview, installed-base measurement or active-user survey was run.
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

Follow-up run: `20261019_guo-codex-worker-fiddler_variant-survey`. It contains
timestamped public API responses, selected source text at fixed commits,
UI-difference inventory, union catalogue builder and validation. The observed
union adds 544 rows, including explicit exclusions and description-only
compatibility targets. It is not a certification of every third-party binary
or all 93 direct forks. Gaps in source availability remain in the row notes.
The source-text collection excludes assets and binaries; the survey does not
establish redistribution rights. Firecrawl searches returned insufficient
credits; research continued through available web search and public GitHub
endpoints without purchasing credits.

The original 654-row delivery's broad smoke exited 1 because AI check
`editor_data_retired_slow_load_no_publication` failed. The follow-up survey's
required full smoke **passed, exit 0**: build, offline load, headless editor,
asset export/read-back, 105 launcher pairs and final synthetic probes.
The headless report has `ok: true` and an empty top-level failure list; it
still reports `splat_lods_present` as a non-blocking art subcheck failure.
Native Pixelorama animation-save and private-shard checks were skipped as
reported by the harness. The first follow-up attempt could not write Godot's
profile/cache under the sandbox; the authorized rerun completed. Neither run
certifies the new third-party formats or their visual behavior.

Phase 1 delivery is these two documents and a director review note. Phase 2
review is in progress; the first-pass director decisions are recorded above.
Phase 3 remains held until plan approval, except the separately authorized
F01a read-only runner brief. This branch adds no implementation or dispatch.

Completion review run: `20261020_guo-worker-fiddler_parity-plan-review`.
Catalogue structure/count checks, documentation links, privacy scan and the
required full smoke passed (exit 0) after restoring this worktree's gitignored
local configuration with the repository setup tool. The CSV and runtime code
are unchanged. This verifies the planning cleanup and existing smoke coverage;
it adds no third-party format or visual-parity certification.
