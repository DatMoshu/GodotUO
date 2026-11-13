# ADR-0051: Retire legacy runtime voice synthesis

Status: Accepted for local implementation by the acting project lead, 2026-11-13,
under the existing owner-approved L3/export policy. Validation is separate.

## Context and decision

After the bounded Comfy review (ADR-0050), source tracing found another runtime
generation path: the `voice` gump's Design/Test actions enqueue work that launches
Python `voice.py` for voice design or synthesis. This is outside L1's allowed
dependencies and lacks the editor `AiFeatures` and cancellation boundary.
Legacy comments describing automatic speech are not evidence of a live caller:
no current production callers of SpeakOwn/SpeakMob were found.

Retire the legacy runtime synthesis/design queue, worker pump, Python launcher
and corresponding gump actions in every configuration. Keep loose-file profile
enumeration, manual mobile mappings/default saves and the existing attenuation
function unchanged. These retained mappings are migration metadata, not the
independent recorded-pack reader. Requested legacy synthesis diagnostics fail
explicitly without starting processes; they must never silently pass.

Prepared `PackVoice` playback, typed pack lookup, its default-off option and
silent missing-line behavior remain unchanged. This change does not add an editor
replacement or promise legacy voice mappings drive recorded-pack playback.

## Acceptance and scope

Build Debug and ExportRelease; run full smoke. Inspect the actual exported
assembly for absence of legacy synthesis/design/launcher methods and nested
worker types, with an old-artifact negative control. Test manual profile mapping
and default save/reopen in isolated fixtures; exercise prepared voice playback
through the existing client.audio.packs scenario. Independently review and repeat
native acceptance before integration. No actual synthesis or service call is
required for this retirement.

This decision covers the legacy voice path only. It does not certify all runtime
authoring, scripting, plugins, dynamic process behavior or other target platforms.
Keep previous source/export/archive artifacts immutable and bind any new package
to its exact source and runtime identities.
