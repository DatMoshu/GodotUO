# Sound-effect WAV export

F14a, L3 editor, compiled only under `#if TOOLS`. Manual authoring requires no AI
service or additional codec. Assets → Sounds → Effects exposes native icon actions
for Play, Stop and Export WAV, with accessible names and tooltips. The inspector
shows aligned name, sample format, rate, sample count and duration fields. Empty
image preview/zoom controls and the Image tab are hidden for text/audio selections.

Export WAV opens Godot's filesystem save picker, suggesting
`sound_0x<ID>.wav`. Cancel writes nothing. The dialog belongs to the sound panel,
so addon teardown closes it. Music retains its existing playback actions and is
not offered this effect-PCM exporter; MP3 conversion is separate work.

The exporter reads the selected effect again through `SoundsLoader.TryGetSound`.
This is the same active PCM the existing player receives, including supported
loose/pack overrides. It wraps those bytes in a RIFF WAVE PCM header: 22,050 Hz,
one channel, 16 bits, little endian, block alignment 2 and byte rate 44,100.
Sample data is copied unchanged. There is no resampling, normalization, silence
trimming, name conversion or modification of the native sound archive/index.

PCM must be nonempty, contain whole two-byte samples and fit 64 MiB. Output paths
resolve directory aliases and refuse the configured source installation. Writes
use a temporary sibling followed by atomic replacement, so a pre-existing output
hardlink's backing file is not truncated. Invalid input and cancellation leave
existing output untouched. Native save-picker overwrite confirmation is retained.

## Verification

`python tools/editor_smoke/run.py --reload` exercises signed extrema, silence,
sample count and exact PCM bytes through Python's independent standard-library
`wave` reader. A wrong-rate header must fail that check. Godot's WAV loader checks
the resulting format and duration. The suite covers empty/odd/oversized input,
unchanged output on refusal, the source-install guard, hardlink preservation,
native picker type/cancel/export, effect action registration and music separation.
It exports one active effect and checks source sound-file hashes, sizes and
modification times before/after. Existing sound-preview startup remains covered
by the panel smoke; automated startup is not a human listening certification.

The authorized `--windowed --assets-workbench` proof includes a sound inspector
capture. Reproducible fixture details are in
`tools/scenarios/fiddler-f14a/README.md`.

Sound working documents, WAV replacement/import, staged native sound writing,
names/list exchange, batch WAV export and slot/removal controls remain separate
F14 stories. This slice exports a selected existing effect to a standard WAV.
