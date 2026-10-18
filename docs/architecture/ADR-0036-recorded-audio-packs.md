# ADR-0036: Recorded audio packs as an optional client feature

Status: Proposed for the owner-authorized PR41 restructure (2026-10-13).

The client plays prepared audio; generation belongs in the editor or Labs.
`ContentAudioEnabled` is an L1 profile option, false for new and existing
profiles. Options > Sound > **Regional audio and recorded NPC voices** enables
it. Normal speech never starts synthesis or calls a service.

Verified active content packs supply `region`, `music`, `sound`, and `voice`
components. Region JSON uses the existing name/facet/priority/areas contract,
plus optional `music` and `sound` arrays of declared component references.
References must resolve through the content lock to the matching audio type.
Both playlists are independent. Higher-priority regions win; equal priorities
keep manifest order. Bounds include Z and use half-open width/height/depth
extents. Existing loose-file authoring remains until its separate migration.

Voice JSON is `{"schema":"guo/voice@1","clips":{"exact speech":"clip.wav"}}`.
Every clip references the component's declared WAV list. The component binds
to one NPC name or body. A case-insensitive name match takes precedence over
body; speech matches exactly after trimming outer whitespace. Missing lines
stay silent. Labels, system messages, spells, private channels and the player's
own echo do not trigger NPC playback.

Mounting stages all components and validates bounded PCM WAV payloads before
applying anything. The existing format is PCM 22050 Hz mono 16-bit. Playback
uses in-memory bytes, the existing music/SFX fade layers and music ducking,
and a separate recorded-voice player with distance attenuation. Disabling the
option stops playback; scene unload stops streams and restores base music.
There is no ComfyUI requirement for these consumers.

Acceptance: `client.audio.packs` runs an installed synthetic pack through the
real Store loader. The smoke line checks independent layers, XYZ bounds,
name/body precedence, silent missing clips, default-off profile persistence,
stream format and malformed/undeclared refusal. Live UI and playback checks
are reported separately; a synthetic stream does not prove audible sound.
