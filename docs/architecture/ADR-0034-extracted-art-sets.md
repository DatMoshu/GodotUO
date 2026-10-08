# ADR-0034: Extracted art sets — a local mirror of the install's art as atlas pages

**Status:** Accepted for implementation (owner approved PLAN guo-art, 2026-10-07). Contract only: no exporter and no
runtime source exist yet (stories AX1-AX3).
**Date:** 2026-10-08
**Decision makers:** the owner (on a community member's request to extract the muls/uops and use proper sprite
sheets), the GUO director

## Summary

A player can export the art of their own UO install once into **atlas pages** (2048x2048 RGBA8 PNG) plus one
`index.json` per asset class, under a local folder named by `UO_ART_EXTRACT_DIR`. The client can then read art from
that **extracted set** instead of decoding the archive's 1555 pixels on demand. The set is a disposable local
mirror: it is not a store pack, it is never signed, published, committed or bundled, and anything it cannot answer
falls back to the original files. The byte-level contract is `docs/data_formats.md` section 36.

## Context

- The asset loaders (`src/Assets`, verbatim tier) already ask a content overlay, `UOFileManager.Content`
  (`StoreRuntimeContent`), before the archive: `ArtLoader.GetArt`, `GumpsLoader.GetGump`, `TexmapsLoader`,
  `LightsLoader` and `Animation.TryAnimation`. An extracted set can use that seam with no new edit to a verbatim
  file.
- The overlay decodes PNG packs at startup into memory under a 64 MB image budget (ADR-0019). A whole client's art is
  far larger, so a set needs lazy, page-at-a-time loading, not the pack path as it is.
- `data_formats.md` section 1 promises decoded atlases under `UO_CACHE_DIR`; nothing writes them today (decoding is
  in memory, on demand). The extracted set is how that promise is kept for art.
- `tools/uopack` already unpacks art, land, gumps and animations to one PNG and JSON per asset with a pixel hash, and
  guoasset's `atlas_art` packs ids into one sheet. Neither produces a set the runtime can load.
- The runtime's own `src/Render/TextureAtlas.cs` packs sprites into 2048 pages lazily per id. ADR-0007 (batched
  world meshes) measured and recommended against changing how the world is drawn; this ADR does not touch drawing.

## Decision

1. **A set is a local mirror of the user's own install, not a store pack.** No signing, no image budget, no
   publishing, no installer. It shares only the mount seam with ADR-0019 packs. Precedence, first answer wins:
   world-project PNGs (ADR-0020) > store packs (ADR-0019) > extracted set > original files.
2. **Format.** Per asset class, 2048x2048 RGBA8 PNG pages, lossless, with each 1555 pixel decoded exactly as the
   loaders decode it (`HuesHelper.Color16To32`, transparent = 0), so the hue shader still applies. One `index.json`
   per class maps id -> page, x, y, w, h and a pixel hash, plus per-class extras. A `set.json` at the set's root
   carries the version and the fingerprint. Section 36 is the contract.
3. **Fingerprint.** Every set records the name, size and sha256 of each source file it was made from (as
   `client_manifest` records names and sizes, plus the hash). At mount the runtime compares names and sizes; a
   mismatch, a missing id or a damaged page falls back to the original files with one warning, never a failure.
4. **Location and switch.** `UO_ART_EXTRACT_DIR` (default `art_extract` under the workspace, ADR-0032) holds the
   set. It is gitignored, never committed, never in an export preset or bundle (rule 8: it is derived from the
   user's proprietary data). The set is **off by default**; `UO_ART_SET=1` or the `--art-set` argument turns it on
   (`--no-art-set` forces off). Presence of a folder never switches it on.
5. **Parity is the bar.** Per-id pixel hashes equal the loaders' own decode (`art_extract verify`), and frames are
   identical with the set on and off. guoasset (upstream's loaders) stays the reference renderer.
6. **"Ditch the muls" is the end state for art only.** Maps, tiledata, multis, hues, sounds and clilocs stay read
   from the install. Whether the client can start with the art files absent is measured first (AX5) before it is
   promised anywhere.

## Relation to other decisions

- **ADR-0019 (asset store):** the same runtime seam, a different thing. Packs are independently licensed content,
  signed and budgeted; a set is the user's own data, unsigned and unbounded. A pack answer wins over a set answer.
- **ADR-0020 (asset overlay):** edits in the world project win over everything. See the hazard below for how the
  loader's own file overrides sit relative to the seam.
- **ADR-0007 (batched meshes) and `src/Render/TextureAtlas.cs`:** set pages are *storage* pages. The runtime copies a
  sprite's rectangle out of a page into its own draw atlas exactly as it does for decoded sprites; the draw path,
  nearest-neighbour sampling (rule 7) and ordering are unchanged. Pages carry no gutter because nothing samples them
  directly.
- **`data_formats.md` section 1 (derived data under `UO_CACHE_DIR`):** an extracted set is derived data in that sense
  (deleting it is always safe) but it lives under its own setting so it can be large, kept or deleted apart from the
  decode cache, and kept out of every bundle.

## Known hazard (settled in AX2)

**Settled:** `ExtractedArtSource` lists, at mount, the ids the client folder's `Art/Statics`, `Art/Land` and `Gumps`
override files hold (parsed as the loaders parse them) and never answers for those, so the loader reaches its own
file. No verbatim loader is edited. Original statement of the hazard:

`ArtLoader.GetArt` asks `Content` first and only then its own shard-art files (`_ourLand`, `_ourStatics`, which
"correct art the archive already has"). An extracted source that answers through `Content` would therefore answer
before those files. The set must stay behind every override. AX2 must either make the source defer for ids the
loader's own files override (without editing the verbatim loader beyond calls the seam already makes) or stop and
report (rule 2). The set is built from the pure install, never from overrides.

## Consequences

- A first export costs disk and time (AX1 measures both); a set is stale after a client patch, which the
  fingerprint detects, and is rebuilt by running the export again.
- `UO_ART_SET` off means zero behaviour change, so the default client is unaffected until AX2's parity proof is
  accepted.
- A set is the player's own art on their own machine. The client's art is still drawn to the screen, so a set does
  not protect anything; protection of a shard's *custom* art is a separate, later decision (AX6/AX7 in the
  sprint) and does not apply to a player's own extracted set, which stays plain and unmarked so parity checks stay
  exact.

## AX3: animations are keyed by the loader's read

Animation art does not have one id per image. Which block of `anim*.mul` or `AnimationFrame*.uop` a body, action and
direction reach depends on Body.def, Bodyconv.def, Corpse.def, mobtypes.txt, the UOP replacement tables and the client
version. The `anim` class is therefore keyed by the block the loader reads (file, position, size or direction), below
all of that resolution. The loader keeps resolving bodies, so conversion is exact by construction and the set never
carries a second copy of those rules; `Animation.cs` asks the content seam for a block before it calls the loader's
read, and a miss reads the archive. Consequence: a block shared by two bodies is stored once, and the exporter has to
mirror only the two read methods (palette, run rows, the UOP frame table). The probe's animation pass enumerates blocks
through the loader's own `GetIndices` to prove the keys line up. Details: data_formats section 36, Animations.

## Validation

Contract only. Each later story proves its part: AX1 byte-determinism and 100% `verify`; AX2 identical frames on/off
(login screen, Britain bank, paperdoll) and scenario runs with the set on; AX3 animation parity and a 10-minute
endurance run; AX5 the art-files-absent measurement.
