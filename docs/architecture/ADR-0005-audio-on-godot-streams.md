# ADR-0005: Audio Uses Godot's Streams, Not a DynamicSoundEffectInstance Shim

## Status

Accepted

## Date

2026-09-20

## Last Verified

2026-09-20 — measured against upstream at pin
`007ef8c3e13dec13fcc02387fe4817ef6f371c85`.

## Decision Makers

Project owner; `uo-render-engineer`.

## Summary

`ClassicUO.IO.Audio` is three files built on FNA's
`DynamicSoundEffectInstance`: the caller owns a PCM buffer, submits it, and
refills on a `BufferNeeded` callback. `UOSound` submits one complete buffer and
never refills. `UOMusic` decodes MP3 with the vendored MP3Sharp and tops the
queue up to three 32 KB chunks every frame.

Godot has no `DynamicSoundEffectInstance`, and it already does both of those
jobs. `Sound` keeps its whole public surface and its lifecycle; the protected
half — `SoundInstance`, `GetBuffer`, `OnBufferNeeded`, `Channels`, `Frequency`
— is replaced by an `AudioStreamPlayer` and one abstract `GetStream()`.
`UOSound` becomes an `AudioStreamWav` over the bytes `SoundsLoader` already
returns; `UOMusic` becomes an `AudioStreamMP3`, and MP3Sharp does not get
ported.

## Context — measured, not assumed

### What the callers actually depend on

Everything outside these three files uses only the public surface: `Name`,
`Index`, `DurationTime`, `Volume`, `VolumeFactor`, `IsPlaying(curTime)`,
`CompareTo`, `Play(curTime, volume, volumeFactor, spamCheck)`, `Stop()`,
`Dispose()`, and `UOMusic.Update()`. `AudioManager` is the only caller, plus
`World` asking for the current music. The buffer protocol is private to the
trio, so replacing it is not observable to the port; replacing the *surface*
would be.

### The shape of the data

`SoundsLoader.TryGetSound` returns headerless 22050 Hz mono signed 16-bit PCM
— the `.mul` path strips a 40-byte name header, and the loose-`.wav` path
rejects anything that is not exactly that format. Music is an `.mp3` on disk,
named by `Music/Config.txt`.

### The alternative was not cheap

Shimming `DynamicSoundEffectInstance` on `AudioStreamGenerator` would keep all
three files verbatim. But `Sound` has no update hook — the refill happens on
FNA's own audio thread via `BufferNeeded`, and `UOMusic.Update` polls
`PendingBufferCount` from the game loop. Godot's generator is a pull buffer
that something has to push into, and nothing in `Sound` is in the scene tree to
do it. That shim is a push pump, a thread or an idle callback to drive it, and
a port of MP3Sharp on top — to reach a place Godot already is.

### Measured — `launchers\dev\audio_probe.bat`

* `AudioStreamWav` takes the raw PCM bytes as-is; no RIFF container is needed,
  so `SoundsLoader` does not change.
* It reports the exact length (0.2500 s for 11024 bytes at 22050/mono/16). This
  is load-bearing: `Play` sets `DurationTime` from it and `IsPlaying` compares
  against it.
* `AudioStreamPlayer.VolumeLinear` round-trips, so `Sound.Volume` maps straight
  across with no decibel conversion.
* A player parented into the tree plays, its playhead advances, and a
  non-looping stream stops itself at the end.
* `AudioStreamMP3.LoadFromBuffer` decodes a real UO music file — 139.5 s from
  the client install — and its `Loop` flag is settable.

One thing the probe caught that would otherwise have been a startup-order bug:
**a player must already be inside the scene tree before `Play()`**, or Godot
refuses with *"Playback can only happen when a node is inside the scene
tree"*. `AudioHost` therefore parents the player at creation and never defers
it.

## Decision

### 1. `Sound` keeps its surface; `SoundInstance` becomes an `AudioStreamPlayer`

`Play`, `Stop`, `Dispose`, `Volume`, `VolumeFactor`, `IsPlaying` and
`DurationTime` keep upstream's logic line for line, including the
`_lastPlayedTime` spam gate, the volume clamp, and `Max(value - VolumeFactor,
0)`. Only what they operate on changes.

`GetBuffer()` and `OnBufferNeeded()` are replaced by one abstract
`GetStream()`. `Channels` and `Frequency` move to `UOSound`, which is the only
thing that has to know the PCM format now.

### 2. `UOSound` builds its `AudioStreamWav` once

The buffer is immutable and the sound object is cached for the life of the
client by `Renderer.Sounds.Sound`, so the stream is built on first use and
kept. `Delay` keeps upstream's `(buffer.Length - 32) / 88.2f`.

That expression is wrong — 88.2 bytes/ms is 22050 Hz *stereo*, and this data is
mono, so the spam gate is half as long as intended, and the 32 is not the
40-byte header either. It is upstream's behaviour and it stays. Project rule 2.

`OnBufferNeeded`'s body is a commented-out distance-attenuation block in
upstream. It is carried across as a comment on `GetStream`, because it records
an intent the port will meet elsewhere, and deleting it would lose that on the
next merge.

### 3. `UOMusic` hands the file to Godot and `Update()` does nothing

`BeforePlay` reads the `.mp3` off disk and calls
`AudioStreamMP3.LoadFromBuffer`, setting `Loop` from the flag
`Music/Config.txt` supplied. `AfterStop` drops the stream.

`Update()` stays, and is empty. Upstream's whole reason for it — keeping three
chunks queued or the sound dies — is the engine's job now. The method is kept
because `AudioManager` calls it every frame and will keep calling it after
every upstream merge.

The bytes are read with `System.IO.File`, not `AudioStreamMP3.LoadFromFile`:
the music lives in the user's UO install, outside `res://` and `user://`, and
routing an absolute host path through Godot's `FileAccess` is a second thing
to be right about for no gain.

### 4. `AudioHost` owns the one node the players hang from

A static host `Node` is created on first use under `SceneTree.Root`, and each
`Sound`'s player is parented to it and freed with the sound, matching
upstream's `SoundInstance` lifecycle exactly. It exists because an
`AudioStreamPlayer` is a `Node` and `Sound` is not, which is the only genuine
impedance mismatch in this subsystem.

### 5. MP3Sharp is not ported

Nothing else uses it. It is 40 files of vendored decoder under
`sources/ClassicUO/external/`, and `GUO.csproj` has no package or project
reference at all today — keeping that true is worth more than the verbatim
line count.

## Alternatives Rejected

**Shim `DynamicSoundEffectInstance` on `AudioStreamGenerator`.** Keeps all
three files verbatim, which is the project's default preference. Rejected on
cost: see above. It also puts engine behaviour behind an XNA type name, which
is the failure mode `GUO.Compat` is explicitly kept narrow to avoid.

**Convert music to PCM at load and use `AudioStreamWav` for both.** One code
path instead of two. Rejected: a three-minute track at 44.1 kHz stereo is about
30 MB resident, per track, and it still needs a decoder.

**Use `AudioStreamPlaybackPolyphonic` and one shared player.** Fewer nodes.
Rejected: `Sound` owns per-instance volume and stop, and polyphonic playback
hands back an id rather than something with those properties, so the mapping
would have to be rebuilt by hand.

## Consequences

* `src/Audio/**` and `src/Render/Sounds/**` come off the staged-build exclusion
  list in `GUO.csproj`; the files move to `src/IO/Audio/`, matching upstream's
  `ClassicUO.IO/Audio`.
* MP3Sharp never enters the port. `GUO.csproj` still has no third-party
  reference.
* Music gains accurate `DurationTime` — the full track length rather than the
  duration of one 32 KB chunk — which makes `IsPlaying(curTime)` mean what its
  name says.
* Looping music no longer glitches at the seam. Upstream refills a partial
  chunk from the rewound stream and submits it as if it were full; the engine
  loops the decoder instead.
* `Sound` is now the rewrite tier, not the verbatim tier. Three files, and the
  audit should say so.

## Validation

* `launchers\dev\audio_probe.bat` — every fact this ADR rests on, re-measured:
  raw PCM in, exact length out, linear volume, a player that actually runs,
  and a real UO `.mp3` decoded. The music stage reports SKIP without
  `UO_CLIENT_DATA` and writes nothing either way.
