# GUO Editor — plan for the in-editor UO day to day

**Audience:** the GUOEditor session (and whoever picks the work up after it).
**Status:** proposed 2026-09-26; becomes binding through the ADRs listed in §7.
**Read first:** `AGENTS.md`, `docs/port_plan.md`, `docs/data_formats.md`,
`docs/architecture/ADR-0001..0006`, `docs/uoww-reference.md`,
`tools/guoasset/README.md`.

---

## 1. What we are building, in one paragraph

Godot becomes the UO workbench: open the project and you can browse every
asset the client reads (art, gumps, animations, hues, tiledata, multis, cliloc,
sounds, maps), walk the world in a viewer that uses the *same* renderer the
game uses, edit terrain and statics in that viewer, and see the edit appear on
the running dev shard and in connected clients within a second. That is the
useful core of UOFiddler (asset browsing and inspection), CentrED (multi-user
map editing) and UltimaLive (live world patching), in one place, on top of
code the port already has. The editor is a *tool built on the port*, never a
fork of it: it reads through the ported loaders and draws through the ported
renderer, so what you see in the editor is what the client shows.

## 2. The one rule that shapes everything

**GUO never writes to the client install** (`AGENTS.md` rule 8,
`docs/uoww-reference.md`). An editor that edits maps looks like it violates
this. It does not, if edits live in a **world project**: a folder of ours
(`UO_WORLD_PROJECT`, default `build\world\default` in `config.bat`) holding an overlay over
the read-only install, exactly the way the client's own `mapdifN`/`stadifN`
and `verdata.mul` overlay the base files. The chain is:

```
client install (read only)  +  world project overlay  =  what the editor shows
                                     |
                       export ---> shard data folder (the shard's own copy)
                                     |
                       live   ---> UltimaLive packets ---> connected clients
```

Nothing in the plan writes into `UO_CLIENT_DATA`. The shard has its own copy
of the map files already (ModernUO reads `Data/` from its config); the export
step writes there, or writes `mapdif`/`stadif` files next to it. Asset edits
(art, gumps, hues) follow the same shape: an overlay the loaders consult
first, exported as a patch set, never injected into the install.

## 3. The day to day, as it should feel

A content person's morning, once this exists:

1. `launchers\editor\open_project.bat`. Godot opens with the **UO** dock
   group already laid out: Assets on the left, World in the centre, Inspector
   on the right, Shard in the bottom panel.
2. They search "0x0E75" (or "backpack") in the **Assets dock**, see the art,
   its tiledata flags, the animation frames, which multis use it, and the
   cliloc names that mention it. Double-click opens it in a tab; the tab is
   the same view for a gump, a tile, an animation or a hue.
3. They switch to the **World tab**, type "Britain" (or coordinates) and the
   world view jumps there: land, statics, multis, lights, drawn by
   `GameScene`'s own draw path, with the layer toggles, the altitude readout
   and the grid the game does not show.
4. They pick the stamp tool, choose a static, paint three of them on a
   street. The Inspector shows the exact static entries with x, y, z, hue. The
   Shard panel says *live: on*, and the client window running beside the
   editor shows the statics appear.
5. They press Ctrl+S: the world project saves a delta. `launchers\pipeline\`
   has a numbered step that exports the delta to the shard's data folder for a
   cold start, and the CI diff shows the changed blocks as before/after
   images.
6. They open the **Parity tab**, pick "Britain bank at 16:00", and get the
   reference image from `guoasset` beside the port's frame, with a diff.

Every one of those steps exists today in some form as a Python tool, an MCP
call, an adb line or a launcher. The editor work is mostly giving them one
surface and making the world editable.

## 4. Architecture

### 4.1 Where the code goes

```
godot/GUO/addons/guo_editor/          the EditorPlugin (C#, [Tool])
  plugin.cfg
  GuoEditorPlugin.cs                  registers docks, panels, main-screen tab
  Docks/AssetsDock.cs                 search + browse
  Docks/WorldView.cs                  main-screen world viewer/editor
  Docks/InspectorPanel.cs             selected asset / static / land cell
  Docks/ShardPanel.cs                 shard status, live toggle, log
  Tools/                              stamp, erase, altitude, hue, select
  Overlay/WorldProject.cs             the delta model, save/load, export
  Overlay/AssetOverlay.cs             art/gump/hue overrides for the loaders
godot/GUO/src/Editor/                 runtime-side hooks the addon needs
                                      (small; PORT DEVIATION where it touches
                                      ported code)
tools/world/run.py                    export a world project to the shard,
                                      diff two projects, render block images
tools/editor_smoke/run.py             headless checks of the addon
launchers/editor/open_project.bat     exists; add world_export.bat etc.
docs/architecture/ADR-0010..0013      see §7
```

The addon is a normal Godot `EditorPlugin`. Godot 4.7 mono supports C# tool
scripts; the addon lives in the same assembly as the game (one `GUO.csproj`),
gated behind `#if TOOLS` so the exported client carries none of it. That is
important for the Android and web builds another agent is producing: the APK
must not grow by the editor.

### 4.2 Reuse, not rewrite

| Need | Comes from | Notes |
|---|---|---|
| Read any asset | `src/Assets/*Loader.cs`, `src/IO/` | Already ported. The addon calls `UOFileManager.Load` once with the configured install, exactly as the game does. |
| Draw a tile/gump/anim | `src/Render/{Arts,Gumps,Animations}` atlases | Same textures the game uses; nearest sampling is preserved for free. |
| Draw the world | `GameScene` draw path via ADR-0001's presenter seam, `ChunkMesh` | The viewer instantiates the world renderer with an editor camera and no player. This is the one place a real runtime hook is needed (§4.4). |
| Reference images | `guoasset` MCP (`get_image`, `atlas_art`, `export_multi`) | Read-only; UOWW builds the binary. |
| Client data verification | `tools/uodata` | The Assets dock shows the manifest's gaps. |
| Live patching | `src/Game/UltimaLive.cs` | Already ported: handles the 0x3F/0x40 family that patches map and statics blocks at runtime. The editor's live path *produces* those packets. |
| Shard | `tools/modernuo`, `launchers/shard` | Add a small ModernUO script/patch that accepts block updates from the editor and rebroadcasts UltimaLive packets. |

### 4.3 The world project (overlay) model

- A world project is a folder: `project.json` (name, facet, base install
  hash from the uodata manifest), `blocks/<facet>/<bx>_<by>.json` (or a
  compact binary) holding **whole replaced blocks** for land and statics.
  Whole blocks, not cell deltas: it matches how `mapdif`/`stadif`, UltimaLive
  and the shard all think, keeps diffs reviewable per block, and makes undo a
  block ring buffer.
- The loaders get an overlay hook: `MapLoader` already has the patch layer
  logic for `mapdifN`/`stadifN`; the overlay plugs in at the same seam,
  consulted first. *As planned:* one `PORT DEVIATION` in `MapLoader.cs`.
  *As built (ADR-0011 supersedes this bullet):* the editor repoints the
  loader's `IndexMap` entries for replaced blocks at the project's data, so
  no ported file is changed for terrain at all.
- Export produces, per facet, either `mapdifN.mul`+`mapdifNl.mul` and
  `stadifN.mul`+`stadifNl.mul`+`stadifNi.mul` (the format the client and the
  shard already read) or full patched copies of `mapN.mul`/`staticsN.mul`
  into the *shard's* data folder. Never into the install. `tools/world`
  owns the format; extend `docs/data_formats.md` §7 before emitting a field.

### 4.4 The world viewer

- A main-screen plugin tab (like 2D/3D/Script) hosting a `SubViewport` that
  runs the port's world renderer in "editor mode": `GameController` is a
  Godot node (ADR-0006), so an editor-mode instance with a fixed camera and
  no network is the natural shape. The needed runtime hooks: start without a
  login (there is already `--play`/probe plumbing in `src/Bootstrap`),
  position the camera by coordinates, and expose picking (`PixelPicker`
  exists) so a click resolves to a static or land cell.
- Editor-only overlays drawn on top: grid, altitude numbers, block
  boundaries, selection outline, the stamp ghost. These are canvas items in
  the addon, not changes to the renderer.
- Performance target: the viewer must scroll as fast as the game (it is the
  game's renderer). Editing a block invalidates its `ChunkMesh` only.

### 4.5 Realtime edits

Two tiers, both worth having:

1. **Local live**: the viewer and a client launched from the editor share
   the overlay in-process (same assembly) so an edit shows in the client
   window immediately. Cheap, no shard involved; good for solo work.
2. **Shard live** (the CentrED/UltimaLive tier): the Shard panel connects to
   the dev shard's editor endpoint (a ModernUO script under
   `tools/modernuo/patches/` exposing a small TCP/JSON or the CentrED+
   protocol if adopting it is cheaper than inventing one). The shard applies
   the block, saves it to its data folder, and sends UltimaLive block updates
   to clients in range; `UltimaLive.cs` in GUO re-reads the block. Several
   editors can edit the same shard: last write per block wins, the panel
   shows who touched what (CentrED's model).

Decide in ADR-0012 whether to speak CentrED+'s protocol (gains their client
and community tooling; costs matching a moving target) or a GUO-specific one
(simpler; ours alone). Recommendation: start GUO-specific over the shard,
keep the message shapes block-based so a CentrED+ bridge stays possible.

### 4.6 The Assets dock (the UOFiddler part)

Panels, in priority order:

1. **Art & tiledata**: land and static art, flags, height, name; search by
   id/name/flag; "used by" (multis, animations' equip slots).
2. **Gumps**: browse, size, which gump classes reference the id (grep of
   `src/Game/UI` at load time, cached).
3. **Animations**: body, action, direction, frame scrubber, equipment
   layering preview via `Animation.cs`; `animdata` and body conversion
   tables shown as the client resolves them.
4. **Hues**: the palette, apply-to-preview on any art, partial hue toggle;
   uses `ShaderHueTranslator`, so it is the game's hue math.
5. **Multis**: composite view (guoasset already does this), component list.
6. **Cliloc & speech**: search, language switch.
7. **Sounds & music**: play through the ported audio (ADR-0005).
8. **Maps**: facet list, radar map, block picker that jumps the World view.

Editing assets is phase 3 and always overlay-based (§2). Viewing is phase 1.

### 4.7 Shard-side world objects: spawners, decoration, vendors

The map and statics are only half of a world. The rest lives on the shard:
who spawns where, what furniture and signs the decoration files place, which
vendor stands in which shop. The editor edits those too, in the World tab,
as a second family of tools beside the terrain ones.

**What the dev shard stores, and where** (ModernUO under `tools/modernuo/src`):

| Object | Stored as | Format |
|---|---|---|
| Spawners (monsters, animals, NPCs, vendors) | `Data/Spawns/<expansion>/<facet>/*.json` | JSON: `guid`, `name`, `location [x,y,z]`, `map`, `count`, `minDelay`/`maxDelay`, `homeRange`, `walkingRange`, `entries[{name,maxCount,probability}]` |
| Decoration (signs, furniture, doors, teleporters, moongates) | `Data/Decoration/<facet>/*.cfg` | text: `Type 0xItemID` header then `x y z` lines, with property lines (`Hue=`, `Name=`, `Facing=`) |
| Vendors | spawn entries whose `name` is a vendor class (`Tanner`, `Fisherman`, ...) | same as spawners; the shop inventory is code (`SBInfo`) not data |
| Regions, locations, quest and BOD data | `Data/regions.json`, `Data/Locations`, ... | JSON/cfg, read at boot |

Note the naming: **XmlSpawner is RunUO/ServUO's system; ModernUO does not
use it**. Its spawners are the JSON above (with `Engines/Spawners` in
UOContent). The editor edits ModernUO's native format. An importer from
XmlSpawner XML (the `<Points><Point ... Location=... MinDelay=... Objects=...>`
form) is worth having as a `tools/world` subcommand because so much community
content exists in it, but the editor's own model is the JSON.

**Editor features**

- **World Objects layer** in the World tab: spawners drawn as markers at
  their location with their home range as a ring; decoration items drawn
  with their actual art (the port draws art already) so a placed sign or
  bench looks as it will in game; vendors distinguished from monsters by
  icon. Toggle per kind, filter by name.
- **Inspector** edits the fields: entries table with probability and
  max-count, delays, ranges, hue, name, facing; "add entry" searches the
  shard's creature and vendor class names (`Data/categorization.json`,
  `names.json` and the assemblies' type list, read once).
- **Tools**: place spawner, place decoration item (art picker from the
  Assets dock), move by drag, duplicate, delete; drag the home-range ring.
- **Storage**: the edits are files in the shard's data folder, so the
  world project (§4.3) gains `shard/` holding changed spawn JSON and
  decoration cfg files, diffed by `guid` and by item line. Export copies them
  into the running shard's `Data/`; they take effect on the next boot, or
  live, below. The install is never touched: this is shard data, ours.
- **Live apply**: the shard is administered in game and the client already
  types GM commands for the launcher (`--shard-command`, `ShardCommands` in
  `src/Bootstrap`). Phase 6's first cut applies edits by driving a GM client
  with `[add`, `[props`, `[remove`, `[respawn` and the spawner property
  commands; it is slow but needs nothing new on the shard. The proper cut
  extends the same shard endpoint the terrain tier gets (§4.5) with
  spawner/decoration verbs, so an edit is applied and saved by the shard in
  one round trip and the marker in the editor turns from "pending" to
  "live". Both paths end in the JSON/cfg being the source of truth, so a
  boot from files and a live-edited world agree.
- **Vendor specifics**: placing a vendor is placing a spawner with one entry
  of a vendor class and `homeRange` 0-2; the inspector offers a "vendor"
  preset that fills those in and lists only shopkeeper classes. Shop
  inventory (`SBInfo`) is C# and out of scope for the editor; it is shown
  read-only so a content person can see what a `Tanner` sells.

### 4.8 Server backends: ModernUO, ServUO, RunUO and the rest

The dev shard is ModernUO, but the editor must not be a ModernUO editor.
Terrain and statics are already backend-neutral (every server reads the same
map/statics files, and UltimaLive is a client-side protocol). World objects
are where servers differ, so the editor gets a **backend adapter** layer:

```
editor model (neutral)  <->  tools/world/backends/<name>.py  <->  the shard's files
                              modernuo.py  servuo.py  runuo.py  ...
```

The neutral model is deliberately small: a *Spawner* (location, map, count,
delays, home range, walking range, entries[name, max, probability], plus an
opaque `extra` bag for backend-only fields), a *Placed item* (type, item id,
hue, location, facing, name, properties bag) and a *Region*. Each backend
reads its files into that model and writes them back, preserving fields it
does not understand through `extra` so a round trip is lossless. The editor
never sees a backend's file format; the Shard panel picks the backend from
`UO_SHARD_BACKEND` (config.bat, default `modernuo`) and the shard data path.

| Backend | Spawners | Decoration | Vendors | Live apply |
|---|---|---|---|---|
| **ModernUO** | `Data/Spawns/**/*.json` (guid, entries) | `Data/Decoration/<facet>/*.cfg` | spawn entries of vendor classes | GM commands now; shard endpoint later (§4.7) |
| **ServUO** | **XmlSpawner** `.xml` under `Spawns/` (`<Points><Point Name Map X Y Z Range MinDelay MaxDelay ... ><Object ... />`), plus classic `Spawner` items living only in the binary world save | `Data/Decoration/<facet>/*.cfg`, same format as ModernUO's (it is where ModernUO's came from) | XmlSpawner objects or `Spawner` entries | `[xmlload`, `[xmlsave`, `[xmlspawner` property commands, `[add`, `[props`, `[remove`; all typed by the GM client, no server change needed. Optional later: a small ServUO script exposing the same endpoint verbs. |
| **RunUO 2.x** | `Spawner` items in the world save; XmlSpawner if installed; Nerun's Distro ships XmlSpawner `.xml` sets | `Data/Decoration/*.cfg` (older single-facet layout) | `Spawner` entries | `[add`, `[props`, `[remove`; XmlSpawner commands if present. Binary saves are read-only for us: edits go through commands, not files. |
| **Sphere, UOX3, POL** | script files (`.scp`, `.dfn`, `.cfg`) | same scripts | same | out of scope until someone needs them; the adapter interface is the contract they would implement |

Consequences:

- **XmlSpawner is first-class after all**, as ServUO's and Nerun's format,
  not as ModernUO's. The importer from §4.7 becomes the ServUO backend's
  reader; the same code writes it, so the editor can author XmlSpawner sets
  for ServUO/RunUO shards directly.
- **Binary world saves are never parsed or written.** Where a server keeps
  spawners only in its save (classic RunUO `Spawner` items), the editor's
  source of truth is the *commands it sends*, logged to the world project as
  a replayable script (`shard/commands.log`), and the shard's own save is
  what persists them. That keeps us out of version-specific serialisation.
- **Live apply is one mechanism across backends**: the GM client typing
  commands. It is slow but universal (every RunUO-lineage server has `[add`,
  `[props`, `[remove`), and it is what the launcher's `--shard-command`
  already does. Backend adapters only translate the neutral model into the
  right command lines. The faster endpoint is an optimisation per backend,
  ModernUO first, never a requirement.
- **Vendors** differ only in class names; each backend ships a class list
  (ModernUO: `Data/categorization.json` + assembly types; ServUO/RunUO: the
  `Scripts/` type names, gathered by a one-off scan cached in the world
  project). The neutral model stores the class name as a string.
- **Dev shards for testing**: `tools/modernuo` exists; add `tools/servuo`
  in the same shape (fetch, configure, run launchers) when the ServUO
  backend is built, so phase 6's verification runs against both. RunUO 2.x
  can be verified on the ServUO shard with XmlSpawner disabled, which is
  close enough for the file formats it shares.

ADR-0014 covers the adapter interface and the "commands are the source of
truth for binary-save servers" rule; the Sphere/UOX3/POL row records that
they are explicitly not designed for yet.

## 5. Phases, each shippable on its own

| Phase | Deliverable | Verified by |
|---|---|---|
| 0 Scaffold | `addons/guo_editor` registers, `#if TOOLS` gate, Assets dock with art browser + inspector, `launchers/editor/open_project.bat` shows it | `tools/editor_smoke` runs the editor headless (`--editor --quit` with a script) and asserts the dock loaded and an art id rendered; screenshot |
| 1 Browse | All eight Assets panels, read-only; Parity tab with guoasset side-by-side | smoke asserts each panel opens against the real install; screenshots per panel |
| 2 View | World tab: renderer in editor mode, go-to, layers, grid, altitude, picking | screenshot of Britain matching a `launchers/dev/screenshot.bat` frame at the same spot (pixel diff) |
| 3 Edit | World project overlay, stamp/erase/altitude/hue tools, undo, save, `tools/world` export to the shard, block before/after renders | edit a block, export, restart the shard, connect the client, screenshot shows the static |
| 4 Live | Shard live tier via the ModernUO patch + UltimaLive; multi-editor | two editor instances + a client: edit in one, appears in the other and in the client within 1 s; log lines as evidence |
| 5 Asset edits | Overlay for art/gumps/hues, import PNG, export patch set | round trip: import, view in client, export, diff |
| 6 World objects | Spawner and decoration layer, inspector, place/move/delete, backend adapters (ModernUO JSON/cfg first, then ServUO XmlSpawner XML + cfg, RunUO via commands), export to the shard's data, live apply via GM commands then the ModernUO endpoint | place a vendor in the editor; after export + shard restart the vendor stands there in the client; then live: it appears without a restart |

Do 0-2 before touching anything writable. Phase 2 is where the port itself
gains: the viewer will surface renderer bugs faster than play does.

## 6. Conventions that apply

- Every ported file the editor needs a hook in gets exactly one
  `// PORT DEVIATION (GUO):` block; keep `tools/port_drift --strict` green.
- New tools follow `tools/<job>/run.py` + `README.md`, import `tools/guo`,
  and read config the same way (`UO_WORLD_PROJECT`, `UO_SHARD_DATA`,
  `UO_EDITOR_LIVE_PORT` go in `launchers/_shared/config.bat` and
  `tools/guo/config.py` together).
- Launchers: `launchers/editor/` for opening things, `launchers/pipeline/`
  numbered for export steps, every `.bat` calls `_shared\common.bat` first.
- Never commit rendered captures of client art; write them to `build/`.
- `godot-console` for anything scripted; the editor smoke must be runnable
  by an agent with no display beyond what Godot's own headless mode gives.
- Pixel art is never filtered: any `SubViewport` or `TextureRect` the addon
  creates sets nearest sampling explicitly.

## 7. Decisions to record as ADRs (write them as each phase starts)

- **ADR-0010 Editor addon shape**: one assembly, `#if TOOLS`, EditorPlugin
  docks and a main-screen tab; why not a separate project.
- **ADR-0011 World project overlay**: whole-block overlay over a read-only
  install, export formats, the invariant restated.
- **ADR-0012 Live editing transport**: shard endpoint, UltimaLive on the
  client side, GUO-specific vs CentrED+ protocol, conflict rule.
- **ADR-0020 Asset overlay** (reserved as 0013): how loaders consult overrides, what export
  produces, what is out of scope (injection into the install).
- **ADR-0014 Shard world objects and backends**: the neutral model and adapter
  interface, files vs commands as the source of truth per backend, live
  apply path, which servers are in and out of scope.

## 8. Agents and skills for this work

- `.claude/agents/uo-editor-engineer.md`: owns `addons/guo_editor`,
  `src/Editor`, `tools/world`, `tools/editor_smoke`; knows the invariant and
  the loaders; hands renderer questions to `uo-render-engineer` and packet
  questions (UltimaLive, the shard endpoint) to `uo-network-engineer`.
- Skills: `/editor-smoke` (run the headless checks and screenshots),
  `/world-export` (export a world project to the shard and restart it),
  `/parity-shot` (the Parity tab's job from the command line, for CI).

## 9. Open questions to settle early

1. Does Godot 4.7 mono reliably run C# `[Tool]` EditorPlugins from the game
   assembly with the loaders' unsafe code? Prove it in phase 0 before
   designing around it.
2. Renderer in the editor: does `GameController` come up cleanly without a
   window pin and network (ADR-0006)? Phase 2's first task.
3. UltimaLive's packet set in the ported `UltimaLive.cs` vs what ModernUO can
   send today; if ModernUO lacks a sender, the patch in
   `tools/modernuo/patches/` is the work.
4. Whether to adopt CentrED+'s protocol (§4.5).
5. Undo granularity for terrain (block ring buffer is the proposal; check it
   feels right with the altitude tool).

## 10. What "done" means for the first milestone

Phase 0-2 merged: open the editor, browse any asset, jump the world view to
Britain, and a screenshot of that view pixel-matches the client's own
screenshot at the same coordinates, with `editor_smoke` proving it headless.
No writable feature ships until that is true.

## Default layout (2026-10)

UO Assets is a main-screen tab (the whole centre: asset tabs, S/M/L cell size,
a zoomable radar in Maps with double-click to jump), supplied by the small
`addons/guo_editor_assets` plugin because one plugin owns one main screen. The
UO Inspector has the full height of the right column in front of Godot's
Inspector (preview on top, scrolling details below); Scene and FileSystem share
the left dock; the UO Shard dock is in the bottom panel. The World toolbar folds
the layer and guide toggles into Layers and Guides menus, and a minimap
(Guides > Minimap) sits in the view. The layout is applied once (a flag in the
editor's project metadata) or by Project > Tools > Reset GUO layout
(`GuoEditorPlugin.ResetLayout()`); a layout the user changed is not touched.
The tour records at 3840x2160 with display scale 1.5 on a scratch settings
folder, so the user's editor settings are never changed.

## AI hub (2026-10, ADR-0028)

The **AI** dock (`addons/guo_editor/AI/`, bottom panel beside UO Shard) has three tabs.

* **Chat** talks to Ollama (`/api/tags`, streaming `/api/chat`; the URL follows `OLLAMA_HOST`), to
  any OpenAI-compatible endpoint the user adds (key sealed by the pre-game accounts' `SecretStore`:
  DPAPI on Windows), and to an agent started in the Agents tab. All providers implement `IChatProvider`.
* **Agents** starts a coding agent CLI over ACP (`AcpClient`: JSON-RPC 2.0, one JSON message per
  line, `initialize`, `session/new`, `session/prompt`, `session/update`, `session/request_permission`,
  `session/cancel`). Presets: OpenCode, Codex, Claude Code, Gemini CLI, a custom command. They are
  found on PATH; a missing one shows the install and sign-in commands to run. Permission requests open a
  dialog, one action at a time (no "always" option is offered). The client advertises no file system
  and no terminal, so an agent can only ask.
* **Queue** posts to and reads `tools/agent_queue` (data_formats section 21) by running that tool;
  it lists requests and shows the selected one's replies, polling every 2.5 s while visible.

F3 has "AI: new chat", "AI: show queue/agents" and "AI: start agent X". Child processes die in
`AiDock.Shutdown`, which the plugin calls on close and before an assembly reload. The smoke's AI stage
runs everything against a fake ACP agent, stub HTTP servers and a temporary queue (`tools/ai_hub`).
The editor model tools are not built yet: the models see no editor state and have no tools.
