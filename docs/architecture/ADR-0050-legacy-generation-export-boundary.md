# ADR-0050: Keep legacy Comfy generation out of exported clients

Status: Accepted for local implementation by the acting project lead, 2026-11-13,
under the owner-approved L3/export policy. Release validation remains separate.

## Context

The Windows release qualification found that `repaint`, `splat` and
`audiozones`/`musiczone`/`sfxzone` reached unguarded Comfy HTTP generation.
The export also carried three Comfy workflow resources. Checking only
`GUO.Editor` and `GUO.Labs` namespaces missed `GUO.Comfy` and runtime gumps.
Opening the exact candidate's repaint panel confirmed normal runtime exposure;
no generation or service request was needed for that observation.

Generation belongs to L3, behind `TOOLS` and `AiFeatures`; clients consume
prepared content. ADR-0036's proposed loose-file migration allowance preserves
manual authoring, but supplies no exception for exported generation transport.
This decision enforces the existing layer policy without moving manual work or
adding a client feature, option, service or rendering mode.

## Decision

- Retire the generation-only runtime repaint command and gump. This legacy
  workflow is not promised as a replacement editor workflow by this change.
  Existing override art continues to load.
- Remove generation controls, tasks and result application from the mixed
  splat and audio-zone gumps in every configuration. Retain staged-model
  selection, local native-art preview, targeting, placement, restore, skin rules,
  manifest saves, zone editing, playlists and prepared-content consumption.
- Compile the Comfy transport only with `TOOLS`, for existing editor callers.
  The editor Regions audio path retains its `AiFeatures` checks and linked
  cancellation lifetime. Manual tools do not require AI to be enabled.
- Exclude `assets/comfy/*` from every platform's export preset. This is a
  resource boundary in addition to the C# compile boundary.

## Acceptance and limits

Build Debug and ExportRelease. Inspect the actual exported assembly and PCK:
no Comfy transport type, repaint gump, mixed-gump generation methods or Comfy
workflow resources. Run native manual splat/zone input, save/reopen and pack
consumption checks, plus editor AI-off/cancellation and the full smoke launcher.
Use an independent review and native repeat before candidate integration.

The previous archive remains immutable. Any changed candidate gets a new source
and archive identity. This is not a whole-program audit: voice services, scripting
and other authoring paths need their own bounded reviews. Other-platform preset
checks do not establish native acceptance on those targets. Existing editor hot
reload failure remains a separate readiness blocker.
