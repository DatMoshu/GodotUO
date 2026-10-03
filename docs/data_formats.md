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
| `object` | `action` `put` with `kind` (`spawner` or `item`) and `object` (as in `shard/objects.json`, section 13); or `action` `delete` with `kind` and `id` | Applies it to the world with the boot sync's code (ADR-0014), relays it to the other editors; answered with `object_ack` |
| `multi` | `action` `place` with `tag`, `id` (multi id), `map`, `x`, `y`, `z` (optional: the land's average z), `doors` (as in a built multi's `multi.json`, section 16); or `action` `remove` with `tag` | Places an authored multi (`GUOAuthoredMulti`) and a real door per entry, replacing a multi with the same tag; `remove` deletes it and its doors. Answered with `multi_ack` |

**Bridge to editor**

| `op` | Fields |
|---|---|
| `hello` | `shard` (the UltimaLive shard name), `maps` (facets offered to UltimaLive clients), `seasons` (facet → ModernUO season number, which the editor adopts) |
| `ack` | `facet`, `bx`, `by`, `clients` (UltimaLive clients pushed to), `editors` (other editors relayed to), `ms` (time on the game thread) |
| `block` | as sent, plus `from` (the sending editor's name): another editor's block. Last write per block wins |
| `command` | `ok`, `as`, `text`, or `error` |
| `object` | as sent, plus `from`: another editor's world-object change. Last write per object wins |
| `object_ack` | `action`, `kind`, `id`, `outcome` (`Added`, `Changed`, `Kept`, `Deleted`, `Missing`, `Skipped`), `editors`, `ms` |
| `multi_ack` | `action`, `tag`, `ok`; on a place `serial`, `at` `[x, y, z]`, `components`, `doors`, `replaced`; on a remove `removed`; or `error` |
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
  art/texmaps/0xNNNN.png    texmap (optional), by texmap index: the TexID in
                            tiledata of the land tiles that use it
  gumps/0xNNNN.png          gump, by gump id
  hues/0xNNNN.json          hue, by hue number (1-based, as shards write it)
  tiledata.json             static tiledata rows (optional): {"0xNNNN": {"flags", "height", "name", "weight"}}
```

**Images.** RGBA PNG, already reduced to UO colour: each channel's low three
bits are dropped (15-bit colour), so the file shows what the client will draw.

| Kind | Size | Transparency | Black |
|---|---|---|---|
| Land | exactly 44x44; only the 1,012 pixels of the diamond are used | none | stored as 0 |
| Static | up to 1024x1024 | alpha < 128 is transparent (0) | opaque black is stored as `0x0421` |
| Gump | up to 2048x2048 | alpha < 128 is transparent (0) | opaque black is stored as `0x0421` |
| Texmap | exactly 64x64 or 128x128 | none | stored as 0 |

A texmap is the texture the client stretches over **sloped** land (a cell whose
corners differ in height); flat land draws the land art instead. So a replaced
land tile that sits on slopes needs its texmap too, or the slopes keep the old
look. An index that `TexTerr.def` redirects to another entry never shows its own
texture; export warns about it.

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
| `tiledata.mul` | Only with `tiledata.json`. A copy of the install's with each listed static row written: flags (hex string or number), weight (default 255), height, name (20 bytes); layer, count, anim, hue and light 0. A row past the file's static count is refused |
| `texmaps.mul`, `texidx.mul` | Only with `art/texmaps/`. Copies of the install's: each replaced texmap's 16-bit colours (row by row, 0x2000 or 0x8000 bytes) are appended to `texmaps.mul`, and its `texidx.mul` record (int32 offset, int32 length, int32 extra, extra kept) points at them. Every other record and byte is the install's. Upstream never applies verdata texmaps (file id 10), hence copies |
| `hues.mul` | The install's, with each replaced hue's 88 bytes (32 colours, start, end, 20-byte name) written in place |
| `files_override.txt` | Adds `verdata.mul=`, `hues.mul=`, `texmaps.mul=` and `texidx.mul=` lines, for the files written |
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

Version 2 content envelopes, deployment targets, dependencies, component
identities and activation locks are specified in
[asset_pack_ecosystem.md](asset_pack_ecosystem.md). V1 below remains the
presentation-pack contract. V2 installation is inert; individual runtime
consumers require their own activation evidence.

A pack is a ZIP with one UTF-8 `manifest.json` at its root. Schema id:
**`guo/store-pack@1`**. It contains user media/settings and the explicitly
allowed shader/script formats below, never UO client data or native/.NET
executables. `art-override` is reserved and rejected.

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

- The manifest is UTF-8 **without** a byte order mark, strict JSON (no
  `NaN` or `Infinity`), no duplicate keys, and every string valid Unicode
  (no lone surrogates).
- IDs match `[a-z0-9][a-z0-9-]{0,63}` (Windows device names excluded).
  Versions are three decimal components, each 0..2147483647, without
  leading zeros. Compare numerically, not lexicographically.
- Kinds: `background`, `theme`, `sound`, `profile-preset`, `screensaver`, `postfx`, `razor-script`. Licence allowlist:
  `CC0-1.0`, `CC-BY-4.0`, `CC-BY-SA-4.0`, `MIT`, `BSD-2-Clause`,
  `BSD-3-Clause`, `Apache-2.0`. Publishers are responsible for provenance;
  the identifier does not establish ownership. Non-CC0 packs must include
  `LICENSE.txt` with the licence and attribution.
- `title` and `author` are strings of at most 200 characters, not only
  whitespace, with no control characters (U+0000-U+001F, U+007F).
  `min_profile_version` is a JSON integer 0..2147483647 (not `true`,
  `11.0` or `"11"`); newer requirements
  block installation. `preview` names a declared PNG/JPG/JPEG/WebP file.
- `files` maps every payload path to its SHA-256 (manifest excluded).
  Payload types: `.png`, `.jpg`, `.jpeg`, `.webp`, `.ogv`, `.ogg`, `.wav`,
  `.json`, `.txt`. A background includes at least one image or `.ogv`.
  A `screensaver` has **exactly one** `.ogv` loop (played by the client's
  idle screen saver) and `min_profile_version` of at least **11**, the
  first profile version that can pick one; both the publisher and the
  installer reject anything else. Its `preview` is the still the Store
  shows.
  `.razor` is allowed only in `razor-script` packs, which require at least
  one such file. Each script is nonempty UTF-8 text (an optional UTF-8 BOM
  is accepted), at most 262144 bytes and 65536 UTF-16 code units after BOM
  removal; control characters other than tab, CR and LF are refused.
  Both publisher and installer validate the text, not its CE semantics.
  The kind means Razor CE command scripts, not Razor Enhanced Python or
  ASP.NET Razor templates. Other supported media/text files may accompany it;
  existing licence, preview, path and hash requirements still apply.
  `.mul`, `.uop`, `.idx`, `.def`, and any basename beginning `cliloc`
  are forbidden case-insensitively, even when renamed with another suffix.
- Paths use `/`, are relative, have no empty, `.` or `..` components,
  backslashes, colons, control characters, Windows reserved device names,
  trailing dots/spaces or Windows special characters. Windows device
  names include the superscript forms (`COM¹`). A component must have a
  stem: `.png` alone is refused. Each component is at most 100 and each
  path at most 240 **UTF-16 code units** (what the file system counts; an
  emoji is two). Duplicate paths, symlinks, encrypted entries, directory
  entries, undeclared files, and ancestor/file collisions are rejected.
- Case aliases: two names are one name when they share a folding key.
  Turkish dotted and dotless i count as i. Then each character folds by its
  simple one-to-one lower and upper mapping, as NTFS and .NET do: `K` (Kelvin
  sign) and `k` alias, `ß` and `ss` do not.
- A non-ASCII ZIP entry name must carry the ZIP's UTF-8 flag (bit 11).
  Without it the name is ambiguous (CP437 by the spec, UTF-8 to many tools).
- `tools/asset_store/corpus/cases.json` holds a case for each of these
  rules. `tools/asset_store/test_corpus.py` runs the Python publisher and
  the C# installer over the same built folder, and CI requires both to
  agree with every case. No automatic extraction
  API is trusted to perform path validation.
- Limits: 512 MiB ZIP, 1 GiB total uncompressed payload, 256 MiB per
  payload, 1 MiB manifest, 1024 payload files. Checks run before extraction;
  actual bytes and hashes are verified while reading.

### Store index and publication

`UO_STORE_DIR` defaults to `build/store_cdn` relative to the checkout;
`UO_STORE_URL` defaults to `http://127.0.0.1:18865`. A character may override
the address through Store's **Save & connect** field. The profile directory's
`store-address.txt` sidecar contains one normalized absolute HTTP(S) base URL
(host, optional port and path), saved atomically. Credentials, query strings
and fragments are rejected. Missing sidecars use `UO_STORE_URL`; no profile
schema migration is needed. This setting changes the catalogue endpoint,
not installation paths or pack verification. The root has `index.json`
with schema `guo/store-index@1` and a `packs` array. Each entry is the pack
manifest plus `url` (`packs/<id>/<version>.zip`), `sha256` (whole ZIP), and
`size` (ZIP bytes). Paths are relative to the index's directory. Previews
are published at `previews/<id>/<version>/<preview>` and named by
`preview_url`. The web page renders metadata as text, never HTML.

Ids beginning `sample-` are reserved for generated sample packs
(`tools/asset_store/samples.py`); catalogues label them as samples, and a
real pack must not use the prefix.

Publication validates before writing; an existing id/version is immutable
(identical bytes are a no-op). Rebuilding an index verifies all published
ZIPs; replacement of the index is atomic. HTTP serves GET/HEAD and single
byte ranges (`206`, `Content-Range`); unsatisfiable ranges return `416`.

### Signed catalogue index, `guo/store-index@2` (ADR-0026)

A catalogue that signs its index publishes two files at its root:
`index.json` and `index.json.sig`. The signature file is one line,
`ed25519:` followed by the standard base64 of the 64-byte Ed25519 (RFC 8032)
signature over the **exact bytes** of `index.json`. Nothing is
canonicalised, so the index is never re-serialised between signing and
verifying.

```json
{
  "schema": "guo/store-index@2",
  "catalogue": { "id": "guo-official", "title": "GUO packs", "homepage": "https://..." },
  "key": "ed25519:<base64 of the 32-byte public key>",
  "sequence": 1791043200,
  "issued": "2026-10-02T20:00:00Z",
  "expires": "2026-11-01T20:00:00Z",
  "packs": [
    {
      "manifest": { "...": "the pack's manifest.json, as section 12 above" },
      "sha256": "<64 lowercase hex digits of the whole ZIP>",
      "size": 123456,
      "urls": ["packs/moongate-shimmer/1.0.0.zip", "https://mirror.example.org/moongate-shimmer-1.0.0.zip"],
      "preview_url": "previews/moongate-shimmer/1.0.0/still.png",
      "provenance": "Rendered in Blender from an original scene"
    }
  ]
}
```

- `catalogue.id` follows the pack id syntax. `title` is at most 200
  characters, with no control characters. `homepage` is optional.
- `key` names the signing key. A client checks that it is the key it trusts
  for this catalogue (below); naming it only lets a new catalogue be
  approved.
- `sequence` is an integer that only rises. The publisher uses at least the
  current Unix time in seconds, and at least the previous index's number
  plus one. A client refuses an index whose sequence is lower than the
  highest it has seen from that catalogue.
- `expires` is optional. A client refuses an index past it.
- `urls`: 1 to 16 places to fetch the ZIP, tried in order. A relative URL
  resolves under the index and may not leave it. An absolute URL is HTTPS,
  or plain HTTP only to loopback or a private LAN address (10/8,
  172.16/12, 192.168/16, 169.254/16, IPv6 loopback, link-local and
  unique-local, `localhost`, `*.lan`, `*.local`). No credentials and no
  fragment. Downloads from a signed index may follow up to five redirects,
  never from HTTPS to HTTP; the hash and size decide whether the bytes are
  the pack.
- `provenance` is optional, at most 500 characters, no control characters:
  how the content was made (the content policy, `docs/store/content_policy.md`).
- A `guo/store-index@1` index (above) is still read, and the client shows its
  packs as **unsigned**. `run.py` writes v2 whenever `UO_STORE_SIGNING_KEY`
  is set, and v1 otherwise.

**The client's trust list** is `user://store/.catalogues.json`, an array of
`{"url", "id", "title", "key", "sequence"}`. A catalogue added by URL has no
`key` until the player approves the fingerprint the Store shows: the first
16 hex digits of the key's SHA-256, in groups of four. A different key at a
known address is refused until the player approves it again. The official
catalogue (`StoreTrust.OfficialUrl`) is trusted by the keys compiled into the
client (`StoreTrust.OfficialKeys`), and cannot be removed.

**Listing files** (`run.py build-catalogue`, the catalogue repository): one
JSON file per pack version at `packs/<id>/<version>.json`, with exactly the
fields `urls` (HTTPS), `sha256`, `size` and an optional `provenance`. The
builder downloads each ZIP once from the first URL that serves the listed
bytes, verifies it as a pack, and lists it. In a store folder,
`listing/<id>/<version>.json` holds the same fields; there `urls` adds
mirrors after the store's own copy, and `sha256` and `size` are not needed.

**Secret key file** (`run.py keygen`): one line, `guo-catalogue-secret `
followed by `ed25519:` and the base64 of the 32-byte seed. It is never
committed, and never leaves the publisher's machine or CI secret store.

### Shard content descriptor, `guo/shard-content@1` (ADR-0026)

What a shard runs, for its players' clients. `tools/shard_content/run.py
deploy` writes it to `<shard>/Data/GUO/public/shard-content.json`; the shard
serves it from any web server, and its server-list entry names the address
as `content` (`servers.json`).

```json
{
  "schema": "guo/shard-content@1",
  "shard": {"name": "Example Shard", "host": "play.example.com", "port": 2593},
  "catalogues": [{"url": "https://packs.example.com/", "key": "ed25519:<base64>"}],
  "lock": {"schema": "guo/content-lock@1", "pack": "example-pack", "version": "1.0.0",
           "identity_hash": "<64 hex>", "bindings": {"example-pack:stone": {"type": "static", "id": 6001}}},
  "scripts": "forbidden"
}
```

- At most `StorePack.MaxManifest` bytes, unique keys, fetched over HTTPS (HTTP
  only on this computer or the LAN) with no redirects.
- `shard.name` is 1 to 200 characters, no control characters. `host` and
  `port` are informational; the server entry decides where the client connects.
- `catalogues`: 1 to 16 distinct catalogue addresses, HTTPS or local HTTP.
  `key` is the catalogue's signing key. It may be left out only for HTTP on
  this computer or the LAN, and then the catalogue must be unsigned. A signed
  catalogue named without a key, or one answering with a different key, is
  refused.
- `lock` is a deployment lock (above). The client installs `pack` `version`
  and its dependencies from the catalogues, and the installed closure must
  hash to `identity_hash`.
- `scripts` is `allowed` (the default) or `forbidden`. When forbidden, the
  client's script packs do not run while it plays on that shard.

The player's yes to the shard's question approves the keys the descriptor
names. The client writes the lock to `<store>/.shard-content/<first 16 hex of
identity_hash>.json` and restarts. The shard session (`shard_session.json`)
records `content_url`, `content_lock`, `content_identity` and
`scripts_allowed`; at the next start the lock is mounted for that run only, as
`UO_CONTENT_LOCK` would mount it. An explicit `UO_CONTENT_LOCK` still wins.

The same deploy writes the neutral server export, `guo/server-content@1`, to
`<shard>/Data/GUO/server-content.json`. The ModernUO bridge loads it at start
when `UO_SERVER_CONTENT` is not set, and logs its `identity_hash`.

### Screensavers in the client (profile v11)

`Profile.ScreenSaverChoice` (JSON `screen_saver_choice`, default
`"effects"`) names what the idle screen saver shows:

| Value | Shows |
|---|---|
| `effects` | the drifting UO effects (the v10 saver) |
| `builtin:<name>` | a loop listed in `godot/GUO/assets/screensavers/screensavers.json` |
| `user://store/<id>/<version>/<loop>.ogv` | an installed `screensaver` pack's loop |

`screensavers.json` is an array of `{"name", "title", "video", "still"}`,
paths relative to the folder, like `backgrounds.json`. An entry with
`"store_only": true` is not offered in the client: `seed.py` publishes it as
a `screensaver` pack instead. Every choice keeps the whole frame moving: a
loop is scaled to overfill the screen by 6% on each side and drifts along a
slow path through that margin, with the drifting "Screen saver" label on
top. A missing or unplayable loop falls back to the effects. Uninstalling
the chosen pack resets the choice to `effects`.

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

---

## 13. World objects (the editor's spawners and decoration)

Spawners and placed items live in the world project beside its blocks (§9)
and assets (§11), in one server-neutral file. ADR-0014 has the reasoning.

```
<UO_WORLD_PROJECT>/shard/objects.json
```

`format` is `1`. `spawners` and `items` each hold one object per line, sorted
by map, y, x, id.

**Spawner**

| Field | Meaning |
|---|---|
| `id` | GUID, made by the editor; becomes the server's spawner GUID where it has one |
| `map` | `Felucca`, `Trammel`, `Ilshenar`, `Malas`, `Tokuno` or `TerMur` |
| `x`, `y`, `z` | Where the spawner stands |
| `count` | How many it keeps alive |
| `min_delay`, `max_delay` | `hh:mm:ss` |
| `home_range`, `walking_range`, `team` | As the server means them; `walking_range` `-1` is unlimited |
| `entries` | `[{name, max, probability}]`; `name` is a creature or vendor class on the shard |
| `extra` | Backend-only fields, kept through a round trip |

**Item**

| Field | Meaning |
|---|---|
| `id` | GUID, made by the editor; GUO's own key |
| `map`, `x`, `y`, `z` | Where it stands |
| `item_id`, `hue` | Hex strings |
| `type` | The server's item class; `Static` (the only one synced so far) |
| `props`, `extra` | Server properties (`Name=`, `Facing=`, ...), and backend-only fields |

**ModernUO export** (`tools/world export`, under `<export>/shard/`):

| File | Contents |
|---|---|
| `Data/Spawns/guo/<project>.json` | ModernUO `SpawnerDto` records: `$type`, `guid` (= `id`), `name`, `location [x,y,z]`, `map`, `count`, `minDelay`, `maxDelay`, `team`, `homeRange`, `walkingRange`, `entries [{name, maxCount, probability}]` |
| `Data/Decoration/<Map>/guo-<project>.cfg` | ModernUO decoration: a `Type 0xNNNN (Prop=value; ...)` header, one `x y z` line per item, a blank line after each group |
| `guo_objects.json` | The manifest the bridge syncs from: `spawners [{id, map, location}]`, `items [{id, map, location, item_id, hue, type, props}]`, `files` |
| `APPLY.txt` | GM steps for a shard without the bridge, and what they cannot do |

The shard's record of what GUO applied is not a file: it lives in the world
save as the `GUOWorldObjects` persistence (spawner GUID to a record hash, item
id to serial).

---

## 14. Authored data sets (ADR-0022)

New content goes into a **stage**, never the install:
`tools/uodata_write` writes it, `tools/uopack` feeds it from PNGs, and the
`/uo-data` skill runs the whole flow.

**The stage folder** (`build/uodata/<pack>/`, gitignored: it holds copies of
proprietary files):

| File | Contents |
|---|---|
| `<install file>` | A copy of each file written to, made on first write. Only these. |
| `stage.json` | `format`, `install` (path read from), `files {lowercase name: {sha1, size}}` of each original at copy time |
| `guo_data.json` | The custom-data manifest (§15): `mode` layered, `files` the staged copies, `contains_ea_data` true (local only). Set `UO_CUSTOM_DATA` to the stage to play with it |
| `files_override.txt` | `name=<absolute staged path>` per staged file, for the client's `settings.json` `files_override` |
| `slots.json` | `format`, `packs {pack: {ranges {ns: [[first, last]...]}, used {ns: {what: id}}}}`; `ns` is `static`, `anim`, `gump` or `multi` |
| `multis.json` | (written by `tools/multi write`) `{name: {id, doors, size, storeys}}` per authored multi, what `prove` places |
| `dreadcrest.json` | (the Dreadcrest run only) `item`, `body`, `gumps` |

**The range policy** (`tools/uodata_write/ranges.json`, or a maintainer's
file of the same shape in `--ranges` / `UO_DATA_RANGES`):

| Field | Meaning |
|---|---|
| `never {ns: [[first, last]...]}` | Ids never handed out. A maintainer's lists are added to the default's. |
| `packs {pack: {ns: [first, last]}}` | A pack's fixed range. A maintainer's entry replaces the default's for that namespace. Refused, not trimmed, if any id in it is not free. |

**Asset records** (`tools/guo/uorecord.py`, between uopack and the writers):
`AssetRecord(kind, id, data, meta)`.

| `kind` | `data` | `meta` |
|---|---|---|
| `static`, `land` | The art entry as stored. A static keeps its 4-byte header as read, which is not always 0. A UOP land entry is 2048 bytes: 1,012 pixels plus 24 bytes of padding, kept. | |
| `gump` | `uint32 width, uint32 height`, then the rows (the UOP layout; flag 0 uncompressed) | `width`, `height` |
| `anim` | One direction's frame group as `anim.mul` stores it | `action`, `direction` |
| `tiledata-item` | empty | Any of `flags weight layer count anim hue light height name`; the rest keep their value |
| `hue` | One 88-byte hue entry | |
| `multi` | The `MultiCollection.uop` record: `uint32 id, int32 count`, then per component `uint16 item, int16 x, y, z, uint16 flags` (0 shown, 1 hidden), `uint32 0` (no cliloc list). Ids stay below 0x4000, which the shard masks to | |

**Writes** (all append-only except the in-place records):

- **LegacyMUL UOP.** The data is appended, then one new block
  (`int32 count, int64 next = 0`, then `count` × 34-byte entries:
  `int64 offset, int32 header_len 0, int32 size, int32 decompressed size,
  uint64 hash, uint32 adler 0, int16 flag 0`). The previous last block's
  `next` field (at +4) is set to it, and the header's file count at offset 24
  is increased. Names: art `build/artlegacymul/{id:08d}.tga` (a static's index
  is 0x4000 + id), gumps `build/gumpartlegacymul/{id:08d}.tga`.
- **MUL + IDX.** The data is appended to the `.mul`; the 12-byte `.idx` entry
  `int32 offset, int32 length, int32 extra` is written at `index × 12`.
- **anim.idx index.** For a body below 200: `body × 110`. Below 400:
  `22000 + (body − 200) × 65`. Otherwise `35000 + (body − 400) × 175`. Add
  `action × 5 + direction`.
- **tiledata.mul (new format).** 512 land groups of `4 + 32 × 30` bytes, then
  static groups of `4 + 32 × 41`. An item's 41 bytes are
  `uint64 flags, u8 weight, u8 layer, int32 count, u16 anim, u16 hue,
  u16 light, u8 height, char[20] name`, written in place.
- **Paperdoll gumps** of a wearable with animation body `b`: male
  `50000 + b`, female `60000 + b`.
- **Multis.** A new `MultiCollection.uop` entry `build/multicollection/{id:06d}.bin`
  (uncompressed). On an install without the UOP, `multi.mul` gets the
  components as 16-byte records (`uint16 item, int16 x, y, z, uint32 flags`
  1 shown / 0 hidden, `uint32 0`) and `multi.idx` is grown to hold the id. The
  `multi` pack's range is 0x3F00-0x3FFF.

---

## 15. Client data sources and the custom-data manifest (ADR-0021)

Which data a run reads is resolved in this order:

1. a custom data folder;
2. the UO install (the environment, then the saved setting, then the
   platform default);
3. the first-run wizard.

`tools/guo/datasources.py` is the tools' half and the runtime must match
it. A folder is **valid** when every required `FILE_REGISTRY` entry is
satisfied under the rules of §3.

**Settings**

| Key | Where | Meaning |
|---|---|---|
| `UO_CUSTOM_DATA` | environment, `config.local.bat`, central config; the client also reads `--custom-data` and a `guo_data/` folder beside its executable | A custom data folder (needs a manifest) |
| `UO_CLIENT_DATA` | environment, then `config.local.bat` or the central config; the client: `--client-data`, then the environment, then `settings.json` `ultimaonlinedirectory` | The UO install. The first one set is the only one tried |
| `UO_DATA_SOURCE` | set by `play.bat` for the client | `install`, `install+custom`, `custom` or `wizard` |
| `UO_FILES_OVERRIDE` | set by `play.bat` | A layered folder's override file, passed as `--files-override` |

**Exit code 3** from a tool means "no valid data: run the first-run wizard".
It is not a failure.

**`guo_data.json`** (at the root of the custom folder):

| Field | Meaning |
|---|---|
| `format` | `"guo/data-folder@1"` |
| `name` | A short name for the pack |
| `mode` | `"complete"`: a whole data set, valid on its own. `"layered"`: files that replace single install files |
| `files` | `{file name: {...}}`. Each is a plain file name in the folder (no paths) that must exist. For `layered`, the name is the install file it replaces, matched case-insensitively. The value may hold `sha1`, `size` and `replaces` |
| `contains_ea_data` | **Required**, true or false. `true` (a staged set, anything made from the user's install) is local only; release and publishing tools refuse it |
| `license`, `source` | Where the content comes from and under what terms. Required for a pack that is shipped or published |
| `client_version` | Optional: the client version the pack was made against |

A layered folder reaches the client as upstream's `files_override` file:
one `name=absolute path` line per file, lowercase names. Launchers write it
to `build/datasources/files_override.txt`, so a pack itself never carries an
absolute path. A staged set (§14) writes its own manifest with `mode:
layered` and `contains_ea_data: true`.

---

## 16. Multi descriptions and the multi catalogue (tools/multi)

A multi (a house, keep, castle or boat) is a list of components
`(item, x, y, z, shown)` around a centre. `tools/multi` authors new ones:
it mines the client's own buildings into a catalogue, expands a readable
**description** into components, validates them, writes them into a stage
(section 14) and proves them on the private shard. The `/uo-multi` skill
runs the flow.

**The catalogue** (`build/multi/catalogue/`, gitignored: derived from
client data, so never committed), written by `run.py mine`:

| File | Contents |
|---|---|
| `multis.json` | Per client multi: `id`, `kind` (`one-storey`, `two-storey`, `large-house`, `castle-or-keep`, `boat`, `open`, `marker`), `bounds`, `size`, `components`, `roles` (count per role), `storeys` (floor z levels), `storey_steps`, `wall_height`, `wall_materials`, `floor_materials`, `roof` (`z_from`, `z_to`, `step`, `above_top_storey`), `stairs` (runs, as in `stairs.json`), `doors` `[x, y, z, item, shown]`, `markers` (hidden components) |
| `families.json` | What generators pick from, most used first: `material → wall/window/post → height → signature → [ids]`, `material → roof → side → [ids]`, `material → stair → "ascent/signature" → [ids]`, `material → floor → signature → [ids]`, `material → door → any → [ids]` |
| `pieces.json` | Per item id: `name`, `flags`, `height`, `role`, `material`, `signature`, `roof_side`, `step`, `uses_multi`, `uses_statics`, `multis`, `in` (multi ids that use it), `z_above_floor` |
| `stairs.json` | Every stair run: `items`, `tiles`, `z_from`, `z_to`, `rise` (z per step), `direction`, `from_storey`, `to_storey`, `cells`, `source` |
| `buildings.json` | Clusters of wall statics on a facet (west of the dungeons), each analysed like a multi, plus `furnishing` (item → count) |
| `furnishing.json` | Per furnishing item: `placed`, `against_wall` and `on_surface` (fractions), `with` (the items most often in the same building) |
| `summary.json` | Counts: multis by kind, statics and buildings scanned, `storey_steps`, `stair_rise`, `ground_floor_z` |

Terms:

- A **role** is `wall`, `window`, `post`, `door`, `floor`, `stair`, `roof`,
  `deco` or `marker` (a hidden component), read from tiledata flags.
- A **material** is the tile name without its role word. Walls, windows,
  posts and doors take the wall material of the level they stand in, because
  some tile names are unreliable.
- A **signature** is which of `N` (y − 1), `E` (x + 1), `S` (y + 1) and
  `W` (x − 1) hold the same kind of piece at the same z (`-` for none).
- A roof **side** is the side a slope faces (`N`, `E`, `S`, `W`, or a corner
  such as `NW`), or `ridge_x`/`ridge_y` along the top, or `cap`.
- A step's **ascent** is the side the next higher surface is on.

**A description** (`format` 1; e.g. `tools/multi/examples/cottage.json`):

| Field | Meaning |
|---|---|
| `name` | The multi's name in the stage |
| `size` `[W, H]` | Outer walls on the lines x = 0, x = W, y = 0 and y = H. The floor fills x 1..W, y 1..H, as the client's houses do |
| `materials` | Required: `wall`, `floor`. Optional: `foundation` (a 5-high ring at `floor_z − 7`), `steps` (outside each ground-floor door), `roof`, `door` (`wood` or `metal`) |
| `floor_z` | The ground floor's z (default 7, the originals' usual value) |
| `storey_height` | z between storeys (default 20); `wall_height` defaults to one less |
| `storeys[]` | Per storey, bottom up: `openings[]` (`kind` `door` or `window`, placed by `side` `N`/`E`/`S`/`W` with an `offset` along it, or by `at` `[x, y]`), `partitions[]` (inner walls: `{"x": k, "from", "to"}` or `{"y": k, ...}`), `floor_holes[]` (`[x, y]` left open, for stairwells) |
| `roof` | `style` `gable` with `ridge` `x` or `y`: it covers x 1..W+1 and y 1..H+1 and rises 3 z a course, with gable-end fill (the wall material, 3 high) on both ends (the originals fill only the south or east end the client shows; generated multis are complete on every side); the span across the ridge must be odd (W even for a ridge along y, H even for a ridge along x). Or `style` `flat`: `material` floor tiles at the top, with an optional `parapet` material |
| `rects[]` | Instead of `size`: boxes `[x0, y0, x1, y1]`, or `{"box", "storeys", "roof"}`, whose union is the footprint (an L, a T, a U). Walls stand on the union's edge cells; a rect with fewer storeys is a lower wing with its own roof. Where roofs overlap the higher one wins, and nothing is roofed inside a taller rect |
| `storeys[].stairs[]` | `{"at": [x, y], "rise": N/E/S/W, "width"}`: a straight flight to the next storey as the client builds them (0x009E): step i a stair piece at z + 5i on i stacked 10-high blocks, then a landing; the next floor is left open over it, and the cell past the landing is where a climber arrives; `rail` (a material, e.g. `wooden fence`) stands a low rail round that opening on the floor above, the arrival end left open, so the hole reads as a stairwell and not a gap |
| `storeys[].floor` | That storey's floor material, over `materials.floor` |
| `rect` in an opening | Its `side`/`offset` count along that rect, not the whole footprint |
| `porches[]` | `{"box", "floor", "posts", "entry": {"side", "offset"}, "balcony": {"rail", "rail_height"}}`: paving off the house at floor level, posts at its free corners, entrance steps at its entry, and with `balcony` a railed floor over it on the second storey |
| `yard` | `{"box", "fence", "height", "gate": {"side", "offset"}, "gate_type", "path"}`: a fence on the box's edge (closed by the house's walls where it meets them), a real gate (`IronGate` or `LightWoodGate` by default), and a path of `path` paving from the gate to the entrance steps inside the fence |
| `decor[]` | `{"item", "at", "z", "storey"}`: an item at a cell, z above that storey's floor (or above the ground) |

**A built multi** (`build/multi/built/<name>/`, from `run.py build`):

- `components.json`: `[item, x, y, z]` per component, with a fifth element
  `0` when hidden, centred on `(W // 2, H // 2)`.
- `multi.json`: `name`, `size`, `centre`, `storeys` (z), `roof_z`, `doors`,
  `components`, `valid`, `problems`, `multi_id` (once written), and `local`
  (the grid per storey, for the validator).
  - Each door has `x` and `y` from the centre, `z`, `storey`, `facing` and
    `type`.
  - `facing` is ModernUO's `DoorFacing`: `WestCW` in a wall along x,
    `SouthCW` in a wall along y.
  - `type` is `DarkWoodDoor` or `MetalDoor`: plain doors with the house
    doors' art. A `BaseHouseDoor` refuses everyone outside a real `BaseHouse`.
- `stops`: where a proof walks, in order (`name`, `x`, `y` from the centre,
  `z`): outside (or through the yard's gate), the step, the entrance, inside,
  and per stair its foot, the storey it reaches, a room there and a balcony.
- `local` also holds `yard` (fence, gate, box, steps, path) and `stairs`.
- A door with no floor under it (one in a north or west wall, or upstairs)
  gets a sill: a floor tile in the door cell.
- Beside a door the wall's run piece stands, as if the wall went on through
  the doorway (the originals: stone 385 runs to 54 ends).
- A foundation and a flat roof's parapet (`parapet_height`, default 6) are
  courses of the material's low pieces that belong with the walls already
  picked: the client's stone castles found and top their walls with that
  wall's own 3-high pieces, not the whiter 5-high set.
- A row of entrance steps is one step piece end to end wherever the
  originals use that piece at the ends.
- `preview.png`, `preview_noroof.png` and `plan_<n>.png`.

**The validator** refuses:

- an item missing from tiledata, or one with no art;
- a z outside −128..127;
- more than 4,676 components (the shard reads an entry into 64 KB);
- a ground floor the outside reaches with the doors shut (a gap in a wall);
- a floor cell not reachable through a door (ground floor) or from a stair
  (upper storeys);
- a multi with no door;
- a yard whose fence is open, or whose gate does not lead to the entrance steps.

**A scene** (`kind` `scene`, `format` 1; `tools/multi/fort.py`,
e.g. `tools/multi/examples/fort_demo.json`) is `elements[]` on one grid, and
a `tour[]` of `{"name", "at", "z"}` stops. Every element names its `part`.
The scene is cut into multis on a grid of 35-tile squares, one multi per
square holding whatever stands in it (cut again on straight lines while it has
too many components), so no two overlap and none reaches more than 17 tiles
from its centre: ModernUO loses a multi's tiles at a point inside another
multi's bounds where that one has none, and sends a multi only within 22 of
its centre. `"layout": "parts"` instead keeps one multi per element `part`,
in element order, overlaps reported as notes: only for testing the shard
against overlapping multis. Elements:

| `type` | Fields |
|---|---|
| `wall` | `path` (points; diagonals become stepped runs), `thickness`, `z` (base), `top` (the walkway), `outer` (`left`/`right` of travel: the parapet side, crenellated), `parapet` (`outer`, `both`, `none`), `parapet_gaps`, `gates[]` (`box` or `cells`, `z`, `height`, `door` and `door_line`; without `door` it is a culvert) |
| `tower` | `disc` `[x, y, r]`, `z`, `levels[]` (floors, 20 apart for its stairs, which turn over three rows so no flight stands over another: a walker climbing one stacked over another was dropped to the floor below in game), `top`, `doors[]` (`at`, `z`, `door`); it opens where a wall's walkway meets it at a level (any wall in the scene, listed before or after it) |
| `platform` | `shapes[]`/`minus[]` (`box`, `disc`, `band`), `z`, `floor`, `face` (stone faces down to `base`; with `false`, faces still stand on every edge cell nothing else stands against, so no side is left open) |
| `causeway` | `path`, `width`, `z`, `rail`, `floor`, `buttress` (every N cells along it, a pier two cells long stands out from each side, of the face material's run pieces) |
| `stair` | `at`, `rise`, `z`, `to`, `width`, `landings` (z levels where it pauses on a landing `landing` cells long, default 2, then goes on the same way; everything stands on blocks from `z`) |
| `house` | `desc` (a house description), `at`, `z` |
| `props` | `items[]` (`item` id, `at`, `z`): loose pieces (a wall torch, a banner, debris) kept wherever they stand; they claim no cells, so the floor or wall under them stays |

A scene's `ground` (default 0) is the height it stands on; `plinth` (default
6) is how far below that its walls, towers, platforms, causeways, stairs and
houses reach, in the walls' own low pieces, so lower land shows stone.
Parapets are two courses of those low pieces with a merlon of the same
piece, turned with the wall, on every other cell. Every floor 16 or more
above the ground (a gate's passage, a platform, a tower's first level)
stands on a solid fill up to 15 under it: a hollow there is room to stand
on the land below, and the client steps down into it. Any element's `floor`
or `walk` may be a list of materials: the first lies on about half the cells,
the rest share the others, picked per cell by a fixed hash (CRC-32 of `x,y`),
so a courtyard is not one tile repeated and the bytes stay the same. `scene-prove --at X
Y` without a z stands the scene on the land height most of it covers. A tour
stop at the ground on a cell no multi covers is marked `on_land`, and the
proof checks it against the land's own z there (land under a wide scene is
rarely flat).

`scene-build` writes `build/multi/scenes/<name>/`: `scene.json` (`parts[]`
with `name` (the element it mostly holds and its square), `centre`, `bounds`,
`square`, `holds` (the elements in it), `doors`, `components`; `bounds`, `tour`,
`valid`, `problems`), `parts/<part>.json` (components from the part's
centre) and `preview.png`. `scene-write` writes each part into the stage as
`<scene>.<part>` and the scene into the stage's `scenes.json`
(`bounds`, `tour`, `parts[]` with `id`, `centre`, `doors`). A part is
checked for known items with art, z range and size.

**Storeys on the map's own buildings** (`kind` `storeys`, `format` 1;
`tools/multi/storeys.py`, `run.py storeys DESC --project DIR`) raise
buildings that already stand in the statics instead of placing multis, and
write the result as a world project (section 9) for `tools/world` to export.
The ground storey stays as the map has it (walls, floor, furnishings), so the
shard's doors, signs and vendors there stay valid; everything in the
footprint from the ground walls' top up (and a pitched roof's eaves one cell
round it) goes. `buildings[]`:

| Field | Meaning |
|---|---|
| `name` | The building's name in the record |
| `box` `[x0, y0, x1, y1]` | Map cells that hold it: the footprint is its ground walls (wall, window or post pieces at `base_z`, give or take 2: on uneven land the map sets some a unit off) and what they enclose or a flat roof at their top covers, doorways closed |
| `base_z`, `storey_height` | The ground storey's z (default 0) and z between storeys (default 20) |
| `storeys` | How many storeys in all, the ground one included (1 or more: 1 keeps the ground storey and replaces its pitched roof with a flat one, `roof` and `roof_z` as for any building) |
| `wall`, `floor`, `stair_material` | Families for the storeys added: walls repeat the outline (a ground doorway in it becomes wall) and the ground partitions, with a window every `window_every` cells (default 3) of a straight run. `wall` `ground` repeats the ground storey's own piece in each wall cell (its windows too); other cells take the ground walls' commonest family |
| `roof` | `{"floor", "parapet", "trim"}`: the flat roof's tiles, its 5-high parapet, and optional `trim`: heights of low-wall courses laid on the parapet in turn (e.g. `[2, 3]`), in the parapet's family; a corner the family has no piece of that height for takes its 2-high corner |
| `stairs[]` | `{"storey", "at", "rise", "width"}`: a house stair (above) from that storey to the next; the ground storey's own pieces on its cells go |
| `setback` | `{"side", "cells", "storey"}`: from that storey (default 1) up the building steps in that many cells from one side (`N`, `E`, `S`, `W`); the strip left over is a terrace on the storey below, floored with the roof's tiles and edged with its parapet |
| `roof_z` | The roof deck's z, when lower than a whole storey up (16 or more above the top storey). The client stands on nothing above z 112 (its pathfinder caps every cell at 128, and a walker needs 16), so a walkable deck is 112 at most; the stair to it is a short flight with no landing |
| `partitions` | `false`: upper storeys have no inner walls (the ground storey's may enclose a void, a hall two storeys tall, with no door to repeat) |
| `floor_holes[]` | `{"storey", "box"}`: cells left open in that storey's floor, over a stair the map already has |
| `hue` | A hue for every piece added |

Beside `buildings[]`, a description may carry:

| Field | Meaning |
|---|---|
| `ground` | The land's height for the offline walk: a number (default 0) for the whole scene, or `"land"` to read each cell's height from the map (a street on a slope, a building on a hill) |
| `swaps[]` | `{"at": [x, y], "z", "item", "new"}`: that piece in place of the wall standing at the cell and z (an arch in a wall, the map's or one added). A cell with no wall there is refused unless `new` is true (a doorway the piece closes) |
| `paving[]` | `{"box", "floor", "variants"}`: floor pieces of that family (up to `variants`, default 4) at the land's height on each flat cell of the box (all four corners level) that holds no statics |
| `resurface[]` | `{"box", "from", "floor", "variants"}`: every map static in the box whose id is in `from` (hex strings) is replaced by a piece of the `floor` family at the same cell and z (a timber dock laid in stone). A box holding none of them is refused unless `"optional": true` (a sweep over many boxes) |
| `scenes[]` | `{"scene", "at", "z", "clear", "hue"}`: a new building in place of an old one. `scene` is a scene description (as `scene-build` takes, its own `tour` ignored), stood with its grid origin at map `at` and raised by `z`; every map static in each `clear` box is removed first. The scene's pieces go into the statics, so its doors are not placed (the shard's own doors stay where they were: keep the ground floor's doorways on those cells). `buildings[]` may then be left out. The scene's problems, other than part overlaps, count as the project's |
| `props[]` | `{"item", "at", "z", "hue"}`: loose pieces at map cells (street dressing, room decor, rooftop kit), added as given; `item` and `hue` are hex strings |
| `remove[]` | `{"item", "at", "z"}`: exact map statics taken out (the loose furniture a prop replaces); one not there is skipped |
| `reclad[]` | `{"box", "from", "to", "kinds", "ids"}`: every map wall, window, post or stair piece (`kinds`, default all four) of material `from` in the box becomes the `to` material's piece with the same part, height and joins; failing that, the nearest height and closest joins, and a window or post `to` lacks becomes its plain wall. `ids` (hex or `"lo-hi"`) limits it to those source pieces. A piece with no match stays and is reported; a box with none is refused unless `"optional": true` (a sweep over many boxes) |
| `reland[]` | `{"box", "from", "to", "keep"}`: land cells in the box whose id is in `from` get one of `to` (ids or `"lo-hi"` hex ranges, picked per cell by a fixed hash), except inside the `keep` boxes: a district's grass laid as paving, a park left. `sparse` N takes only a cell with fewer than N listed cells in the 5 x 5 round it (stray patches go, a road of them stays); `level` N gives a paved cell with nothing on it the middle height of its eight neighbours when within N (a lone dip shades a dark cross into flat paving) |
| `land[]` | `{"at": [x, y], "id", "z"}`: one land cell painted by hand, its tile id (hex) and, when given, its height. Applied after `reland[]` |
| `strip[]` | `{"box", "names", "keep"}`: map statics in the box whose tiledata name contains one of `names` (case ignored) are removed, except inside `keep`: the trees and brush on paved ground, the pitched-roof pieces a flat roof leaves |

`tour[]` is as a scene's, in map coordinates; for `world-prove` a stop may add `"go"`: `true` steps
there by staff command (`[go x y z`) instead of walking, `"xy"` leaves the z to the shard (a spot
for a still: a roof, a place no path reaches). The project folder gets
`project.json`, `blocks/`, `storeys.json` (per building its storey z levels,
stairs with foot, cells and arrival, footprint, `roof_z`; the counts added and
removed; the tour and its walk problems) and `preview/` (`whole.png`, and
`cut_below_<z>.png` above each storey).

## 17. The player's servers (`servers.json`)

GUO's own file beside upstream's `settings.json` (same folder), written by the pre-game card's Servers tab
(`src/Input/Touch/Pregame/ServerBook.cs`, docs/ui/second_screen_pregame.md). Upstream's settings keep their shape;
the address in use stays in `settings.json` (`ip`, `port`), which Play sets.

```json
{ "servers": [
  { "name": "My shard", "host": "play.example.com", "port": 2593, "own": true, "favourite": false,
    "last_played": "2026-09-28T12:00:00Z" }
] }
```

| Field | Type | Meaning |
|---|---|---|
| `name` | string | What the list shows |
| `host`, `port` | string, int | The login server's address |
| `own` | bool | Added by hand; only these show their address on screen |
| `favourite` | bool | Listed under Favourites |
| `last_played` | UTC time or absent | Set on each entry into the world; Recent is the newest five that are neither own nor favourite |
| `data_folder` | path or absent | Where the player keeps a shard's own client files, picked once through the first-run screen; Play on a shard that needs them restarts GUO with them (section 19). An entry with one is never trimmed from Recent |
| `accounts` | list or absent | The player's accounts on it: `name`, `secret` and `last_used`. `secret.store` is `dpapi` (Windows: `blob`, base64 DPAPI output), `android-keystore` (`iv` and `blob`: AES-GCM under a non-exportable AndroidKeyStore key), `libsecret` (nothing; the Secret Service keyring has it, schema `org.guo.Account`, attribute `binding`) or `none`; `secret` is absent when no password is kept. `dev` (true) marks a dev build's one-click dev login, on the dev shard's entry only. Never a plaintext password. The secret is bound to the entry's host:port:name. See docs/ui/second_screen_pregame.md, Accounts |
| `era`, `emulator`, `client_version`, `encryption`, `needs_custom_data`, `third_party_clients`, `site`, `description` | optional | The community catalogue's manifest fields (step 3); `third_party_clients: false` or another client version or encryption keeps Play from playing |

A debug build adds its dev shard (from `UO_SHARD_HOST` / `UO_SHARD_PORT`) as a favourite at run time. It is
never written to the file, and a release build never has it (`OS.IsDebugBuild()`).

## 18. The community server catalogue (`servers/catalogue.json`)

The Servers tab's **Community** group. It lives at the repo root in `servers/`, is built into the client as the
embedded resource `servers/catalogue.json`, and is read once per run and again on **Refresh**. Every entry is
owner-approved and allows third-party clients (`servers/README.md`); `servers/catalogue.sample.json` shows every
field filled in.

```json
{ "version": 1, "servers": [ { "name": "Example Shard", "host": "play.example.com", "port": 2593, "era": "AOS",
  "emulator": "ModernUO", "third_party_clients": true, "site": "https://example.com", "description": "..." } ] }
```

| Field | Type | Meaning |
|---|---|---|
| `version` | int | 1 |
| `servers[]` | objects | The `servers.json` entry fields of section 17 without `own`, `favourite` and `last_played` |

An entry without a name or host, with a port outside 1-65535, or with `third_party_clients: false` is skipped. A file
that doesn't parse leaves the group with "The server list couldn't be loaded". A catalogue shard the player
favourites or plays on is copied into `servers.json` and then lists under Favourites or Recent instead.

Live status is a bare TCP connect to the host and port, timed from the moment it is resolved: at most 8 at once, a
3 s timeout, repeated every 60 s while the Servers tab is showing. Nothing is sent and no result is stored.

## 19. Playing with a shard's own files (`shard_session.json`)

Beside `settings.json` in the client home. Written by Play on a shard that needs its own client files, and read by
Main at the next start before the data is resolved (ADR-0021). GUO then restarts (`OS.SetRestartOnExit`, the same
arguments; a run from source is given its project as an absolute `--path`).

```json
{ "name": "Shard B", "host": "play.example.com", "port": 2593, "data_folder": "/path/to/shard-b-client",
  "client_version": "7.0.15.1", "encryption": 1, "own_encryption": 0, "started": "2026-09-28T19:00:00Z" }
```

| Field | Meaning |
|---|---|
| `name`, `host`, `port` | The shard; the run connects there instead of `UO_SHARD_HOST` / `UO_SHARD_PORT` |
| `data_folder` | Its files, read in place. With a valid `guo_data.json` (section 15) it is the custom folder of ADR-0021; without one it must be a whole client and is the run's install. A `--custom-data` or `--client-data` flag still wins |
| `client_version`, `encryption` | The shard's, when its entry names them; else the configured ones |
| `own_encryption` | The player's own, put back when they go back |
| `started` | When the session began |

While the file has a `data_folder`, every start uses it, and the Servers tab says so with "Your own files". A folder
that is no longer usable drops the session at boot, with the reason shown once in the Servers tab.

Going back ("Your own files", or Play on another server) writes a one-shot file with no `data_folder`: the player's
own encryption, and the server to play on next, if any. It is deleted as soon as it is read.

## 20. Pre-game choices (`pregame.json`)

Beside `settings.json` in the client home: GUO's own choices made on the pre-game card that belong to no profile
(upstream's settings.json keeps its shape).

```json
{ "login_background": "builtin:starlit-sea" }
```

| Field | Meaning |
|---|---|
| `login_background` | What the canvas background (ADR-0016) shows before a profile is loaded: `""` for the last character's (ADR-0016's own rule, the default), `builtin-grey`, `builtin-wood`, `builtin:<name>` from `assets/backgrounds/backgrounds.json`, or `embedded:<file>.png`, a picture compiled in from `Resources/embedded/backgrounds`. A choice that no longer exists reads as the default. In the world the profile's own background applies |

## 21. The agent request queue (`agent_queue.db`)

One SQLite file per user at `UO_AGENT_QUEUE` (default `%APPDATA%/GUO/agent_queue.db`, or
`~/.config/guo/agent_queue.db`), written by `tools/agent_queue` and read by the editor's chat window and
by agent sessions. WAL mode, 30 s busy timeout. Times are UTC ISO-8601 with milliseconds.

| `requests` column | Meaning |
|---|---|
| `id` | Integer primary key, increasing |
| `to_agent`, `from_agent` | Agent names (`[A-Za-z0-9_.-]{1,40}`); `to_agent` may be `*` (the first watcher using `--include-broadcast` takes it) |
| `text` | At most 8000 characters; never a secret |
| `attachments` | JSON array of absolute local paths (at most 16); never copied or opened |
| `status` | `new`, `taken`, `answered` or `cancelled` |
| `created`, `taken_by`, `taken_at` | When posted, and who took it when |

| `replies` column | Meaning |
|---|---|
| `id`, `request_id` | Primary key, and the request answered |
| `from_agent`, `text`, `attachments`, `created` | As for requests |

Taking a request is one transaction that moves `new` to `taken`, so no request is delivered to two
watchers. The first reply moves a request to `answered`. The JSON lines printed by `tail` and
`watch-replies` use the keys `id, to, from, text, attachments, status, created, taken_by, taken_at` and
`id, request_id, from, text, attachments, created`.

## 22. The AI dock's endpoints (`ai_endpoints.json`)

Written by the editor's AI dock (ADR-0028) to `%APPDATA%/GUO/ai_endpoints.json` (`~/.config/guo/` elsewhere),
never to a project or `.godot`. A JSON array of `{ "Name", "Url", "Model", "Key" }` for the OpenAI-compatible
endpoints the user added. `Key` is `{ "store", "iv", "blob" }`, the same `Secret` that `servers.json` keeps
for a pre-game password: ciphertext sealed by the operating system's store (DPAPI on Windows), bound to
`ai:URL:NAME`, or null when the endpoint has no key or the platform keeps none. The key itself is never in the file.
