# tools/art_extract -- the client's art as atlas pages

Exports the art of your own UO install (static art, land tiles, gumps, texmaps, lights) into a local *set*: 2048 x 2048
RGBA8 PNG pages plus an index per class, in the format of data_formats section 36 and ADR-0034. The runtime source that
reads a set is story AX2; animations (`--what anim`) are AX3. Nothing in this tool changes how the client runs today.

```
python tools\art_extract\run.py export [--what art,land,gumps,texmaps,lights] [--out DIR] [--from DATA]
python tools\art_extract\run.py verify [--set DIR] [--what ...] [--from DATA]
python tools\art_extract\run.py where
python -m pytest tools/art_extract -q
```

`--what` takes `art` (land + static), `land`, `static`, `gumps`, `texmaps`, `lights`, `anim`; the default is all of them.
`--from` and the data folder default to `UO_CLIENT_DATA`. The output folder is `UO_ART_EXTRACT_DIR` (environment, then
`config.local.bat`, then `config.bat`, else `art_extract` under `UO_WORKSPACE_DIR`). **`--out` may only be that folder,
a folder inside it, or a folder inside the repository's `build/`**; anything else is refused. An export removes the
previous set in its output folder first (`set.json` before anything else), so a half-written or older set is never
mixed with a new one; a `--what` subset gives a set of those classes only.

## What it reads, and how

No decoder of its own. The readers are `tools/guo/uoread.py` and the row decoders `tools/guo/uoart.py`, the ones
`tools/uopack` and `tools/world` already use; they mirror `ArtLoader`, `GumpsLoader`, `TexmapsLoader` and
`LightsLoader`. This tool adds only the loaders' rules for turning a result into pixels and for which ids exist:

| Class | Source | Notes |
|---|---|---|
| `land` | `artLegacyMUL.uop` or `art.mul` + `artidx.mul` | the 1,012 pixels of the diamond are drawn (colour 0 is black); the four corners are `00000000` |
| `static` | the same files, id = index - 0x4000 | colour 0 is not drawn; opaque black is the near-black 0x0421 the file stores |
| `gump` | `gumpartLegacyMUL.uop` or `gumpart.mul` + `gumpidx.mul` | ids that only exist through `gump.def` (they carry a hue) are not stored; the client still resolves them itself |
| `texmap` | `texmaps.mul` + `texidx.mul`, `TexTerr.def` | the def's aliases are applied as `TexmapsLoader` applies them, last group member wins |
| `light` | `light.mul` + `lightidx.mul` | a byte above 0x1F is bit inverted, as in `LightsLoader`; 0 is not drawn |

A non-empty `verdata.mul` patches art and gumps exactly as the client does (and joins the fingerprint). Colours are
expanded with `HuesHelper`'s table, so a pixel is the same four bytes `R, G, B, A` the loader hands the renderer.
An id the install does not hold is simply absent. An id it holds but this tool cannot decode (a damaged entry, an image
wider than a page) is listed in the class's `skipped` with a reason, and the client falls back to its own loader for it.

### Animations

`art_anim.py` reads `anim*.mul` + `anim*.idx` and `AnimationFrame*.uop` block by block, the way `AnimationsLoader` reads
them (palette, run-length rows, UOP frame table with its gap fill and the frame-per-direction count, with and without the
Equipment minimum), and stores each block under the key the loader will ask for. It does **not** resolve bodies:
Body.def, Bodyconv.def, Corpse.def and the UOP replacement tables stay in the loader, so body conversion is exact by
construction. Frames are packed in order and flushed page by page, so the full install never sits in memory. See
data_formats section 36 for the key and row format. The proof that these decoders agree with the client is
`ArtSetParityProbe`'s animation pass (every block the loader can reach, set on and off), as for the other classes.

## Layout and determinism

Pages are numbered `page_0000.png` onward, each a full 2048 x 2048. Images are placed on shelves in the order (height
descending, id ascending), with no gutter. Two ids whose pixels are byte-identical share one rectangle (the lower id
owns it); the index still lists both. PNG output is fixed: filter 0, zlib level 6, IHDR/IDAT/IEND only. `set.json`'s
`generated` is the newest modification time among the source files rather than the clock, so exporting the same
install twice gives byte-identical files (pytest proves it on a synthetic install; the story run compares hashes of a
real export).

## verify

`verify` is the acceptance of a set. It checks the source files against the fingerprint (size and sha256), the schemas,
each page's sha256 and PNG shape, then decodes **every id from the install again** and compares: the image's size and
pixel digest with the index, the id's presence in both directions (nothing missing, nothing extra, every undecodable
id listed as skipped), and finally the page rectangle's pixels with `pixels_sha256`. It prints per-class counts and the
first mismatches, and exits 1 unless everything matches. Exit code 2 is a usage or data problem (a message, no
traceback).

Because export and verify share the row decoders, `verify` proves the pages hold what those decoders produce. That the
decoders agree with the client is proved against the client's own loaders by AX2's frame comparison (set on and off)
and by `/parity-check` against `guoasset` (upstream's loaders).

## Privacy

A set is derived from proprietary data. It is written only under `UO_ART_EXTRACT_DIR` (or `build/`), is gitignored, and
is never committed, bundled or put in an export preset (rule 8). The tests use a synthetic install of random pixels;
no game data is in any fixture. To remove a set, delete the folder.

## Using a set in the client (AX2)

Start the client with `--art-set` (or `UO_ART_SET=1`; `--no-art-set` forces it off). It mounts the set when `set.json`'s
fingerprint matches the install, logs one `[GUO] art set mounted` line, and answers land, static, gump, texmap and light
images from the pages; otherwise it logs one warning naming the reason and reads the original files. A shard-author
override file under `Art/Statics`, `Art/Land` or `Gumps` in the client folder still wins over the set. Pages are read
lazily; `UO_ART_SET_CACHE_MB` (default 256) caps the decoded pages kept in memory. Parity probe, every id through the
real loaders with the set on and off:

```
godot-console --headless --path godot/GUO res://src/Assets/Extracted/ArtSetParityProbe.tscn -- --art-set
```
