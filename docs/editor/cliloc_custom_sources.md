# Custom Cliloc sources

F15e, L3 editor authoring. Select **Custom 1** or **Custom 2** in Assets > Cliloc,
then use the folder icon to choose a source file. The native picker offers
`cliloc.*` and All files; arbitrary extensions are supported. Both plain and BWT
files use the existing exact-file reader. The browser and inspector identify the
slot and filename; the source path is available in a tooltip.

The two slots follow Fiddler 4.24's custom-language file opening, pinned at
`610c930833dac8d158f08a0af36da43dd90d08b4`,
`UoFiddler.Controls/UserControls/ClilocControl.cs:TestCustomLang`. They are source
selectors, not runtime language codes. They do not change a client profile,
runtime fallback, `Clilocs.txt`, or the read-only Speech mode.

Each file supports the same text edits, add/remove, undo, recovery and reviewed
[CSV exchange](cliloc_csv_exchange.md) as installed languages. A valid empty
file can receive Add or CSV import. Missing or malformed sources show a source
error and clear any previous source's inspector. Choosing an invalid file leaves
the previous assignment intact. File paths are remembered privately in Godot's
editor-local project metadata (`guo_cliloc/custom1_source`, `custom2_source`).

## File and version isolation

Custom project documents live at
`metadata/cliloc/custom/<source-identity>/<source-sha256>/<slot>.json`.
The identity is lowercase SHA-256 of the resolved absolute source path encoded
as UTF-8 (uppercase path on Windows). It prevents two files with identical bytes
from sharing drafts. The content hash separates versions, and the slot separates
Custom 1 from Custom 2. JSON stores the hashed `source_identity`, not the source
path. Installed-language JSON paths and recovery keys remain unchanged.

Switching files retains each visited source/version's unsaved text, including
invalid raw drafts. Explicitly choosing a changed source creates or opens that
version's document; prior saved versions remain on disk and prior session drafts
remain cached. Returning to the earlier bytes/path reuses the earlier version.
There is no automatic rebase or cleanup. **Reopen document** confirms discarding
the current unsaved document and its recovery entry before loading the source
and the matching saved project document.

Source files remain read-only inputs. Source hashes are checked before save or
exchange. Shared project/export destination guards refuse registered source
paths and resolved directory/symlink aliases, including sources outside the
installation. Atomic output replacement preserves backing files when an output
is a hardlink alias. These are application guards, not operating-system file
permissions. This source-opening feature does not emit native files;
[F15b native staging](cliloc_native_staging.md) supplies a separate reviewed
export, including explicit custom output filenames.

## Verification

`python tools/editor_smoke/run.py --reload` checks source/slot/version isolation,
plain and real BWT reading, flags, save/reopen, source-byte preservation, CSV,
source-path guards, malformed and missing sources, empty-source import, invalid
draft retention, editor-local recovery and installed-language separation.
The authorized windowed workbench proof captures the custom inline inspector
and its CSV review target. [Scenario recipe](../../tools/scenarios/fiddler-f15e/README.md).
