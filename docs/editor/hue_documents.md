# Hue palette working documents

F08a adds manual L3 hue authoring behind `#if TOOLS`. Assets → Hues opens
the selected palette in the integrated inspector: 32 selectable swatches,
a project name, the selected raw hexadecimal color, RGB picker, optional raw
table bounds, undo/redo/revert, and a pinned **Save copy** footer. Icon actions
carry tooltips and accessible names. The palette and preview use nearest-neighbor
sampling. AI and external dependencies are not required.

The document reuses `assets/hues/0xNNNN.json`, format 1, with one-based hue IDs.
Existing project strips remain readable. Saving adds `source_sha256` and
`source_record_base64`, binding the document to the active loader's complete
88-byte base record, including name padding, bounds and every color bit.
Unknown JSON fields survive saves. Existing unbound format-1 documents are
accepted and receive a binding on their next explicit save.

All 32 color values and both table bounds remain unsigned 16-bit values.
Selecting a swatch commits valid pending fields as one undo step; invalid raw
fields remain attached to the document. The RGB picker quantizes each channel
to five bits and preserves the selected color's existing bit 15. Importing PNG
uses the first 32 pixels of its first row, rounds RGB to five-bit channels,
and replaces the working palette as one undo step. PNG does not carry bit 15.
Import does not save automatically. Export writes the current committed palette
as a 32-by-1 PNG. Correct or commit pending fields before export.

Project names accept up to 4,096 UTF-8 bytes without NUL or line breaks. Native
hue names use 20 ASCII bytes; a visible warning explains conversion when the
complete authored name differs. The complete name remains in project JSON.
Changing a palette without changing its name preserves the original record's
name bytes in the document's record preview.

Working names participate in browser and F3 name search. Saved project hue
documents are discovered lazily; untouched source palettes are not copied into
documents. Unsaved CPU previews temporarily substitute the working palette and
restore the loaded block in `finally`. Saving explicitly reapplies the existing
project asset overlay. The original base record remains available separately.

Save checks the current loaded base and the opened project's saved JSON hash,
then uses a temporary sibling and atomic replacement. External saved-copy edits
refuse overwrite. Resolved destinations must remain inside the project and
outside the install. This checks the active loaded library, not a live watch of
native files on disk. Reload the library after external native changes.

Draft recovery lives in local Godot project metadata, separately from explicit
project saves. It retains invalid raw fields and selected index across addon
reloads, validates the saved-copy and source bindings, and refuses stale recovery
without overwriting it. **Reopen hue** asks before discarding the selected draft
and reopening the saved project document. Cancel keeps edits. Revert to source
is an undoable working edit; it does not delete project files.

`EditorSmokeHueDocument` verifies raw-record preservation, full-width values,
atomic validation, history, save/reopen, legacy overlays, unknown JSON fields,
source and saved-copy conflicts, recovery, native inspector binding, picker
quantization, PNG import undo and unchanged native-file hashes. The reload smoke
runs these checks before and after assembly reload. The windowed Assets proof
captures the working hue inspector at 1024, 1366 and 1920 widths.
See `tools/scenarios/fiddler-f08a/README.md`.

Fiddler text exchange and reviewed native staging remain F08d–f. These slices
add no native writer or native insertion,
deletion, or replacement authorization. The existing world-project export path
is unchanged; its presence is not proof of F08f stage certification.

## Palette operations (F08b/c)

Expand **Palette operations**, choose inclusive indices 0–31, select an operation,
then use its apply icon. Bounds can be entered in either order. Spread and
exponential gradients affect only the interior; adjacent or identical indices
have no interior to change. Reverse includes both endpoints. Shift RGB accepts
integer deltas from -255 to 255 in eight-bit channel units, clamps each channel,
and includes the endpoints. The working name and raw table bounds stay intact.
These tool settings are editor controls, not part of the saved palette or its
undo history. Each applied palette operation is one undo step. Invalid pending
fields, changed source bindings, or failed calculations refuse the entire edit.

The operations follow the pinned UOFiddler `HueEditForm.cs` and
`Ultima/Helpers/HueHelpers.cs` at commit
`610c930833dac8d158f08a0af36da43dd90d08b4`. Spread divides endpoint-channel
differences by the interior count, so its last interior color repeats the end
color. Exponential uses the reference logarithmic exponent and retains its
equal-channel behavior. A reference overflow (notably an equal 255 channel)
is refused atomically with a recovery suggestion instead of leaving a partially
modified palette. No source implementation is copied into production.

RGB conversion expands five-bit components by bit replication, then packs with
right shifts; nonzero RGB that collapses to zero becomes raw color 1. Like the
reference, gradient interiors and RGB shifts write 15-bit colors, clearing bit
15 only in rewritten entries. Reverse preserves every bit. Colors outside the
range and gradient endpoints remain byte-for-byte unchanged.

`EditorSmokeHueRange` uses fixed spread/exponential goldens, equal-channel and
overflow cases, reversed bounds, exact raw reversal, dark/zero packing, RGB
clamping, immutable input, document validation and one-step undo, source conflicts,
save/reopen, and native editor controls. Windowed proof captures expanded RGB
range controls at 1024, 1366 and 1920 widths. See
`tools/scenarios/fiddler-f08bc/README.md`.
