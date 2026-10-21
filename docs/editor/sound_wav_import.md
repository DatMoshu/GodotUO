# Sound-effect WAV import and project PCM

F14c is L3 editor authoring, compiled only under `#if TOOLS`. It needs no AI,
external codec or runtime option. Assets → Sounds → Effects offers **Import WAV**
beside playback and WAV export. Music is not an effect PCM document.

Choose a WAV in the native filesystem picker. The review captures its bytes,
shows current/incoming sample counts and durations, and offers separate Play
current, Play incoming and Stop actions. Cancel changes nothing. **Import into
working copy** retains the working name and replaces only the project's PCM.
If the document changes while the review is open, confirmation refuses it;
close the review and choose the file again. Later input-file changes cannot
alter the captured candidate. The original installation remains read-only.

## Accepted audio

Uncompressed RIFF WAVE PCM, mono, 16-bit little-endian, 22,050 Hz; byte rate
44,100, block alignment 2, one 16-byte format chunk before one data chunk.
PCM must be nonempty, even-sized and at most 64 MiB; the complete file is at
most 65 MiB. The RIFF size and every padded chunk boundary must match the file.
Unknown chunks are skipped, including their odd-byte padding; duplicate format
or data chunks, inconsistent headers and truncation are refused. Import copies
samples exactly, without resampling, normalization or retaining container tags.

This follows Fiddler 4.24's core PCM format (`610c930833dac8d158f08a0af36da43dd90d08b4`,
`Ultima/Sound.cs`, `CheckAndFixWave`), with additional consistency checks.
GUO retains the working name instead of renaming it from the incoming filename.
The inline name editor remains available. No third-party codec code is copied.

## Working document, save and recovery

Play and WAV export use the working PCM when replaced. The inspector labels
**Project PCM**, shows working duration and source duration, and retains a
secondary source-play action. Project preview uses an editor-owned mono Godot
WAV stream; Stop, reopen/reload cleanup and addon teardown release playback.
Playback startup tests do not certify human listening quality.

Import participates in document undo/redo and undoable revert to source. Names
and PCM share the working history. A pending valid name is committed before
import; invalid names block import without replacing PCM. History retains up to
100 states and approximately 128 MiB of distinct history PCM, always keeping
the last two states. Original and saved baselines can retain additional buffers.

**Save copy** writes schema 2 `metadata/sounds/0xNNNN.json`, kind
`guo/sound-effect`, retaining ID, source fingerprint and name. `pcm_sha256` is
null for source PCM, or the lowercase SHA-256 of raw working PCM. Changed PCM
lives in `assets/sound-documents/<pcm_sha256>.wav`. Blob paths are derived from
validated hashes; they are not arbitrary manifest paths. Every referenced blob
is bounded, decoded and checksum-verified when reopening or reusing it. A
missing/corrupt blob refuses the document; it is never silently replaced by
source audio. Schema 1 `guo/sound-name` documents and naming-only recovery are
accepted and upgrade on save. Missing schema 2 PCM references are refused.

Blobs are written before the atomically replaced manifest. Source binding is
rechecked on import, save and document WAV export. Recovery stores small hash
references plus saved-name/PCM baselines, not base64 audio. It materializes a
project blob even before explicit Save copy; this makes recovery independent
of the incoming file. Failed recovery remains preserved and read-only. Failed
recovery writes retain the previous recovery row. **Reopen sound** explicitly
confirms discarding unsaved name and PCM state. Unreferenced blobs can remain
after undo, failed saves or discarded drafts; no automatic deletion is implied.

These inert project documents are neither Store packs nor native sound records.
F14d adds separately reviewed [native sound staging](sound_native_staging.md)
with pinned independent sound read-back. WAV import itself changes only the
project document; no runtime source loader is changed.

## Verification

`python tools/editor_smoke/run.py --reload` uses Python's independent `wave`
writer for input and its reader for exact exported PCM. `EditorSmokeSoundImport`
covers chunk/format refusals, no partial mutation, undo/revert, project save,
legacy migration, missing/corrupt blobs, recovery, source binding, the native
picker and review cancellation/confirmation, captured candidates, project
playback and shutdown. Active sound-file SHA-256, sizes and mtimes are compared.
The authorized `--windowed --assets-workbench` proof captures the real import
review and project-PCM inspector. See the
[fixture recipe](../../tools/scenarios/fiddler-f14c/README.md).
