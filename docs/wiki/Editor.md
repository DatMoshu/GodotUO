# Editor

A Godot editor add-on for looking at, editing and shipping UO world data,
built on the ported client rather than beside it. Sources of truth:
`docs\editor_plan.md`, ADR-0010, ADR-0011, ADR-0012, ADR-0015, and the
READMEs of `tools\editor_smoke`, `tools\editor_live`, `tools\editor_shard`,
`tools\world` and `tools\world_parity`.

The add-on lives in `godot\GUO\addons\guo_editor\`. It is enabled in
`project.godot`, so `launchers\editor\open_project.bat` opens with the UO
docks laid out: Assets on the left, World in the centre, Inspector on the
right, Shard in the bottom panel.

**UO Gumps** is the layout authoring tab for classic gump documents and native
Godot forms. It includes a canvas, layers, properties, undo/redo, classic
layout import/export and live-client capture/apply. See the
[Gump Studio guide](../ui/gump_studio.md) for commands, runtime integration
and the limits of custom-control captures.

## Shape (ADR-0010)

- The add-on is C#, compiled into the same assembly as the client behind
  `#if TOOLS`, so it can use the ported loaders and renderer directly.
- It implements `ISerializationListener`: Godot unloads and reloads the .NET
  assembly on every build, and a dock that does not save and restore its
  state across that reload loses it.
- A **windowed** editor run rewrites `project.godot` on exit and drops the
  comments in it; the headless smoke does not. Check the diff before
  committing after a windowed session.

## Phases

| Phase | What it delivers | State |
|---|---|---|
| 0 | The add-on skeleton, the docks, the smoke. | Done |
| 1 | The Assets dock's panels (`addons\guo_editor\Panels\`): art, gumps, animations, hues, sounds, clilocs, multis, map, plus a Parity panel that renders an asset through the port and through the reference reader side by side. | Done |
| 2 | The **World** tab: the client's own renderer embedded in the editor (ADR-0015), drawing any cell of the install at the client's size, with a season. `tools\world_parity` proves it draws what a logged-in client draws. | Done |
| 3 | The **world project** overlay (ADR-0011): edits are whole replaced 8x8 blocks stored as JSON under `UO_WORLD_PROJECT`, drawn over the install in the World tab. Tools, undo, and export to patched map files (`tools\world`). | Done |
| 4 | The **live tier** (ADR-0012): a UO Shard dock that talks to a private ModernUO instance through a bridge assembly, so a block edit is pushed to the shard and to connected clients (UltimaLive) while they play. | Done |
| 5 | **Asset edits** (ADR-0020, reserved as 0013): land art, static art, gumps and hues replaced from PNG in the world project's `assets\`, shown at once in the Assets dock and the World tab, exported as a `verdata.mul` and a patched `hues.mul` that any ClassicUO-lineage client loads through `files_override`. | Done |
| 6 | Shard world objects and backends (ADR-0014, reserved). | Not started |
| H | **Bulk import/export** (ADR-0022): the Assets dock's Bulk tab unpacks art, land, gumps, animations or tiledata to PNG plus JSON sidecars and packs an edited folder into a staged data set, by running `tools/uopack` and showing its lines; Verify re-checks the stage and the untouched install. See [Author UO Data](Author-UO-Data.md). | Done (unpack smoke-tested) |

"Done" here means the phase's check has been run and its README or ADR
records the run; the details are in each validation section.

## The world project (ADR-0011)

The ported `MapLoader` is not changed. A project replaces whole blocks, and
the editor repoints the loader's index entries for those blocks at the
project's data, so everything else reads the install untouched. The format is
`docs\data_formats.md` section 9. `docs\editor_plan.md` section 4.3 had
proposed one `PORT DEVIATION` in `MapLoader.cs` instead; the ADR chose the
index repointing, and the plan was not updated.

### Exporting it

```bat
launchers\pipeline\04_world_export.bat [--project DIR] [--out DIR] [--force]
```

`tools\world export` copies the install's land, `staidx` and `statics` files
for each facet the project touches and patches the copies in place; every
block the project does not replace stays byte for byte the install's. Beside
them it writes `files_override.txt` (ClassicUO's `files_override` format, so
either client can read the export) and `export.json` (fingerprint, blocks,
SHA-1 per file). `verify` reads the export back with an independent Python
reader and checks every block. An `--out` inside `UO_CLIENT_DATA` is refused;
a project made on a different install is refused without `--force`.

A server reads the export by listing the folder first in its data
directories (ModernUO: `dataDirectories`), ahead of the install.

## The live tier (ADR-0012)

A small bridge assembly is loaded into the private ModernUO instance and
listens on `UO_EDITOR_LIVE_HOST:UO_EDITOR_LIVE_PORT` (`127.0.0.1:2595`) for
JSON lines from editors. A block edit goes editor, bridge, shard, and the
shard sends the changed block to its clients with UltimaLive packets, which
the ported client already handles. `GUO_BRIDGE_MAPS` says which maps the
bridge serves. The wire format is `docs\data_formats.md` section 10.

### The private shard instance

```bat
python tools\editor_shard\run.py setup --from <built Distribution> [--port 2594]
python tools\editor_shard\run.py start [--data-first <export folder>]
python tools\editor_shard\run.py status
python tools\editor_shard\run.py bridge [--bridge-port 2595]
python tools\editor_shard\run.py stop
```

A copy of the built dev shard on port 2594 with its own saves, so live edits
and export checks never touch the shard people play on. `--data-first`
puts an export ahead of the install in its data directories.

## The checks

| Tool | Proves |
|---|---|
| `launchers\dev\editor_smoke.bat` (`tools\editor_smoke`) | The real editor opens headless, the add-on loads, the docks answer, an art id is found. `--windowed` for screenshots (takes the desktop's focus); `--reload` also rebuilds and hot-reloads the assembly. |
| `tools\editor_live\run.py` | Two editors and a client, end to end: an edit in editor A appears in editor B and in the client's UltimaLive log. Headless by default; `--windowed` for a client picture. |
| `tools\world_parity\run.py --at X,Y --windowed` | The World tab's frame of a cell against a logged-in client's frame of the same cell, pixel by pixel, masking the player, the paperdoll and the chat line. Measured 2026-09-27: 99.98% identical in the wilderness, 99.29% in Britain with the residual being shard decoration, 99.84% for a project drawn over the install against the client on its export. Plays on the private instance by default; the shared dev shard's port is refused without `--allow-shared`. |

| `tools\editor_asset_roundtrip\run.py` | A project's asset edits are exported, verified, and read back unchanged by a headless GUO client pointed at the export; a control run without the export must not match. `editor_smoke` runs it on its own fixtures. |

Output of all of them goes under `build\` and contains renders of client art,
so it is never committed.

For a task-by-task walk-through (setup, world view, export, live tier) see
[Manage Your Shard From the Editor](Manage-Your-Shard-From-The-Editor.md).
