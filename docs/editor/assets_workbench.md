# Assets workbench

Layer: L3 editor authoring, compiled only under `#if TOOLS`. Moshu approved the
workbench proposal in the guo-worker-fiddler-parity terminal on 2026-10-20.
No AI gate or runtime option is needed; all manual authoring stays available.

The Assets main screen has a library rail, the existing asset panels, and the
shared inspector on the right. Library groups Art, Gumps, Animations, Hues,
Multis, Maps, Cliloc and Sounds; Workflows groups Bulk, Art set and Parity.
Art has Statics and Land children. A compact family picker replaces the rail
when the browser becomes narrow. Each panel instance keeps its search and
selection when switching families. The family, thumbnail size, preferred image
editor and inspector width are editor-local preferences.

Cliloc has a [localized-string working document](cliloc_documents.md) inspector:
working/source text, preserved record flags, token warnings and a language-wide
Save copy action. The pixel preview, image toolbar and Image tab are hidden for
this text-only document. [CSV exchange](cliloc_csv_exchange.md) reviews imports
and provides explicit export scope/dialect choices. Two [custom source slots](cliloc_custom_sources.md)
open arbitrary exact files with independent working documents and read-only
source protection. Native cliloc output is a separate milestone.
The [tiledata sync review](cliloc_tiledata_sync.md) maps source/project item names
to Cliloc IDs, reviews selected adds/updates/removals and applies one undoable
working-document batch. It does not mutate tiledata or runtime strings.

Sounds uses aligned audio fields and native Play/Stop icons. Effects also offer
[WAV export](sound_wav_export.md); music does not use the effect-PCM exporter.
Selections without an image hide the Image tab and pixel preview controls.
[Loop audition and slot browsing](sound_audition_slots.md) add active-library
occupied/empty counts, all/empty views, read-only empty inspectors and next-empty
navigation. The loop status distinguishes source/project PCM and stops on
selection, family or working-document changes; no native slot writer is included.

The inspector starts with identity, copy ID and origin, then a bounded pixel
preview with integer nearest-neighbor zoom. Animation playback and frame scrub
remain available. The action row uses theme-native icons with tooltips and
accessible names. The pencil runs the preferred image editor; its adjacent menu
chooses Pixelorama's tab, Pixelorama's window or the configured Pinta editor and
remembers that choice. Import and PNG export have their own icons. Infrequent
and existing family-specific commands remain in the labeled More menu and the
asset grid's context menu. No English-label matching selects an icon.

Art offers source-image and source Wall-flag filters. ID/name search continues
to use the installed metadata; a project metadata draft does not rename runtime
assets or change source-flag filtering. A source-ID jump obeys the active filters.
Art source fields are aligned read-only rows. For modern static records,
[item metadata](item_metadata.md) is edited inline on Properties; Image lists
image operations and History shows the document summary. Other existing asset
families retain their previews and source text.

**Project changes** lists project image replacements, saved metadata documents
and visited unsaved metadata drafts. Selecting an entry returns to that asset.
**Review export** is enabled for a selected project static image replacement.
Metadata documents are explicitly excluded from static image export.

The export review shows source art and project art side by side, native pixel
sizes, a staged-data destination, the existing independent preparation/verification
result and a separate apply confirmation. Preparation changes no existing stage.
Apply reruns the verifier through `tools/fiddler_parity/replacement.py`, promotes
only a verified candidate, and retains the prior stage. Destination edits invalidate
preparation. Recovery appears only when the destination has a recovery journal.
Closing the review cancels its child job. The source installation stays read-only.
The narrow writer handles MUL/IDX and uncompressed UOP, preserving opaque UOP
entry headers. Compressed UOP and binary tiledata output remain unsupported.

The source comparison uses the overlay's saved original index for one synchronous
main-thread decode and restores the replacement index in `finally`. It writes no
source file and introduces no ported-code changes. The editor smoke checks both
that source and replacement differ in its fixture and that comparison leaves
the replacement active.

## Verification

`python tools/editor_smoke/run.py --reload` checks every existing editor panel
and the new typed actions, document validation, draft selection retention,
unknown-bit preservation, save, undo/redo, editor-local recovery and comparison.

With the person at the machine's agreement,
`python tools/editor_smoke/run.py --windowed --assets-workbench --out FOLDER`
captures the real workbench at 1920, 1366 and 1024 window widths and its export
review. Captures come from Godot viewports and use UTC evidence names. This
focused mode runs no export process and does not imply native tiledata parity.
The full project smoke remains required before commit.

Saved searches, favorites, editable batch metadata and the proposed additional
family-specific inspectors are follow-up stories. Existing multiselect/context
export remains available; these future tools are not represented as working.

[Batch static PNG replacement](batch_static_replacement.md) is available from
Art and Bulk: scan filename IDs and errors, compare selected images, prepare
one independently verified candidate, then explicitly apply or recover the
whole stage. It leaves project image overlays and metadata documents unchanged.

[Sound naming documents](sound_documents.md) add inline working names and a
persistent undo/save footer to effect selections. Invalid names survive selection
changes; project saves and editor-local draft recovery leave source PCM unchanged.
[WAV import](sound_wav_import.md) adds captured current/incoming review,
undoable project PCM, project preview/export and checksum-bound project WAV blobs.
Reopen confirms discarding both unsaved names and PCM. Native sound archives
remain outside this workflow.
[Sound lists and batch WAV export](sound_list_export.md) add reviewed
all/browser/selected scopes, UTF-8 and Fiddler CP1252 CSV, optional POL CSV IDs,
filename collision checks, cancellable whole-folder publication and sound
name/duration sorting. These standard-file exports leave native archives unchanged.
