# Verified native Cliloc staging

F15b adds **Export a verified native Cliloc stage** to the Cliloc page's icon
toolbar. It is manual L3 editor authoring plus Tools, with no AI dependency.
Save copy continues to save project JSON. Native export has a separate review.

Choose a staged folder and native filename, then **Prepare verified copy**.
Preparation reads the selected exact source, validates its working document and
independently verifies a private candidate without publishing it. The review
shows added, edited and removed IDs, preservation counts, output encoding, and
every reviewed row in pages of 100. Select a row to read its full current and
exported text. **Apply verified Cliloc** opens a separate confirmation, including
addition/removal counts, before independent recheck and staged promotion.

Untouched native records retain their exact bytes, order, flags and six-byte
header. Duplicate IDs keep last-record-wins behavior: edits replace only the last
occurrence; removal deletes every occurrence of that ID so an earlier value
cannot reappear. New IDs append in ID order with flag zero. Empty text remains
an active string. Source flags cannot be changed by this working document.

Plain UTF-8 and bounded BWT sources are supported. Identity export retains all
original bytes, including BWT compression. An edited BWT file becomes standard
plain UTF-8 Cliloc, retaining the decoded header and untouched record bytes.
The review explicitly names this output encoding; it does not claim BWT encoding.
Runtime English fallback and loose `Clilocs.txt` are excluded from the oracle.

Custom 1/2 sources may have arbitrary input filenames. Their native output
defaults to `Cliloc.custom1` or `Cliloc.custom2`; the filename is explicit and
must be `Cliloc.<safe suffix>` (letters, digits, underscore or hyphen, 1–32
characters). Export does not register or activate a runtime language. An existing
installed target with different source bytes is refused: choose a source-matched
or unused custom filename instead of silently replacing another language.

The stage records the exact document source path/hash. Subsequent exports from
that source preserve unrelated edits already in its native staged file and other
staged files. A differently bound or unbound modified Cliloc is refused. Stage
creation also binds the absence of a new installation filename. Source, source
mapping, project save, draft, review, candidate, baseline and prior-stage changes
require a fresh review. Invalid drafts or failed recovery block native export.
Reopen retains its existing explicit discard confirmation.

**Recover previous stage** restores the prior complete stage or its absence and
retains the candidate separately. Closing during preparation cancels the owned
process. The editor refuses a destination supplying its currently loaded staged
data. Neither the installation nor an external custom source is written.

Tools workflow:

```text
python tools/fiddler_parity/cliloc_stage.py prepare --source <install> --input-file <exact-source-file> --filename Cliloc.enu --stage <stage> --edits <document.json> --plan <new-plan-folder> --client-version <version>
python tools/fiddler_parity/cliloc_stage.py apply --plan <new-plan-folder>/plan.json --confirm
python tools/fiddler_parity/cliloc_stage.py recover --stage <stage> --confirm
```

Edits use the existing schema 1 `guo/cliloc-document` snapshot, including source
hash, decoded header, compression state and custom-path identity. The native
capture additionally supplies `native_source_file` for custom sources, checked
against the exact resolved input path without cross-language Unicode case folding.
Native input
and decoded files are bounded to 64 MiB and 500,000 records; a patch retains the
working document's 10,000-change/2-MiB text limits. The pinned reader uses signed
16-bit text lengths, so native records exceeding 32,767 bytes are refused.
Long source text reviews live in a separately hashed `review.json`; the compact
transaction plan stays within its existing 16-MiB bound.

The typed `cliloc-file` record carries ID zero, complete native bytes and only
`filename` metadata. It requires explicit replacement. The writer validates the
whole batch and output paths before copying anything. New native filenames
record `source_exists: false` in private stage metadata; unrelated writer kinds
retain their existing copy-on-write behavior. No new Store type is introduced.

Run the existing `fiddler_parity_doctor` launcher pair with `--build` for the
pinned read-only oracle. Synthetic proofs are in `proof_cliloc.py` and
`test_cliloc_records.py`; the editor recipe is
`tools/scenarios/fiddler-f15b/README.md`. Native export does not certify runtime
font coverage, language fallback, shard deployment or automatic activation.
