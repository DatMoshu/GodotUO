# Manage Your Shard From the Editor

A walk-through of the GUO editor as a shard tool. It takes a map or art change
from the editor to a shard your players see, without ever writing to the UO
install. [Editor](Editor.md) is the reference: its design, phases and ADRs.
This page is the how-to.

What you can do today:

| Job | Where | State |
|---|---|---|
| Look at the world exactly as the client draws it | World tab | Done (ADR-0015) |
| Change terrain and statics: stamp, erase, raise and lower, hue, with undo | World tab | Done (ADR-0011) |
| Replace land art, static art, gumps and hues from PNG | UO Inspector | Done (ADR-0020) |
| Export all of it as files a client and a shard load | `tools\world` | Done |
| Push edits to a running shard and its players while they play | UO Shard dock | Done, private instance only (ADR-0012) |
| Spawners, decoration, vendors | none yet | Phase 6, not started |

## 1. Setup

1. **Point GUO at your install.** Put `UO_CLIENT_DATA` (and, if yours is not
   the default, `UO_CLIENT_VERSION`) in `launchers\_shared\config.local.bat`.
   See [Configuration](Configuration.md). The editor reads the same file as
   every launcher and tool, including when Godot is started some other way.
2. **Choose a world project folder.** Every edit lives in a *world project*,
   a folder of yours at `UO_WORLD_PROJECT` (default `build\world\default`).
   It is created on first use. Keep it outside `godot\GUO`: the editor
   refuses a project inside the Godot project. It can be its own git
   repository: every file in it is text or PNG, one file per changed map
   block or asset, so a change reviews as a diff.
3. **Open the editor.**

   ```bat
   launchers\editor\open_project.bat
   ```

   It builds the C# first. The UO docks load with it: **UO Assets** on the
   left, **UO Inspector** on the right, **UO Shard** in the bottom panel, and
   the **UO World** tab beside 2D/3D/Script.
4. **Check it works on your machine** (headless, a few minutes):

   ```bat
   launchers\dev\editor_smoke.bat
   ```

   It should end with `OK`. The Assets, Client and Export lines cover
   everything on this page except the live tier.

The editor never writes into `UO_CLIENT_DATA`. Edits go to the world
project, exports go to an export folder, and an export aimed inside the
install is refused before anything is written. The smoke checks this refusal
on every run.

## 2. The world view

Open the **UO World** tab, or pick a spot in **UO Assets > Maps** and press
**Show in UO World** in the UO Inspector. The tab runs the client's own renderer inside the
editor, so what you see is what a player sees at that spot (measured against
a logged-in client: 99.98% identical in the wilderness; the differences in
towns are the shard's own decoration). See the checks in [Editor](Editor.md).

- **Go:** type `x,y` and press Go.
- **Season:** a shard sends one per map; pick the one yours uses (the dev
  shard's Felucca is spring).
- **Tool:** what a left click does: select, stamp, erase, raise, lower, hue.
  **Stamp** places the static last picked in **UO Assets > Art (Statics)**.
  The hue box is decimal.
- **Undo / Redo:** Ctrl+Z / Ctrl+Y. One click is one step; up to 200 steps
  are kept.
- **Reload project:** re-reads the project from disk, after you have edited
  or pulled its files outside the editor.

Every edit is saved as it happens: the block's file in the project *is* the
save. There is no unsaved state to lose.

## 3. Replacing art, gumps and hues

Select something in **UO Assets** (Art, Gumps or Hues). The UO Inspector
shows it and says whether it comes from the install or from your project.

- **Save PNG...** writes it out to edit in any paint program. For a hue it
  is **Save strip PNG...**: a 32x1 strip, one pixel per colour.
- **Import PNG...** replaces it in the project. The Assets dock and the World
  tab switch to it at once.
- **Revert** deletes the replacement; the install's version is back.

Rules the import enforces (details in `docs\data_formats.md` §11):

- Land tiles are exactly 44x44, and only the diamond is used. Statics are up
  to 1024x1024, and gumps up to 2048x2048. A wrong size is refused and
  nothing is written.
- Colours are reduced to UO's 15-bit colour on import. The saved PNG is the
  reduced one, so what the file shows is what the client draws.
- In statics and gumps a pixel under half alpha is transparent. Opaque black
  is kept visible (stored as the nearest grey, `0x0421`), because 0 means
  transparent there.
- A hue keeps its name and table range; only the 32 colours change.

A hue edit shows in the Assets dock at once, and in the World tab after the
tab restarts.

## 4. Export

```bat
launchers\pipeline\04_world_export.bat
```

or `python tools\world\run.py export`, then `verify`. The export goes to
`<project>\export` unless you pass `--out`. It holds:

| File | What |
|---|---|
| `map<N>...`, `staidx<N>.mul`, `statics<N>.mul` | Copies of the install's map files with your blocks written in; every other block is byte for byte the install's |
| `verdata.mul` | Your replaced art and gumps, in upstream's own patch format. Any ClassicUO-lineage client applies a non-empty `verdata.mul` whatever its version. If your install already has one, its patches are kept and yours win on the same id |
| `hues.mul` | The install's, with your hues written in |
| `files_override.txt` | The list a client needs to read the export instead of the install |
| `export.json` | What was exported, from which project, onto which install |

`verify` reads the export back with a separate reader. It checks that every
changed block, image and hue equals your project, and that everything else
equals the install. A project made on a different install is refused unless
you pass `--force`.

The export is derived from your install, so it is **never committed or
redistributed**: `build\` and `*.mul` are gitignored.

### Getting it to players

- **The client:** set `files_override` in the client's `settings.json` to
  the export's `files_override.txt`. Checked headless on 2026-09-27
  (`tools\editor_asset_roundtrip`): a GUO client started this way applied the
  export's `verdata.mul` and decoded every replaced image with 0 pixels
  different. The same client without the override did not. A logged-in
  client's frame of replaced art in the world has **not been captured yet**:
  that needs a windowed run.
- **The shard:** list the export folder **first** in the shard's data
  directories (ModernUO: `dataDirectories` in `modernuo.json`), then restart
  it. Verified on the private instance: after `[go 1162 1666`, `[where`
  reports the altitude from the exported map, not the install's. On the
  shared dev shard these steps are **written, not verified**. The procedure
  is in `tools\editor_shard\README.md` and is the owner's to run, because it
  restarts the shard everyone plays on.

### Sending your edits to someone else's shard

You can't send the export folder: it holds copies of your install's map files
and `hues.mul`, which are not yours to hand out. Send a **pack** instead: one
zip of your edits and nothing derived from the install.

```bat
python tools\world\run.py pack
```

It writes `build\world_pack\<project>.zip`, holding:

- `project.json` (which client version and install it was made on);
- your changed map blocks, as JSON;
- your replaced art, gumps and hues, as PNG and JSON;
- a `README.txt` for whoever receives it.

Before writing anything it checks every file: block files parse, land tiles
are 44x44, statics and gumps are within size, hues have 32 colours. So a pack
that leaves your machine exports cleanly.

Whoever receives it (the shard owner, and each player) unzips it and runs
section 4 against **their own** install:

```bat
python tools\world\run.py export --project <unzipped folder> --out <export folder>
python tools\world\run.py verify --project <unzipped folder> --out <export folder>
```

The shard owner lists the export folder first in the shard's data
directories and restarts. Each player points `files_override` at their own
export's `files_override.txt`. If `export` refuses because the pack was made
on another install, check that both use the same client version, then add
`--force`.

Checked on 2026-09-27: a pack of one map block and four assets was unzipped
into another folder, then exported and verified from there.

## 5. The live tier, on a private instance

Live editing sends each edit to a running shard, which applies it in memory
and pushes it to every connected player through UltimaLive. Other editors see
it too, within a second. It runs only against a **private** copy of the dev
shard, never the shared one.

1. **Make the private instance** (once; it copies the built dev shard,
   without its archives, and listens on `127.0.0.1:2594` only):

   ```bat
   python tools\editor_shard\run.py setup --port 2594
   python tools\editor_shard\run.py bridge
   python tools\editor_shard\run.py start
   ```

   `bridge` installs the editor bridge assembly into the copy (stop the
   instance first if it is running). The bridge then listens for editors on
   `UO_EDITOR_LIVE_HOST:UO_EDITOR_LIVE_PORT` (`127.0.0.1:2595`).
2. **Go live:** in the **UO Shard** dock, check **Live**. The dock shows the
   bridge address and the name other editors see (`UO_EDITOR_NAME`). From
   then on every stamp, erase, altitude change, undo and redo in the World
   tab goes to the shard as the whole 8x8 block.
3. **Play alongside:** start a client against the private instance for that
   run only (`UO_SHARD_HOST=127.0.0.1`, `UO_SHARD_PORT=2594`), and log in
   with one of the dev shard's GM accounts (`guoeffects`, `guohighlight`,
   `guosweep`). An edit appears in the client without a restart.
4. **GM commands:** the dock's command line runs a command such as `[where`
   as a named online character.
5. **Make it permanent:** the shard keeps live edits in memory only, and your
   world project is the source of truth. Export (section 4) and restart the
   shard with the export first to keep them.
6. **Stop:** `python tools\editor_shard\run.py stop`. It stops only the
   instance `start` recorded, and cannot stop the shared shard.

Measured on 2026-09-27 (`tools\editor_live`): an edit's round trip to the
shard's acknowledgement took 51-57 ms, and a second editor received the block
35-46 ms after the first sent it. The client drew the new tree where the
editor draws it.

Conflicts: the last write to a block wins, and every relayed block names its
author in the dock's log.

**UOP installs work on every facet.** UltimaLive keeps the client's own copy
of each map. Upstream ClassicUO makes that copy blank on a UOP-only install
(no `map<N>.mul`). GUO builds a real copy instead (ADR-0012):

- from your export's `map<N>.mul` when the client uses the export's
  `files_override`, so the copy carries your edits;
- otherwise by converting the install's `map<N>LegacyMUL.uop` itself.

The private shard offers all six facets. Checked on 2026-09-27: with no
export at all, a client's copies of all six equalled the install block for
block, and a live stamp reached it.

A client keeps its copies once made. After you export a new version, delete
`%ProgramData%\GUO-Editor-Private\` on that machine so the next login makes
fresh ones.

## Checks, in one place

| Command | Proves |
|---|---|
| `launchers\dev\editor_smoke.bat` | Add-on, docks, every Assets panel, World tab, overlay, edit tools and undo. Also asset import, revert, export, verify, the client reading the export back, and the refusal to write into the install |
| `python tools\editor_asset_roundtrip\run.py [--project DIR]` | Your own project's assets survive export and are read back unchanged by a headless client |
| `python tools\world\run.py verify` | An export equals its project, and everything else equals the install |
| `python tools\editor_live\run.py` | Two editors and a client on the private instance, end to end |
| `python tools\world_parity\run.py --windowed` | The World tab against a logged-in client's frame. Takes the desktop's focus, so run it only when that is fine |

Output of all of them goes under `build\`, and holds renders of client art,
so it is never committed.
