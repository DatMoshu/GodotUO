# F14a sound-effect WAV export

Layer: L3 editor, no toggle, no AI dependency. Doctor: normal editor setup;
Python's standard library is used only by the verification fixture. Contract:
`docs/editor/sound_wav_export.md`. This fixture recipe follows the existing
F06a/F15a pattern and introduces no new runtime scenario verb.

Run `python tools/editor_smoke/run.py --reload`. `EditorSmokeSoundWave` verifies:

- A signed-extrema/silence fixture becomes PCM 22050 Hz mono 16-bit WAVE.
- Python `wave` independently reads exact frames and sample bytes; Godot reads
  the expected format and duration. A changed-rate header fails the oracle.
- Null/empty/odd/oversized PCM is refused before replacing output.
- Export into the configured install is refused; an external output hardlink
  is replaced without altering its synthetic backing file.
- Play/Stop/Export WAV are typed effect actions. The native save picker filters
  WAV, belongs to the panel, cancels without writing, and exports exact active PCM.
- Source sound files retain their hashes, sizes and modification times. Music
  does not receive the effect exporter.

For authorized visual review:
`python tools/editor_smoke/run.py --windowed --assets-workbench --out FOLDER`.
The sound inspector capture follows the existing art, batch and Cliloc captures.

Manual walkthrough: Assets → Sounds → Effects → select an effect → Play → Stop
→ Export WAV → Cancel → Export WAV to a folder outside the source install.
Open it in an independent player and compare with preview. Music must retain
its own playback path. Export must never change the source archives or indexes.
Record human listening separately from automated playback-startup evidence.
