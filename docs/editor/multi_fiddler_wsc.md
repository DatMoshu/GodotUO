# Reviewed Fiddler WSC exchange

F24a adds an explicit **Fiddler WSC (reviewed world items)** profile to the
existing Multi Editor. This is manual L3 authoring and Tools; no client option,
AI service or new drawing workspace. The existing **GUO legacy WSC (keeps Color)**
remains available. Detection continues to choose the legacy profile.

Each complete `SECTION WORLDITEM` supplies decimal uint16 ID and signed int16
X/Y/Z. Unknown world fields are inert. The reviewed profile ignores Color as
Fiddler does. It accepts 1–65,535 complete records, limited to 4 MiB, preserving
order and duplicates. Missing, duplicate, malformed or overflowing required
fields are refused. ID 65535 is refused on input/output: the pinned reader uses
it as an internal sentinel and otherwise returns a zero-filled allocated slot.
GUO reports the problem rather than silently creating that component.

## Coordinate and visibility review

Fiddler's WSC reader has a world-coordinate heuristic. With minimum X/Y initialized
to 10000 and maximum X/Y to zero, if both minima are positive and each exceeds
its corresponding extent, subtract those minima from every X/Y. Z is unchanged.
GUO matches that pinned behavior, including its seeded-minimum quirk. For example,
`120,80` and `123,82` become `0,0` and `3,2`; a single `15000,15000` becomes
`5000,5000`, while `20000,20000` stays unchanged. Review displays the actual
imported offsets and explains the heuristic. Optional centering is a separate
operation, off by default, performed after the heuristic.

WSC stores no visibility flags. Fiddler's optional `DynamicItemIds` set maps
listed IDs to hidden components. GUO's review provides a decimal comma-separated
dynamic-ID field, empty by default; changes update the preview and invalid IDs
disable confirmation. The imported project retains the resulting 0/1 visibility.
CLI `import` accepts optional `dynamic_ids`, a bounded list of uint16 integers.
This explicit set does not claim to migrate Fiddler application preferences.

## Export and guards

Export matches the pinned writer: decimal fields separated by tabs, Windows
CRLF, Color 0, no visibility/raw-flag/level field and no blank separator line.
If hue or non-default flags would be lost, direct unreviewed editor export
refuses. The review names the counts and requires explicit field-loss consent;
the working document keeps its hues, visibility and raw flags. Fiddler may apply
its coordinate heuristic when reopening an exported positive-coordinate file.

The profile reuses the captured-file/document/destination checks and atomic
output of [reviewed TXT](multi_fiddler_txt.md). Cancel makes no changes. Source
installation and protected sources remain read-only. Arbitrary world metadata
is not retained in the multi project; this is component interchange.

## Independent verification

`multi_wsc_reference.py` compiles the unchanged WSC import case/export method
from the same hash-pinned Fiddler source as TXT/UOA. Its private wrapper supports
an explicit dynamic-ID set and preserves the source's Beer-Ware notice. No
third-party source/binary is committed. `probe_multi_wsc.py` records legacy GUO
differences and the sentinel defect; it does not count mismatches as parity.
`proof_multi_wsc.py` compares both directions, exact export bytes, duplicate
order, signed limits, dynamic classification and three coordinate cases.
This is a scalar contract, not spatial-renderer or Fiddler GUI certification.

See [the reproducible recipe](../../tools/scenarios/fiddler-f24a-wsc/README.md).

Private run `20261021_guo-worker-fiddler-parity_multi-wsc` records 517 synthetic
records in both directions with identical export bytes, corrupt-ID refusal by
both readers, seven Python regressions and the full multi suite. The final
Windows proof passes all 17 WSC checks at three widths after one visual
correction batch. Full smoke and assembly reload pass; each reload pass
completes 237 multi report entries, including native 151-component staging and
independent read-back. Failed sandbox/harness/initial-layout reports are retained
separately as diagnostics. Existing broad art skips/subchecks certify no WSC
rendering behavior.
