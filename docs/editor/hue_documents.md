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
uses the first 32 pixels of its first row, discards the low three RGB bits
(matching the legacy hue-strip importer), ignores alpha, preserves zero,
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

Reviewed native staging remains F08f. These slices
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

## Palette text and names (F08d/e)

The inspector's More actions imports a palette text file or exports the working
palette as Fiddler Windows-1252 or UTF-8. Import captures at most 64 KiB, previews
the name, bounds and two 32-color strips, and requires confirmation. Encoding
is explicit and can be changed in the review. A UTF-8 BOM requires the UTF-8
choice; UTF-16 and malformed UTF-8 are refused. Cancel leaves the document intact.
Confirmation checks the complete working state, raw drafts, selected project,
source binding and saved-copy hash, then replaces all fields in one undo step.
The captured file is not reread after review. Import does not save automatically.

Palette text has exactly 35 logical lines: name, decimal unsigned table start,
decimal unsigned table end, then 32 decimal unsigned 16-bit colors. CRLF, LF
and CR are accepted. Missing/extra lines, invalid values and invalid names refuse
the entire import. Export writes CRLF with a final newline and no BOM. Complete
project names are retained; Windows-1252 export refuses unrepresentable names
instead of replacing them. Pending raw fields must be committed or undone before
exchange. Writes validate first, use a temporary sibling and atomically replace
the destination, with the install/source boundary checked again before commit.
Name whitespace is retained instead of the reference importer's trimming.

The Hues toolbar also exports the Fiddler name list as UTF-8 without BOM, CRLF:
`0x<zero-based-uppercase-hex-index> <complete-name>`. GUO displays one-based hue
numbers: displayed `0x0021` therefore exports index `0x20`. All loaded palette
names are exported in ID order; saved and committed working names participate.
Pending fields, stale/refused project documents and invalid multi-line names
refuse the export. Source names retain their original spacing. This list is an
export format, not an ID remapping or native palette writer.

The pinned Fiddler exporter writes Windows-1252 palette text, while its importer
uses the default StreamReader encoding and mutates fields incrementally. GUO
offers explicit encodings and validates the complete candidate before mutation.
Tests cover exact independent legacy bytes, all 16 bits, Unicode/BOM/encoding
failures, record lengths, number limits, immutable review snapshots, stale state,
source/install protection, atomic destination retention, full-name guards,
native review cancel/confirm/undo and zero-based name-list output. See
`tools/scenarios/fiddler-f08de/README.md`.
