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

F09b will add a source-bound image replacement document with history and
recovery. F09c will bridge the existing world texture exporter to independently
verified native staging. This browser does not allocate/remove slots or claim
native texture write certification.

Verification: the regular editor smoke covers both installed dimensions, ID and
land-name search, filtering, opaque pixels, PNG read-back and malformed index
guards before and after hot reload. The native Assets workbench proof captures
the texture browser at 1366, 1024 and 1920 logical widths. See
`tools/scenarios/fiddler-f09a/README.md` for the repeatable recipe.
