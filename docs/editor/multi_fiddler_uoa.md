# Fiddler UOA text interchange

F24a's next format adds **UO Architect text (Fiddler, decimal IDs)** to the
existing Multi Editor. Choose it explicitly: the earlier GUO UOA profile remains
available as **GUO legacy UOA text**, with its earlier detection and centering
behavior. This is manual L3 authoring, using the same document, canvas and
guarded review as [Fiddler TXT](multi_fiddler_txt.md).

The format has four header lines followed by `ID X Y Z Flags` records. IDs are
decimal uint16, X/Y/Z signed decimal int16 and flags signed decimal int32.
Unlike Fiddler TXT, bare `1650` means decimal 1650 here. Input count must match
the body exactly; malformed fields, extra/missing records and overflow are
refused. Limits are 1–65,535 components and 4 MiB. Order and duplicates survive.

Fiddler's reader ignores the first three header values. Its exporter always
writes `6 version`, `1 template id`, `-1 item version`, then the current count
as `N num components`. GUO matches this behavior and states it in review;
custom input header values are not project metadata. Export uses canonical
decimal values and Windows CRLF. Numeric ASCII output matches the reference's
Windows-1252 bytes exactly. Surrounding/repeated whitespace and padded numbers
are accepted; output is normalized to the reference syntax.

The review shows decimal IDs, raw flags, bounds and count, with source
coordinates preserved by default. Optional centering changes X/Y only, and
refuses coordinate overflow. Nonzero hue requires explicit loss confirmation
for export; the working copy retains hue. The format has no level field.
Opaque flags retain the same optional seventh project component column as TXT,
including history, copy/paste, stamps and proven one-to-one transforms. Other
legacy exports and native staging refuse silent opaque-flag loss.

Import and export reuse captured-file/document/destination checks, protected
source paths and atomic guarded output. Cancel makes no change. Installation
writes and changed reviews are refused. This is text interchange; it does not
certify UO Architect binary, Fiddler spatial conversion or native opaque flag
translation.

The development reference now compiles the unchanged UOA import case and
`ExportToUOAFile` beside TXT from the same explicit hash-pinned source file.
Run `python tools/fiddler_parity/proof_multi_text.py --format uoa-fiddler
--reference <Reference.dll> --out <new-folder>` for bidirectional scalar and
exact-byte proof. The standard Python regression file covers both text
profiles; the regular editor smoke and focused multi text proof cover review,
centering, decimal display, project retention and hue-loss confirmation.

Private run `20261021_guo-worker-fiddler-parity_multi-uoa` records a passing
517-record bidirectional comparison with identical export bytes, 13 Python
regressions, the full multi suite and focused Windows UI review at three widths.
Full smoke and assembly reload passed; each reload pass completed 223 multi
report entries, including native 151-component staging and independent read-back.
The initial sandbox HTTP/profile failure is retained separately as diagnostic
evidence. See [the review recipe](../../tools/scenarios/fiddler-f24a/README.md).
