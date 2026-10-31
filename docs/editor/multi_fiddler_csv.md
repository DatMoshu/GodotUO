# Reviewed Fiddler CSV multi exchange

F24a adds explicit **Fiddler CSV (hex IDs/flags, retains Unk1)** to the existing
Multi Editor. This is manual L3 authoring and Tools. Legacy PUNT, SwervUO and
CentrED CSV remain separate; automatic detection keeps their earlier behavior.
Choose this profile explicitly for the pinned Fiddler contract.

## Fields and preservation

The canonical header is `TileID,OffsetX,OffsetY,OffsetZ,Flag,Unk1`. IDs are
hexadecimal uint16, offsets signed decimal int16, and flags hexadecimal int32
bit patterns. Bare `1650` means item 0x1650; bare flag `100` means 256.
`0xffffffff` retains signed flag -1, not boolean 1. When visibility is edited,
the existing text-flag policy emits edited 0/1 while retaining the original
flag in the working document for undo.

The sixth column retains the signed trailing int32 called `Unk1` in Fiddler.
Input accepts decimal, `0x`-prefixed hexadecimal, empty or absent values (the
last two mean zero). Older `Cliloc` header spelling is recognized as in the
pinned reader; this numeric column does not import localized-string content.
Export uses lower-case hex IDs padded to four digits, lower-case hexadecimal
32-bit flags/Unk1, decimal offsets and Windows CRLF. Output numeric ASCII bytes
match Fiddler's Windows-1252 export exactly.

Order and duplicate records survive. Source coordinates stay unchanged by
default; optional centering changes X/Y only. No WSC world-coordinate heuristic
applies. Input/output are bounded to 4 MiB and 1–65,535 records. Extra columns,
blank/malformed records, overflow and nonnumeric Unk1 are refused. Fiddler can
silently turn malformed Unk1 into zero; GUO explicitly refuses it to avoid loss.

The existing components project adds optional eighth `text_extra`, after the
seventh raw `text_flags`. Six/seven-column projects remain readable; a null
seventh placeholder is supported. History, undo/revert, recovery, copy/paste,
stamps and proven one-to-one transforms retain the extra field. Other exports
refuse nonzero Unk1, even reviewed TXT/UOA/WSC; native translation is separate.
The native writer refuses nonzero Unk1 even with ordinary 0/1 flags.

## Review and guards

The existing review gains an Unk1 column. It shows the component count, bounds,
IDs and signed flags/extra values before confirmation. CSV has no hue or level
field. Hued export requires explicit loss consent and retains working hues.
Captured source/document/destination checks, cancellation, protected source/install
paths and atomic output reuse [reviewed TXT](multi_fiddler_txt.md).

CLI `import`/`export` uses `csv-fiddler`; parallel `text_flags` and `text_extra`
arrays retain both int32 fields. Export validates array lengths/types/bounds
before any write. Legacy format dispatch refuses nonzero extra-field loss too.

## Independent proof

The explicit pinned reference builder now includes the unchanged CSV import
case, `ParseCsvUnk1` and exporter beside TXT/UOA, retaining the Beer-Ware notice
in private generated source. Nothing runs third-party source during editing.
`proof_multi_csv.py` checks both directions, exact output bytes, signed limits,
duplicates, older extra forms and corruption refusal. TXT/UOA regression proofs
run against the expanded probe. This is scalar interchange, not Fiddler's
spatial renderer or native extra-field translation.

See [the reproducible recipe](../../tools/scenarios/fiddler-f24a-csv/README.md).

Verified in `20261021_guo-worker-fiddler-parity_multi-csv`: 517 independent
records in both directions with exact export bytes; TXT/UOA probe regressions,
27 text-format unit regressions and the full multi suite passed. Focused Windows
UI passed 23 CSV checks, including import/export fitting at three widths; final
captures were inspected. Full smoke and assembly rebuild/reload passed (exit 0).
Both reload passes completed native 151-component staging/read-back with an
unchanged installation; nonzero Unk1 alone was refused before stage creation.
The existing nonblocking `splat_lods_present` failure and harness skips remain.
