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

