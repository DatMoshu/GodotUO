# Fiddler TXT multi interchange

F24a adds an explicit **Fiddler TXT (hexadecimal IDs)** profile to the existing
Multi Editor import/export menus and F3 actions. This is an L3 editor feature.
It extends the existing multi document and canvas; it adds no runtime option.

The important distinction is the item ID radix. Fiddler 4.24 reads bare `1650`
as hexadecimal `0x1650`. GUO's earlier TXT reader reads that token as decimal
1650. The earlier profile remains available as **GUO legacy TXT (decimal IDs
unless 0x-prefixed)**. File-extension detection keeps that existing behavior;
choose the Fiddler profile explicitly for Fiddler files.

## Records and review

Each line carries `ID X Y Z Flags`. IDs are uint16 hexadecimal, offsets are
signed decimal int16 and flags are signed decimal int32. Export writes
`0x`-prefixed uppercase hexadecimal IDs, decimal values and Windows CRLF lines.
Numeric ASCII bytes are identical in UTF-8 and Fiddler's Windows-1252 encoding.
The profile accepts 1–65,535 records and at most 4 MiB. Order and duplicate
components survive; nothing is deduplicated or sorted.

The import review shows item IDs, coordinates, flags, total count and bounds.
Source coordinates are preserved by default. Optional **Centre X/Y around zero**
shifts only X/Y, leaves Z/flags unchanged and refuses signed-coordinate overflow.
The first 200 rows are shown; confirmation operates on every captured record.
Blank lines, surrounding/repeated whitespace and uppercase `0X` prefixes are
accepted as input conveniences; output uses Fiddler's canonical line syntax.
Comments, missing/extra fields, non-decimal offsets/flags and numeric overflow
are refused with a line-specific error. This is not a general text autodetector.

Raw flags survive working-document history, project save/reload, stamps,
copy/paste and one-to-one transforms. The optional seventh project component
column is described in [data_formats section 28](../data_formats.md).
The editor displays nonzero flags as shown. A visibility edit exports 0/1 when
it differs from the retained raw flag's visibility; undo restores the raw value.
The native multi writer has a different flag contract and refuses effective
opaque TXT flags rather than silently discarding them. Translation is separate.

TXT has no hue or level fields. Exporting a document with nonzero hues requires
explicit **Export without hue** confirmation. The working document keeps its
hues. Use a suitable existing format when hue interchange is required; this
milestone does not certify that other format's external compatibility.

## File boundaries

Import captures bounded bytes before parsing and checks source content/path and
the working-document revision again at confirmation. Export captures the exact
candidate bytes, reviews the destination's previous contents and checks both the
document and destination before an atomic file replacement. Canceled reviews
make no document/file change. Changed inputs, destination links/contents and
document changes—including undo/redo—require a fresh review. The shared editor
guard refuses installation writes and overwriting registered source documents.

## Independent reference

`tools/fiddler_parity/multi_text_reference.py --source <pinned-file>` builds a
development-only probe from an explicitly supplied, hash-checked
`Ultima/MultiComponentList.cs` at POL UOFiddler commit
`610c930833dac8d158f08a0af36da43dd90d08b4`. Its generated source retains the file's
Beer-Ware notice. The unchanged TXT parsing case, export method and scalar entry
declaration compile in a small wrapper outside the checkout. No Fiddler source
or binary is committed, and the editor does not need that probe to exchange TXT.

`proof_multi_text.py --reference <Reference.dll> --out <new-folder>` checks both
directions against that probe: IDs, signed offsets/flags, order, duplicates and
exact Windows export bytes. The probe deliberately excludes `ConvertList`, image
rendering and other formats. A passing scalar interchange proof is not proof of
Fiddler's spatial renderer, native multi containers or full F24/F25 parity.

Run `python tools/multi/test_fiddler_text.py` for numeric, visibility, radix,
centering, CLI validation and refusal regressions. The regular editor smoke
also exercises document retention, guarded export and GUI confirmation.

Private run `20261021_guo-worker-fiddler-parity_multi-interchange` records the
517-record bidirectional comparison, exact output bytes, padded numeric input,
nine Python regressions and the full multi test suite. The focused native UI
proof passes at 1024/1366/1920 widths, including hue-loss confirmation. Full
smoke passes; before/after assembly reload each completes 187 multi checks,
including a 151-component native stage, read-back and unchanged installation.
The initial canceled multi report was a false green and is retained as a
diagnostic, not acceptance evidence. Dock lifetime and configured build paths
were corrected before the completed native checks. The broad windowed smoke
hit an unrelated AI composer IME crash; the focused multi visual proof and full
headless/reload proofs are separate recorded results.
