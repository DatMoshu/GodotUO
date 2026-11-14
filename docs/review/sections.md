# GUO architecture review sections

Initial source inventory: 2026-11-10. This is a review map, not certification of
the listed systems. Current source registration wins when a label or path changes.
Review one bounded section/workflow at a time under the [review contract](contract.md).

The first objective is a dependable ClassicUO port with a coherent editor and a
public setup that works without a maintainer's private infrastructure. Performance,
correctness, workflow completion and clean installation are acceptance criteria,
not separate cleanup phases after exposing a feature.

## Reusable skills

| Skill | Use |
|---|---|
| [guo-arch-review](../../.claude/skills/guo-arch-review/SKILL.md) | Dependency, ownership, parity and failure-path review of any section |
| [guo-ui-review](../../.claude/skills/guo-ui-review/SKILL.md) | Each workspace/mode, navigation, input and complete user tasks |
| [guo-perf-review](../../.claude/skills/guo-perf-review/SKILL.md) | Measured CPU/GPU/IO/memory and interaction latency |
| [guo-net-review](../../.claude/skills/guo-net-review/SKILL.md) | Protocol, transport, reconnect, plugins and server authority |
| [guo-readiness-review](../../.claude/skills/guo-readiness-review/SKILL.md) | Whether normal main exposure is justified by evidence |
| [guo-portability-review](../../.claude/skills/guo-portability-review/SKILL.md) | Fresh clone, public agent setup, dependencies and shareability |

Examples: “Use guo-arch-review for R05 reconnect and queued sends”; “Use
guo-ui-review for R11 Sounds, from import through reopen”; “Use guo-readiness-review
for Pixelorama on a fresh Windows install.” Read skill files directly when automatic
discovery is unavailable. No organization tooling is needed.

## System boundaries

Paths in the next table are relative to `godot/GUO/` unless prefixed with `tools/`,
`launchers/`, `docs/`, `.github/`, or a root instruction filename. Shared addon paths
are under `addons/guo_editor/`. Each row owns a distinct acceptance result; shared
dependencies must also be checked when the change crosses their boundary.

| ID | Review section | Source entry points | Deep questions and required proof |
|---|---|---|---|
| R01 | Bootstrap, configuration and profiles | `src/Bootstrap`, `src/Configuration`, `src/Workspace`, `tools/guo/config.py`, `launchers/_shared`, `GUO.csproj` | Same precedence across launchers/Python/C#; missing data; migration; worktree fallback; spaces in paths; fresh launch and profile reopen without private defaults |
| R02 | Port seams and core lifecycle | `src/Client`, `src/Compat`, `src/Platform`, `src/Utility`, `tools/port_drift`, `tools/port_audit` | Faithful upstream behavior; marked deviations; engine ownership; start/stop/reset; strict drift and appropriate runtime parity |
| R03 | Renderer and picking | `src/Render`, `src/Game/Scenes`, `docs/architecture`, `tools/perf_probe` | Active presenter/backend from current ADRs; depth, transparency, hues, batching, nearest sampling, picking and zoom; matched visual/input proofs and frame distributions |
| R04 | Files, asset decode and caches | `src/IO`, `src/Assets`, `src/Render`, `tools/guoasset` | Corrupt/truncated inputs; map/texture lifetime; cache bounds, invalidation and eviction; cold/warm load and travel/soak measurements |
| R05 | Netcode and session state | `src/Network`, `src/Platform/Web`, `tools/network_tests`, `tools/server_lab`, `tools/ws_bridge` | Packet framing, encryption/compression, redirect, partial traffic, reconnect and send queues; production loopback plus relevant backend/transport scenarios |
| R06 | World simulation and gameplay | `src/Game/GameObjects`, `src/Game/Managers`, `src/Game/Map`, `tools/scenarios` | Movement/ack/resync, items, combat, targeting and scene changes; server-authoritative round trips and upstream parity |
| R07 | Player UI, input and audio | `src/Game/UI`, `src/UI`, `src/Input`, `src/IO/Audio`, `src/Configuration` | Login, gumps, drag/drop, focus, controllers/touch, options and audio ownership; off-state parity and target-specific interaction evidence |
| R08 | Plugin and mod boundaries | `src/Network`, `godot/GUO.ModApi` (repo-relative), `tools/plugin_host`, `docs/modding` | ABI, callback ownership, mutation/veto/injection order, permissions, exceptions and unload/reconnect; production plugin probe |
| R09 | Editor shell and navigation | `addons/guo_editor/GuoEditorPlugin.cs`, `addons/guo_editor/Docks`, sibling `addons/guo_editor_*` plugins | Workspace/dock ownership, stable identities, shortcuts, layout restoration, lazy startup and hot reload; no duplicate tabs/processes or hidden idle work |
| R10 | World authoring and inspection modes | `addons/guo_editor/World`, `addons/guo_editor/Overlay`, `docs/world_workspace.md` | Each tool/mode below; block transactions, undo, brush bounds, layers, selection, facet/project changes, staging and read-only client install |
| R11 | Assets workbench | `addons/guo_editor/Docks/AssetsView.cs`, `addons/guo_editor/Docks/AssetsWorkspace.cs`, `addons/guo_editor/Panels`, `docs/editor` | Every category below gets its own browse/edit/save/reopen/stage review; lazy scans, stable IDs, format fidelity and no stale inspector selection |
| R12 | Multi Editor | `addons/guo_editor/MultiEdit`, `addons/guo_editor_multiedit`, `tools/multi`, `docs/architecture/ADR-0031-multi-editor.md` | Selection from World; import variants, pivots, bounds, heights, undo and export/reimport; generation separately gated from manual construction |
| R13 | Map Generator | `addons/guo_editor/MapGen`, `addons/guo_editor_mapgen`, `tools/mapgen`, `docs/architecture/ADR-0030-world-generation-in-the-editor.md` | Preview versus committed output; deterministic seeds, progress/cancel, large maps, borders/stairs, project handoff and safe staging |
| R14 | Gumps workspace | `addons/guo_editor/Gumps`, `addons/guo_editor_gumps` | Document/pages, layout/anchors, asset picks, text/localization, preview, undo and saved-runtime result; native and modern output separately |
| R15 | Store and packs | `addons/guo_editor/Store`, `addons/guo_editor_store`, `src/Store`, `tools/asset_store`, `tools/uopack` | Browse/validate/install/activate/remove; typed components, provenance, archive paths, interrupted installs, compatibility and rollback; no executable installation or implied publication |
| R16 | Admin and shard operations | `addons/guo_editor/Admin`, `addons/guo_editor_admin`, `addons/guo_editor/Docks/ShardDock.cs`, `tools/modernuo`, `tools/editor_shard` | Each admin subview below; capability checks, current connection identity, destructive-action recovery, async status and server acknowledgement |
| R17 | Art exchange and Pixelorama | `addons/guo_editor/Art`, `addons/guo_editor_pixelorama`, `tools/pixelorama` | Install, open correct asset, paint/undo, save-back, provenance, validation, preview and reload; native embedding/standalone supported separately |
| R18 | Animation authoring / proposed UO Anims | `addons/guo_editor/Animation`, `addons/guo_editor/Panels/AnimationPanel.cs`, `addons/guo_editor/Docks/SpriteMotionDock.cs`, `tools/spritemotion`, `tools/uopack/outfit.py` | Existing frame editor versus hosted Fit Lab versus planned Sprite Lab; source parity matrix, public fixtures, fitting/rendering, metadata, staging and in-game consumption |
| R19 | AI, MCP and external workers | `addons/guo_editor/AI`, `src/Automation`, `tools/editor_mcp`, `tools/guo_mcp`, `tools/comfy` | `AiFeatures` visibility and cancellation; manual path with AI off; permission groups, loopback/token boundary, job ownership, timeout and idle cost |
| R20 | Build, export and readiness | `GUO.csproj`, export presets, `src/Labs`, `.github/workflows`, `tools/windows`, `tools/android`, `tools/web`, `tools/steamdeck` | Default surface inventory; Debug/ExportDebug/ExportRelease differences; editor/browser/Labs exclusion; correct dependencies/icons; actual target doctor/export/smoke |
| R21 | Public repository and agent onboarding | `README.md`, `CONTRIBUTING.md`, `AGENTS.md`, `CLAUDE.md`, `.claude`, `tools/agent_skills`, `tools/privacy_scan` (repo-relative) | Ordinary clone without personal services, paths, handoffs, paid accounts or ignored binaries; instructions/hooks/skills resolve; source attribution preserved |
| R22 | Localization, docs and diagnostics | `src/Localization`, `docs/localization.md`, `docs/wiki`, editor log/status surfaces, `tools/docs_lint` | Accurate supported behavior, actionable error recovery, long translations, redaction, bounded logs, reproducible bug reports and consistent product terms |

For every section, include dependency direction, lifecycle/thread ownership, persisted
contract, negative cases, performance/idle budget and tested exposure. These are review
dimensions, not instructions to implement a new abstraction in every subsystem.

## Editor workspaces and modes

Observed main-screen registrations: **World, Assets, Store, Admin, MapGen, Multis,
Gumps, Pixelorama** (verify current `TabName` constants). Bottom/auxiliary
surfaces include Inspector, Shard, Fit Lab, AI, Art, Static Studio, Logs, Layers and
Regions. Registration and default enablement must be inspected separately from the
layout the reviewer happens to have saved.

### R10: World

Review each WorldTool individually: Select, Stamp, Erase, Raise, Lower, Hue,
PlaceItem, PlaceSpawner, MoveObject, DeleteObject, Measure, Route, Pin, Area, Brush,
PickRegion and AudioZones. Follow the live enum if it changes. Separate project
edits from shard-side actions. Include drag cancellation and undo grouping across
block boundaries. New boundary controls also need their own selection/draw/resize
path and terrain compatibility proof.

Review each registered visual mode: Height, Walkability, Reachability, Types, IDs,
Land mesh, Problems and Project diff, plus Off. Check legends against actual
classification, tinted/solid visibility, picking agreement and invalidation after
edits/facet changes. Reachability needs a bounded flood fill; Off needs no residual
work. Layers, water, guides, minimap and region/audio overlays are separate controls,
not evidence that all modes work.

### R11: Assets

| Category | Independent acceptance focus |
|---|---|
| Art | Land/static distinction, transparency, dimensions, metadata, edit/save-back and overlay consumption |
| Gumps | ID lookup, alpha/size, editor handoff and actual runtime gump result |
| Anims | Body/action/direction/frame, origin/center, playback, sheet/VD exchange and equipment layers |
| Hues | Range semantics, partial hue, preview fidelity, document persistence and native staging |
| Textures | Dimensions, nearest preview, import/export and native staging |
| Multis | Components, relative offsets, heights, import variants and Multi Editor handoff |
| Maps | Facet/block/radar coordinates, lazy image work and jump-to-World consistency |
| Cliloc | IDs, encoding, placeholders, CSV conflicts, dirty documents and staged strings |
| Sounds | Playback ownership, stop/switch, WAV round trip, empty/invalid audio and staging |
| Bulk | Bounded work, preview/dry-run where available, cancellation and partial-write recovery |
| Art set | Source selection, provenance, missing assets, stable references and overlay precedence |
| Parity | Reference revision/input/settings, meaningful comparison and absence of false pass on missing reference |

Use `docs/editor/fiddler_parity.md` and the associated per-format documents as
coverage inputs, not proof that today's branch passes. R18 builds on Anims rather
than duplicating its document model.

### R16 and auxiliary docks

Admin subviews registered in `AdminView` include God View, Settings, Accounts,
Backups, Commands and Audit. Review each for permission denial, stale connections,
server response, cancellation and protected information. Shard start/stop/restart
and save/backup require distinct acceptance scenarios.

Inspector, Regions, Layers, Art, Static Studio, Logs and AI each need open/switch/
close/reload checks plus their own useful task. A successful World smoke does not
certify these docks. Hidden docks should not keep scanning data or starting services.

## First review sequence

1. **R21 + R01:** public instructions and clean setup. Establish an environment that
   can produce evidence without ambient maintainer dependencies.
2. **R09 + readiness across all registrations:** inventory default exposure and
   identify incomplete workflows. Define containment before adding navigation.
3. **R17:** Pixelorama acquisition and real paint/save-back acceptance; then evaluate
   the proposed menu. See [authoring integration plan](authoring-integrations.md).
4. **R18:** animation capability inventory and public setup; then deliver UO Anims
   in bounded vertical slices with round-trip proofs.
5. **R10–R16:** one workspace/category per review, starting with save/undo and staging
   safety. Preserve any concurrent authoring changes during review.
6. **R03–R08:** renderer/IO/network/gameplay performance and correctness against
   reproducible workloads, with R19/R20/R22 checks wherever the feature crosses them.

All sections remain **not fully reviewed** after this inventory. The specific
integration gaps in the companion plan are source/document findings; no new runtime,
visual, clean-machine or benchmark acceptance is claimed by this documentation.
