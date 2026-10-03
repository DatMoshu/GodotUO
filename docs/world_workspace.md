# World editing workspace

Open `launchers/editor/open_project.bat` from the checkout containing the
workspace changes, then select **World**. Existing Godot windows on other
checkouts do not switch to this branch automatically.

## Layout

The World tab has one command row, a scrollable icon rail, an asset/brush
library, the map, and a scrollable settings panel. **Brushes** toggles the
library; **Precision** hides the library and keeps settings; **Focus** (Tab
while the map has keyboard focus) hides both panels and Godot's docks.
Click Focus again to restore the editing panels. Godot's global navigation
and shard run bar remain available.

Tool names and shortcuts appear in tooltips. Existing layers, render modes,
season, scene packs, spawners and Area-to-multi remain in the settings panel.
Block outlines now start disabled; enable them in **Guides** when needed.

## Paint and sculpt

1. Choose **Scatter**, **Terrain**, or **Sculpt** in the library, then choose
   an asset. Search accepts names, decimal IDs and `0x` IDs. Terrain and
   statics use separate ID spaces.
2. Choose the brush operation and target. Set round/square footprint, size,
   density, spacing, strength and hue in **Brush**.
3. Drag on the map. Highlighted cells show the affected footprint; static
   paint also shows ghost art. Release to apply; Escape cancels. A stroke
   spanning several map blocks is one undo operation. Ctrl+Z undoes;
   Ctrl+Y or Ctrl+Shift+Z redoes.
4. **+ Variation** adds the selected asset to the mixture. The variation
   panel shows thumbnails and editable relative weights. The advanced field
   accepts `0x0CCA:3, 0x0CCB:1`; press Enter to rebuild the thumbnail rows.
5. Name and save a preset. Double-click a saved preset to reuse it. Favorites
   are available in the library and through **Q** over the map.

Scatter is deterministic for the seed and world cell, including across
separate strokes. Change the seed for a different arrangement. Spacing uses
a world-aligned lattice; density is the fraction of eligible cells selected.
It is not continuous-distance foliage packing. A stroke is bounded to 8192
cells and a brush to 31 tiles across. At most 128 static ghosts are drawn;
cell highlights still show the planned footprint.

**Keep existing statics** skips occupied cells for static painting.
**Avoid water**, **Maximum slope**, and **Allowed land IDs** restrict where
the brush acts. For example, list grass land IDs to leave roads alone.
Erase and hue brushes operate on statics within the visible Z range.
Raise/lower, flatten and smooth operate on terrain. **Lock terrain** blocks
terrain painting and sculpting, including the single-cell elevation tools.

Optional terrain-edge recipes use a table of `mask=landID` entries. Neighbor
bits are north=1, east=2, south=4, west=8. Use transitions from your own
tileset; GUO does not invent correct transition art from a tile name.
The selected tile, weighted variants and edge IDs define one terrain family.
The brush connects to existing family tiles and updates the adjacent ring;
the preview includes those neighboring cells and undo restores them too.

## Precision

**Visibility & height** controls visible minimum/maximum Z, a fixed placement
plane (or ground-relative offset for the brush), terrain locking and roof
ghosting. Roof ghosts are editor guides drawn over the scene, limited to
512 static roof pieces in view; they do not change the client renderer.

**Under cursor** lists statics and terrain at a cell. Click a row or use
Alt+wheel over the map to select a particular object. Shift-click a new cell
to reset that selection. **Set Z / hue** moves the selected static to the
configured Z and applies the chosen hue, with undo. **Pick** (or Alt-click
the map) samples art and hue for the brush.

Map shortcuts only apply while interacting with the map, so entering text
in the library or inspector does not trigger tools. Space+drag pans;
right/middle drag also pans. Wheel zooms; `[` and `]` change brush size.

## Pixelorama, Pinta and other art editors

There are three entry points:

- **World → Tool settings → Pixelorama / Pinta** edits the selected brush art.
- **Assets → Art or Gumps → select an asset → UO Inspector → Edit in…**.
- **Art** bottom panel → **Edit in Pixelorama / Edit in Pinta** edits the
  inspected asset. **Art tools / setup** explains installation and return paths.

Pixelorama and Pinta open separate application windows.

**Pixelorama:** edit the image, then use **Project → GUO: save back to GUO**.
The GUO extension also supplies hue palettes, UO templates and size checks.
**Pinta:** save the exported PNG in place. The Art watcher imports the saved
result automatically and reports success or rejection. Imported art passes
through UO size/transparency/color processing and updates the asset overlay.
Use the inspector's **Revert** action to restore the installation's asset.

For another editor, use **Save PNG…**, edit the file externally, then
**Import PNG…**. Animation frame round trips and embedding Pixelorama in a
Godot tab are not implemented.

Pixelorama setup: `python tools/pixelorama/run.py fetch --binary`. Discovery
checks `UO_PIXELORAMA`, this checkout, the main checkout for a worktree, then
PATH. Pinta setup: `winget install Pinta.Pinta`, or configure `UO_PINTA`.
Set machine paths in `launchers/_shared/config.local.bat`, then restart the
editor. Configure `UO_PYTHON` there if `python` is not on PATH.

The **Art** panel also retains ComfyUI and Retro Diffusion workflows:
configure the endpoint, choose a workflow/provider, Queue, select a gallery
result and **Import to overlay**. These require their own running service or
credentials; no image-provider request happens simply by opening the panel.

## Storage and verification

World edits remain in the world project's block overlay, with immediate
save and an in-memory undo history. Named recipes and favorites use
`<world-project>/brushes.cfg`. No operation writes to the UO client install.
Art exchanges use `build/art_exchange` by default; derived client images
must remain local and uncommitted.

The editor smoke now uses viewport coordinates for picking and checks brush
footprints, repeatable variants, multi-block undo/redo, duplicate prevention,
no-op filtering, terrain locks and rollback of a failed batch. The existing
art smoke checks export/import against stubs, including Pixelorama's return
format and Pinta's save-in-place path. It does not simulate painting inside
the external applications or make paid image-provider calls.
