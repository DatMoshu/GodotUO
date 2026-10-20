# Sound naming working documents

F14b is an L3 editor feature behind `#if TOOLS`, available without AI. Assets →
Sounds → Effects shows an inline working name, source name, audio properties,
undo/redo/revert and **Save copy**. Names belong to the selected world project;
runtime sounds and native sound archives retain their existing behavior.
F14c subsequently adds [project WAV import](sound_wav_import.md), preview and
export to the same working document.

An effect document binds its ID to the active loader's complete name string and
PCM with SHA-256. Pack/loose overrides therefore participate in the baseline.
F14b edits only a display name. The source display name stops
at its first NUL terminator; all decoded header-name bytes still participate in
the source fingerprint. New names fit 32 UTF-8 bytes and contain no NUL; an
unchanged original display name is preserved even if its
decoded UTF-8 size exceeds that limit. Invalid raw input remains attached to
its document when changing selection, and Save remains disabled until corrected
or undone. Name edits coalesce on submission, focus loss, selection change or
save. Revert to source is undoable; undo after save makes the document dirty.

F14b documents originally used `metadata/sounds/0xNNNN.json`, schema 1, kind
`guo/sound-name`, ID, source fingerprint and working name. F14c reads them and
upgrades saves to schema 2 `guo/sound-effect`, adding a nullable raw-PCM hash
reference. Project WAV blobs hold changed PCM; naming-only documents retain
source PCM. These are not native headers or a Store content type. Reopening
checks the ID, fingerprint and name before accepting the document. Saving
rechecks the current active sound and refuses a changed baseline. Writes use
a temporary sibling and atomic replacement. Resolved project paths must stay
inside the selected project and outside the source installation.

Saved project names appear in the sound browser and name search. Unopened
documents are discovered from the selected project's sound metadata directory;
the entire audio archive is not copied into working documents. Browser IDs and
existing source playback are unchanged. WAV export uses working PCM and checks
its source binding; changing only a name does not alter exported samples or
suggest a native archive write.

Unsaved documents and raw name drafts are stashed in Godot's local project
metadata during addon teardown/reload, separately from explicit project saves.
Recovery verifies source binding and saved-name/PCM baselines before mutation.
A refused recovery stays preserved and makes that document read-only. The
**Reopen sound** action asks before discarding the selected document's unsaved
state/recovery and reopening its source plus saved copy. Cancel retains edits.
This recovery is a local editor convenience, not a durable cross-machine backup.

## Verification

`EditorSmokeSoundDocument` covers immutable PCM copies, name byte limits,
invalid drafts across selections, undo/redo/revert, save/reopen, ID/hash mismatch,
source PCM/name changes, install refusal, recovery and failed-recovery handling.
It saves a real active effect's project name, searches it, tests reopen cancel
and confirmation, then verifies source sound-file hashes, sizes and mtimes.
The required `python tools/editor_smoke/run.py --reload` proves teardown and
assembly reload. The authorized `--windowed --assets-workbench` proof includes
an unsaved working-name inspector. Fixture instructions are in
`tools/scenarios/fiddler-f14b/README.md`.

F14c adds WAV import/replacement and project PCM preview. Native sound staging,
CSV exchange, new slots, deletion and music editing remain separate stories. This document
does not grant occupied-record replacement outside the already approved static
art workflow.

## Refreshed Fiddler reference

The refreshed 4.24 source (`610c930833dac8d158f08a0af36da43dd90d08b4`,
`Ultima/Sound.cs`) uses a 40-byte header, matching GUO's active PCM boundary.
Its `SoundsControl.cs` rename/import UI limits names to 32 characters. GUO's
project naming document uses a 32-byte UTF-8 limit and refuses invalid names
instead of truncating them. The older cached `2b9e492` reader used 32 header
bytes and is not a current native-audio reference. F01 sound read-back remains
required before F14d stages native sound records; no native writer or ported
loader behavior changes in this milestone.
