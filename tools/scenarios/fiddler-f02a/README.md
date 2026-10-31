# F02a single static-art replacement

Layer: editor L3 + Tools; toggle: none; doctor:
`launchers/dev/fiddler_parity_doctor.bat --build` (or `.sh`).
Docs: `tools/fiddler_parity/README.md`.

Synthetic tool recipe:
`python -m pytest tools/fiddler_parity/proof_static.py -q --basetemp <dedicated-scratch>`.
The runtime scenario schema cannot drive these CLI operations, so the fixture
specification follows the F01a convention:

- Create MUL/IDX and uncompressed UOP art with one occupied ID and a synthetic
  edited PNG containing transparent and near-black pixels.
- Prepare: original and current stage stay unchanged; independent read-back
  certifies the candidate and untouched IDs.
- Apply with explicit confirmation. Repeat for a second edit; recover the
  previous stage byte-for-byte, including its bookkeeping files.
- Change the source, candidate or PNG after preview: Apply must refuse.
- Refuse a free slot. Inject a promotion failure: the old stage must remain
  intact. Simulate interruption after moving it aside: Recover must restore it.
- A second writer cannot acquire the same stage lock.

Editor recipe: open Art, select a static, Replace from image, inspect the project
preview, Export verified static stage, Prepare, Apply, then Recover. Closing
before Apply leaves the current stage unchanged. The headless asset smoke checks
the inspector action, initial dialog state and teardown. A windowed walkthrough
is still needed to certify visual layout and interaction, and is not implied by
the synthetic tests.
