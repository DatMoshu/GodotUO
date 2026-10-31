# F14c sound WAV import into working documents

Layer: L3 editor. Toggle/AI: none. Doctor: normal GUO editor setup, no additional
dependency. Contract: `docs/editor/sound_wav_import.md`. This is an editor-smoke
recipe, not a new runtime scenario verb.

Run `python tools/editor_smoke/run.py --reload`. `EditorSmokeSoundImport` creates
independent PCM WAV through Python's standard-library writer, then checks exact
signed bytes, odd unknown chunk padding, malformed/truncated/duplicate chunks,
format/rate/alignment refusals, bounded input, unchanged state on rejection,
name retention, undo/redo/revert, save/reopen and independent working WAV export.
Project blob checksum/path validation, missing blobs, recovery saved baselines,
schema 1 migration and changed active-source refusal are included. Native picker
cancel, review cancel, stale review refusal, captured bytes after file changes,
project playback, UI save/reopen and teardown run against the real sound panel.
Installed sound files are hashed/stamped before and after; they stay unchanged.

Manual: select an effect, edit its working name, import valid mono 16-bit 22050 Hz
WAV, compare sample counts/durations, preview both candidates, cancel, then import
again and confirm. The name is retained; origin says Project PCM. Undo/redo the
import, Save copy, reopen and export WAV. Rebuild/reload with unsaved PCM and
confirm recovery. Try stereo, 44100 Hz, corrupt or empty input: refuse without
changing working PCM. Remove a copied project blob in an isolated fixture:
reopen must report the missing data, without substituting source PCM.

For authorized native UI review run
`python tools/editor_smoke/run.py --windowed --assets-workbench --out FOLDER`.
Inspect `sound-wav-import-review` and `sound-project-pcm` captures. The visual
fixture imports synthetic silence without playing it automatically. Native
sound archives, music, new slots, deletion and staged sound export are separate.
