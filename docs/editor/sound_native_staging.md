# Verified native sound staging

F14d adds **Export selected sound to a verified native stage** to the Sounds
toolbar. It is manual L3 editor authoring plus Tools, with AI disabled or enabled.
Choose an occupied effect, edit its working name or import WAV audio, then open
native export. Music and empty slots remain outside this replacement workflow.

Choose a staged folder and **Prepare verified copy**. Preparation resolves the
selected sound's physical row through the pinned ClassicUO `Sound.def` parser,
builds a private candidate and independently reads its name and PCM. Review the
current staged sound and native export: names, sample counts, durations, audition
buttons, physical row and every affected alias. A second **Apply verified sound**
confirmation publishes the reviewed candidate after rechecking it. Preparation
alone does not create or modify the destination stage.

The native record is a 40-byte UTF-8 name header followed by nonempty, even-length
mono signed 16-bit little-endian PCM at 22050 Hz, bounded to 64 MiB of samples.
An unchanged name retains all 40 original header bytes, including opaque bytes
after its first NUL. Changed names must fit 32 UTF-8 bytes and contain no NUL;
the new header is padded to 40 bytes. Samples are never resampled or normalized.

MUL/IDX replacement appends the reviewed record, repoints only the selected
occupied physical row and preserves its opaque extra word and the entire old
MUL prefix. Uncompressed `soundLegacyMUL.uop` replacement appends the original
per-entry header plus the new record, updates that one table entry/checksum and
preserves every other original byte. Identity exports retain exact file bytes.
Compressed sound UOPs are refused: the pinned sound loader does not decompress
them in `TryGetSound`. Insertion, slot allocation and deletion are separate work.

DEF aliases are copied in file order with the pinned parser's actual semantics;
the independently loaded mapping must agree before certification. Loose
`Sounds/<id>.wav` overrides are excluded from native read-back. A working document
whose loaded source differs from the native archive is refused instead of silently
replacing the archive underneath its override. Runtime music configuration and
music playback are unchanged.

Existing unrelated staged files and native sound edits survive. Unregistered
sound-file collisions, foreign-install stages, hardlinks, overlapping paths,
changed source/DEF/PCM/project save/working draft/review/candidate/prior stage
require a new review. Saved sound JSON uses a hash guard and atomic replacement,
preventing external saved-copy edits from being overwritten. Native export also
guards the current selection, project, source and document revision.

**Recover previous stage** restores the entire prior stage or its absence and
retains the candidate separately. Closing preparation cancels the owned process
and ends audition. The installation stays read-only. Export does not deploy or
activate the staged library.

Tools capture a schema 1 `domain: sound` snapshot: logical `id`, explicit
`packaging` (`mul` or `uop`), native source fingerprint, working `name`, absolute
`pcm_file` and its `pcm_sha256`. The fingerprint hashes the UTF-8 byte count of
the pinned loader's complete decoded name, an ASCII colon, those name bytes and
the PCM. This matches the existing working document's source binding. A separate
hashed native payload and compact manifest bind the transaction; PCM is not
embedded in its JSON plan. These private transaction files are not Store content.

```text
python tools/fiddler_parity/sounds.py prepare --source <install> --stage <stage> --edits <snapshot.json> --plan <new-folder> --client-version <version>
python tools/fiddler_parity/sounds.py apply --plan <new-folder>/plan.json --confirm
python tools/fiddler_parity/sounds.py recover --stage <stage> --confirm
```

The shared typed `sound` record contains a physical ID, complete native payload
and only `packaging` metadata. It requires explicit replacement and validates the
entire batch before copying files. No new Store type or runtime loader change is
introduced. The existing `fiddler_parity_doctor` launcher pair with `--build`
provides the pinned independent reader. Synthetic proofs live in
`test_sound_records.py` and `proof_sound.py`; the reproducible editor recipe is
`tools/scenarios/fiddler-f14d/README.md`.
