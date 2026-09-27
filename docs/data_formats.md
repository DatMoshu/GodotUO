# Data formats — the contract between tools and runtime

This document sits between the Python tooling under `tools/` and the C#
runtime under `godot/GUO/`. Both sides must agree on it. **Extend this
document before emitting a new field.**

Its machine-readable counterpart is `tools/guo/formats.py`. If you add an
entry there, describe it here; if you describe one here, register it there.
Neither half is authoritative alone.

---

## 1. Principles

**1. The UO client install is read-only input.**
The port reads a user's own legally obtained Ultima Online installation in
place, exactly as ClassicUO does. Nothing is written into it. No game data is
copied into this repository, and none is ever committed. The path comes from
`UO_CLIENT_DATA`; it is never hardcoded.

**2. Derived data is disposable.**
Anything the port computes from client data — decoded sprites, atlases, hue
LUTs — lives under `UO_CACHE_DIR`, outside the repo. Deleting that directory
must always be safe; the runtime rebuilds it on demand.

**3. Configuration has one source.**
`launchers/_shared/config.bat` holds the shared defaults; a user's own
values go in `launchers/_shared/config.local.bat` (gitignored), which
`config.bat` reads first. The launchers export the result as environment
variables; `tools/guo/config.py` reads those and falls back to parsing the
same two files when a tool runs outside a launcher.

---

## 2. Configuration keys

Every key resolves as: **environment variable → `config.local.bat` →
`config.bat` → central shared config (`UO_COMMON_CONFIG`, optional)**.

| Key | Meaning |
|---|---|
| `UO_CLIENT_DATA` | Folder holding the `.mul` / `.uop` / `.idx` files |
| `UO_CLIENT_VERSION` | Client version the data corresponds to (e.g. `7.0.107.76`) |
| `UO_CACHE_DIR` | Disposable decode cache |
| `UO_WORLD_PROJECT` | The editor's world project folder (§9); default `build\world\default` |
| `UO_EDITOR_LIVE_HOST` / `UO_EDITOR_LIVE_PORT` | The editor bridge the UO Shard dock connects to (§10); default `127.0.0.1:2595`, the private instance |
| `UO_EDITOR_NAME` | The name this editor shows other editors on the bridge |
| `UO_SHARD_HOST` / `UO_SHARD_PORT` | Shard to connect to |
| `GODOT_VERSION` / `GODOT_FLAVOR` | Pinned engine build |
| `UO_LOG_LEVEL` | `DEBUG` \| `INFO` \| `WARN` \| `ERROR` |

`UO_CLIENT_VERSION` must match the data in `UO_CLIENT_DATA`. A mismatch
produces failures during the network handshake that look like protocol bugs
but are not.

---

## 3. Client data registry

`tools/guo/formats.py` defines a `DataFile` per logical piece of client
data:

| Field | Meaning |
|---|---|
| `key` | Stable identifier used in the manifest and by the runtime |
| `mul` | Classic-format filenames |
| `uop` | UOP archive filenames that supersede `mul` on newer clients |
| `indexed_by` | Companion index files the `mul` form requires |
| `required` | Whether the client can boot without it |
| `subsystem` | Which port subsystem consumes it |

### Resolution rules

These are the rules both the tooling and the runtime must implement
identically.

1. **UOP supersedes MUL.** When both forms are present, the `.uop` archive is
   the live data. A leftover `.mul` beside it is stale and must be ignored,
   not merged.
2. **A `.mul` without its index is unusable.** Treat it as absent rather than
   attempting a partial read.
3. **Expanded maps supersede plain ones.** `mapNxLegacyMUL.uop` and
   `staticsNx.mul` / `staidxNx.mul` replace the plain facet entirely where
   present. They exist because Felucca and Trammel were enlarged; loading the
   plain form when the expanded one exists loads the wrong world.
4. **Patch layers apply last**, in this order, and are all optional:
   `verdata.mul` (overrides entries across several files), then `mapdifN` /
   `stadifN` (patch individual map and statics blocks).
5. **Filenames match case-insensitively.** Real installs mix
   `Gumpart.mul` and `gumpart.mul`, and the port must also run on
   case-sensitive filesystems.
6. **Only the required set blocks startup.** Missing optional data degrades
   gracefully.

### Facet coverage

Facets 0–5 are Felucca, Trammel, Ilshenar, Malas, Tokuno and TerMur. Only
facet 0 is required to boot. Each facet contributes up to five entries:
terrain (`mapN`), statics (`staticsN`), their two patch layers (`mapdifN`,
`stadifN`) and facet metadata (`facetNN`).

---

## 4. `client_manifest.json`

Written by `launchers\pipeline\01_verify_client_data.bat` to
`build/client_manifest.json`. Consumed by the runtime at startup and by the
port audit.

Schema id: **`guo/client_manifest@1`**

```jsonc
{
  "schema": "guo/client_manifest@1",
  "generated": "<ISO-8601 UTC>",
  "data_dir": "<absolute path to the UO install>",
  "client_version": "7.0.107.76",
  "packaging": "uop",          // "uop" if any UOP archive resolved, else "mul"
  "counts": {
    "total": 72,
    "satisfied": 62,
    "missing_required": 0,
    "missing_optional": 10
  },
  "ok": true,                  // false if anything required is missing/unreadable
  "entries": [
    {
      "key": "art",
      "description": "Land and static item sprites",
      "subsystem": "render/art",
      "required": true,
      "satisfied": true,
      "format": "uop",         // which form resolved: "uop" | "mul" | null
      "files": ["artLegacyMUL.uop"],
      "index_files": [],
      "missing_index": [],
      "sizes": { "artLegacyMUL.uop": 123456789 },
      "unreadable": [],
      "notes": ""
    }
  ],
  "stray_files": []            // data-looking files the registry does not know
}
```

### Consumer rules

- **Never boot on `"ok": false`.** Fail with the missing list, and point the
  user at the verify launcher.
- **`format` decides which reader to use** for that entry. Do not re-probe
  the filesystem and risk disagreeing with the manifest.
- **`stray_files` is a to-do list, not an error.** A non-empty list means the
  install contains data the port currently ignores — usually a newer client
  or custom shard content. Investigate before supporting a new client
  version.
- **Treat the manifest as a cache, not a source of truth about content.** It
  records what exists, not what the bytes mean.

---

## 5. `port_status.json`

Written by `launchers\pipeline\03_port_audit.bat` alongside the human-readable
`docs/port_status.md`.

Schema id: **`guo/port_status@1`**

Each upstream file carries:

| Field | Meaning |
|---|---|
| `upstream` | Path relative to `sources/ClassicUO/src` |
| `area` | Destination area under `godot/GUO/src` |
| `tier` | `verbatim` \| `shim` \| `rewrite` |
| `lines` | Line count, used to weight progress |
| `ported` | Whether a file of that name exists in the port |
| `port_paths` | Where it was found |

**`ported` matches by filename only.** It proves a file was created, not that
it is correct or complete. Never report it as "working".

---

## 6. `UPSTREAM_PIN.json`

`docs/upstream/UPSTREAM_PIN.json`, schema **`guo/upstream_pin@1`**, records
the ClassicUO commit whose changes have been reviewed for porting. It is
committed, so the whole team shares one answer to "reviewed up to where?".

`launchers\dev\sync_upstream.bat` reports commits after the pin, and flags
those touching already-ported files — the ones that rot silently. Re-pin with
`--pin` only after actually assessing them.

---

## 7. Adding support for a new data file

1. Add a `DataFile` entry to `tools/guo/formats.py`.
2. Document it here — what it is, which subsystem consumes it, and any
   resolution quirk.
3. Re-run `launchers\pipeline\01_verify_client_data.bat` and confirm it drops
   out of `stray_files`.
4. Only then write the reader, under `godot/GUO/src/IO` or `src/Assets`.

When a format detail is in doubt, there are two independent readers to check
against. Upstream ClassicUO's own loaders, which `tools/guoasset` compiles
from `sources/` and renders with. And [UOFiddler](https://github.com/polserver/UOFiddler),
the long-standing community tool (GPL-3.0; read it, do not copy from it),
whose `Ultima/` library most freeshard tooling agrees with. If GUO disagrees
with both, GUO is almost certainly wrong. Write whatever settles the question
into this document.

---

## 8. Licensing and provenance

ClassicUO is BSD 2-Clause; ported files keep their upstream copyright header.
`docs/upstream/` holds the licence text, the originating commit and the review
pin.

UO client data itself is proprietary and is **never** redistributed by this
project. Users supply their own installation.

---

## 9. World project (the editor's map edits)

The editor never writes to `UO_CLIENT_DATA`. Map edits live in a **world
project**, a folder of ours at `UO_WORLD_PROJECT`, laid over the read-only
install as whole replaced blocks. ADR-0011 has the reasoning.

```
<UO_WORLD_PROJECT>/
  project.json                  name, format, the base install it was made on
  blocks/<facet>/<bx>_<by>.json one file per replaced 8x8 block
  .cache/                       scratch; safe to delete; never committed
```

**`project.json`**

| Field | Meaning |
|---|---|
| `format` | `1` |
| `name` | The folder's name at creation |
| `created` | ISO 8601 UTC |
| `base.client_version` | `UO_CLIENT_VERSION` at creation |
| `base.fingerprint` | SHA-1 over the install's `map*`, `statics*`, `staidx*` file names and sizes, lower-cased and sorted. Tells one install from another; not a content hash |

**`blocks/<facet>/<bx>_<by>.json`** replaces the whole block: every land cell
and every static. A block not in the project is the install's.

| Field | Meaning |
|---|---|
| `format` | `1` |
| `facet` | Map index (`map0` is 0) |
| `block` | `[bx, by]`: block x and y, each cell coordinate divided by 8 |
| `land` | Eight strings, rows y = 0..7; each holds eight `ID:Z` cells for x = 0..7. `ID` is the land tile id in four hex digits, `Z` a signed decimal altitude |
| `statics` | One object per static: `id` (hex string, `0x0CCA`), `x` and `y` (cell within the block, 0..7), `z` (signed decimal), `hue` (hex string). Written sorted by y, x, z, id so an edit diffs as the lines it changed |

The block file mirrors the client's own block layout (`MapBlock`: a header
and 64 cells of id and z; `StaticsBlock`: id, x, y, z, hue), so a project
converts to `mapdif`/`stadif` or patched `map`/`statics` files without loss.
That export, into the **shard's** data folder and never the install, is
`tools/world` (phase 3). Extend this section before emitting a new field.

---

## 10. Editor bridge protocol (the live tier)

The editor's UO Shard dock talks to `tools/editor_shard/bridge`, a ModernUO
assembly, over TCP on `127.0.0.1:<UO_EDITOR_LIVE_PORT>`: one JSON object per
line, UTF-8, `\n`-terminated. ADR-0012 has the reasoning.

**Editor to bridge**

| `op` | Fields | What happens |
|---|---|---|
| `hello` | `editor` (name) | Answered with `hello` |
| `block` | `facet`, `bx`, `by`, `land`: 64 `[id, z]` pairs, row-major (index `y*8+x`), `statics`: `[id, x, y, z, hue]` per static (`x`, `y` 0..7 in the block), `sent_ms` (sender's clock, unix ms, optional) | Replaces the whole block in the server's own map (walking, line of sight and placement see it), pushes it to UltimaLive clients on that map, relays it to the other editors; answered with `ack` |
| `command` | `as` (an online character's name), `text` (e.g. `[where`) | Runs the GM command as that character (`CommandSystem.Handle`); answered with `command` |

**Bridge to editor**

| `op` | Fields |
|---|---|
| `hello` | `shard` (the UltimaLive shard name), `maps` (facets offered to UltimaLive clients), `seasons` (facet → ModernUO season number, which the editor adopts) |
| `ack` | `facet`, `bx`, `by`, `clients` (UltimaLive clients pushed to), `editors` (other editors relayed to), `ms` (time on the game thread) |
| `block` | as sent, plus `from` (the sending editor's name): another editor's block. Last write per block wins |
| `command` | `ok`, `as`, `text`, or `error` |
| `error` | `error` |

**Bridge to game client** (UltimaLive, as `src/Game/UltimaLive.cs` reads it)

- At login, after the login packets: `0x3F/0x02` (shard name), `0x3F/0x01`
  (map definitions for the listed facets), `0x3F/0xFF` (a hash query for the
  player's block, which the client needs before any update), then every block
  changed since boot.
- Per block: `0x40` (terrain, 201 bytes) then `0x3F/0x00` (statics).
- The client's `0x3F` hash replies are accepted and ignored.

The client keeps UltimaLive copies of the listed maps in
`%ProgramData%\<shard name>\`, made from `map<N>.mul` found through its
`files_override` (tools/world writes one beside every export).


---

## 11. World project assets (the editor's art, gump and hue edits)

Replaced art, gumps and hues live beside a world project's blocks (§9), under
`assets/`. ADR-0020 has the reasoning. As with blocks, the install is never
written.

```
<UO_WORLD_PROJECT>/assets/
  art/land/0xNNNN.png       land tile, by land id
  art/statics/0xNNNN.png    static, by item id (not 0x4000 + id)
  gumps/0xNNNN.png          gump, by gump id
  hues/0xNNNN.json          hue, by hue number (1-based, as shards write it)
```

**Images.** RGBA PNG, already reduced to UO colour: each channel's low three
bits are dropped (15-bit colour), so the file shows what the client will draw.

| Kind | Size | Transparency | Black |
|---|---|---|---|
| Land | exactly 44x44; only the 1,012 pixels of the diamond are used | none | stored as 0 |
| Static | up to 1024x1024 | alpha < 128 is transparent (0) | opaque black is stored as `0x0421` |
| Gump | up to 2048x2048 | alpha < 128 is transparent (0) | opaque black is stored as `0x0421` |

**`hues/0xNNNN.json`**

| Field | Meaning |
|---|---|
| `format` | `1` |
| `hue` | Hue number, 1-based: group `(hue-1)/8`, entry `(hue-1)%8` of `hues.mul` |
| `name` | Up to 20 ASCII characters |
| `table_start`, `table_end` | Hex strings, as `hues.mul` stores them |
| `colors` | 32 hex strings, the 16-bit colours as stored (four rows of eight) |

**Export** (`tools/world export`, into an export folder, never the install):

| File | Contents |
|---|---|
| `verdata.mul` | `int32 count`, then `count` records of five `uint32` (file id, block, position, length, extra), then the data. Art is file id 4, block = land id or `0x4000` + item id, in the art layouts below. Gumps are file id 12, extra = `width << 16 \| height`. The install's own patches are kept unless the project replaces the same id. 16 zero bytes pad the end |
| `hues.mul` | The install's, with each replaced hue's 88 bytes (32 colours, start, end, 20-byte name) written in place |
| `files_override.txt` | Adds `verdata.mul=` and `hues.mul=` lines |
| `export.json` | Gains `assets`: the ids per kind and the files' SHA-1 |

Layouts, as `ArtLoader` and `GumpsLoader` read them (and as
`tools/guo/uoart.py` and `AssetOverlay.cs` write them):

- **Land**: 1,012 little-endian `ushort` colours, the diamond row by row
  (row `y` < 22 starts at `x = 21 - y` and is `2(y+1)` wide; the lower half
  mirrors it).
- **Static**: `uint32 flags (0)`, `ushort width`, `ushort height`, a row
  table of `height` `ushort` offsets (in words, from the table's end), then
  per row `(gap, run, run colours...)` spans ended by `(0, 0)`. Transparent
  pixels are gaps.
- **Gump**: a row table of `height` `int32` offsets (in 4-byte units, from
  the start), then per row `(ushort colour, ushort run)` pairs covering the
  whole width; colour 0 is transparent.

None of these files are committed: they derive from the install.
---

## 12. GUO Asset Store packs (ADR-0019)

A pack is a ZIP with one UTF-8 `manifest.json` at its root. Schema id:
**`guo/store-pack@1`**. It contains only user media/settings, never UO client
data or executable code. `art-override` is reserved and rejected.

```json
{
  "schema": "guo/store-pack@1",
  "id": "moongate-shimmer",
  "version": "1.0.0",
  "kind": "background",
  "title": "Moongate shimmer",
  "author": "GUO contributors",
  "licence": "CC0-1.0",
  "min_profile_version": 6,
  "preview": "still.png",
  "files": { "still.png": "<64 lowercase hex SHA-256 digits>",
             "loop.ogv": "<64 lowercase hex SHA-256 digits>" }
}
```

- IDs match `[a-z0-9][a-z0-9-]{0,63}` (Windows device names excluded).
  Versions are three decimal components, each 0..2147483647, without
  leading zeros. Compare numerically, not lexicographically.
- Kinds: `background`, `theme`, `sound`, `profile-preset`. Licence allowlist:
  `CC0-1.0`, `CC-BY-4.0`, `CC-BY-SA-4.0`, `MIT`, `BSD-2-Clause`,
  `BSD-3-Clause`, `Apache-2.0`. Publishers are responsible for provenance;
  the identifier does not establish ownership. Non-CC0 packs must include
  `LICENSE.txt` with the licence and attribution.
- `title` and `author` are nonempty strings, at most 200 characters.
  `min_profile_version` is an integer 0..2147483647; newer requirements
  block installation. `preview` names a declared PNG/JPG/JPEG/WebP file.
- `files` maps every payload path to its SHA-256 (manifest excluded).
  Payload types: `.png`, `.jpg`, `.jpeg`, `.webp`, `.ogv`, `.ogg`, `.wav`,
  `.json`, `.txt`. A background includes at least one image or `.ogv`.
  `.mul`, `.uop`, `.idx`, `.def`, and any basename beginning `cliloc`
  are forbidden case-insensitively, even when renamed with another suffix.
- Paths use `/`, are relative, have no empty, `.` or `..` components,
  backslashes, colons, control characters, Windows reserved device names,
  trailing dots/spaces or Windows special characters. Each component is
  at most 100 characters, each path at most 240. Duplicate paths (including
  case aliases), symlinks, encrypted entries, directory entries, undeclared
  files, and ancestor/file collisions are rejected. No automatic extraction
  API is trusted to perform path validation.
- Limits: 512 MiB ZIP, 1 GiB total uncompressed payload, 256 MiB per
  payload, 1 MiB manifest, 1024 payload files. Checks run before extraction;
  actual bytes and hashes are verified while reading.

### Store index and publication

`UO_STORE_DIR` defaults to `build/store_cdn` relative to the checkout;
`UO_STORE_URL` defaults to `http://127.0.0.1:18865`. The root has `index.json`
with schema `guo/store-index@1` and a `packs` array. Each entry is the pack
manifest plus `url` (`packs/<id>/<version>.zip`), `sha256` (whole ZIP), and
`size` (ZIP bytes). Paths are relative to the index's directory. Previews
are published at `previews/<id>/<version>/<preview>` and named by
`preview_url`. The web page renders metadata as text, never HTML.

Publication validates before writing; an existing id/version is immutable
(identical bytes are a no-op). Rebuilding an index verifies all published
ZIPs; replacement of the index is atomic. HTTP serves GET/HEAD and single
byte ranges (`206`, `Content-Range`); unsatisfiable ranges return `416`.

### Installation and removal

The client verifies the downloaded ZIP hash from the index, then validates
the manifest and every file hash. It installs through a temporary sibling
directory and an atomic rename to `user://store/<id>/<version>`, storing
the manifest alongside the payload. It never writes into the UO install.
An installed version is immutable. Failed installs remove only their own
temporary directory. Uninstall removes only the selected id/version under
the store root. Updates select a numerically newer compatible version;
older installations remain until explicitly removed. Hashes detect corrupt
downloads; trust in the publisher comes from the configured store URL
(use HTTPS for a remote store).
