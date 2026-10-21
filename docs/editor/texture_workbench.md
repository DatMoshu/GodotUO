# Texture workbench

F09a adds **Textures** to the Assets library. This manual L3 surface inherits
the Assets workbench: library navigation, a paged nearest-neighbour thumbnail
grid, the existing inspector and named icon actions with tooltips. Its task is
to find and inspect the texture a sloped land tile uses. It adds no client option.

Search a decimal or `0x` texture ID, or a referring land tile's name, and press
Enter. **All sizes**, **64 × 64** and **128 × 128** filter the effective native
entries. The inspector shows a nearest-neighbour preview, native dimensions and
the count/first sixteen land references. **Export texture PNG** writes the
decoded, opaque image through the shared guarded atomic PNG exporter. Native
black is opaque; this domain has no transparent color key.

The editor uses the existing `TexmapsLoader.GetTexmap`, including its effective
`TexTerr.def` remaps. Before calling that decoder it checks the ID, exact
8,192/32,768-byte record size and range inside the loaded file; malformed entries
are omitted. The ported loader is unchanged. The browser's read-only source is
the active library, which can include the configured staged inputs.

F09b adds source-bound texture working documents. Select a texture and choose
**Replace texture from PNG**. The review shows the current and incoming native
previews and dimensions before **Replace working texture** changes the document.
Only 64/128 square PNGs under 2 MiB are accepted. The PNG header is checked before
decoding. RGB discards its low three bits per channel; alpha is ignored and zero
is opaque black. Existing untouched native records retain all 16 bits.

The inspector has a pinned undo/redo/revert and **Save copy** footer. **Source**
in the browser toolbar compares the original texture with the working copy;
it does not revert the document. **Reopen texture** explicitly discards its draft
and reloads the saved project copy. Saved copies also appear in browser thumbnails.
Unsaved drafts recover through per-editor project metadata, with a 4 MiB budget.
Invalid/stale recovery is refused until the entry is explicitly reopened.

One atomic JSON file at `metadata/textures/0xNNNN.json` stores schema 1,
`kind: "guo/texture"`, the ID, source fingerprint, base64 native record and its
SHA256. Native record length determines 64/128 dimensions. The source fingerprint
includes the active file path, effective offset/length, complete index hash,
TexTerr.def hash and original record bytes. Source/index/remap changes, externally
changed saved copies and stale image reviews are refused. A saved document is
at most 64 KiB; history retains at most 100 immutable image states.

F09c adds [verified native staging](texture_native_staging.md) through the shared
texture append/repoint writer. **Export verified texture stage** prepares a
private candidate, shows current/candidate textures and every affected alias,
then requires a separate Apply confirmation. Saving the working document alone
does not emit native files. No slot allocation/removal is offered.

Verification: the regular editor smoke covers both installed dimensions, ID and
land-name search, filtering, opaque pixels, PNG read-back and malformed index
guards before and after hot reload. The native Assets workbench proof captures
the texture browser at 1366, 1024 and 1920 logical widths. See
`tools/scenarios/fiddler-f09a/README.md` for the repeatable recipe.
Document verification additionally covers exact 16-bit preservation, conversion
boundaries/alpha, replacement sizes, history, atomic save/reload, stale inputs and
editor-local recovery. See `tools/scenarios/fiddler-f09b/README.md`.
