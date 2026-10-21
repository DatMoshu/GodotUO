# ADR: Shared UO+ settings and flags

Status: Proposed for owner review, 2026-10-21. Implements the owner's
post-merge assignment to unify the night-push effects. No merge authorized.

## Context

Particles, Labs weather and water had separate controls and lifetimes.
The owner requested one surface for weather, sway, particles, spell lights
and water ripples, each default off, with individual density controls.

## Decision

One native **Video → UO+ effects...** panel opens from classic and Modern
Options. It uses the existing checkbox, slider, scroll area and footer controls,
including touch scrolling and the generic controller navigator. Cancel closes
without applying. Defaults stages all effects off, densities 50 and the ambient
cap 128; Apply saves the staged values, and Okay applies then closes.
The parent Options page never writes a stale copy of these settings.

`UoPlusOptions` is the common catalog and flag/density API. Fog, leaves and dust
retain their existing boolean fields as their sole serialized flag authority;
spell lighting likewise retains `SpellLighting` from the spell-light lane.
Old particle profiles retain their global density until individual values are saved.
Other flags use `UoPlusFlags`. `UoPlusDensities` stores 0–100 per feature.
All new flags default false; density 0 suppresses work without clearing the
checkbox. Unknown flags are ignored. Missing/null dictionaries are safe.
Unavailable entries cannot activate, including hand-edited profile flags.

L1 consumers are ambient particles, temporary spell-light collection and
water ripples. The spell-light hook additionally requires Realtime lights;
the existing spell-light producer consumes the same gate. Density scales its
spell-only admission budget from 0 to 16 (ceiling of 16 × density / 100), with
immediate trimming of older spell bursts when lowered. Generic temporary lights
and the 100-source total cap are preserved. Water requires a
non-Classic style; density scales disturbance strength. Turning ripples off
resets the field, writes zero ripple strength to the existing shader uniform
and skips object scans and simulation, preserving the selected water style.
No shader, native art, packets, picking or upstream weather behavior changes.

L4 weather stays behind `GUO_LABS` in Debug builds. Its water rings, puddles
and wet-player flags/densities use the same profile API; release/export code
cannot enable them. Ring density thins impacts, puddle density selects stable
ground cells and thins impacts, wet-player density scales the body-drop budget.
Puddle ground IDs and the cobblestone selector remain session-local experiments;
invalid IDs reject the entire staged Apply. The old Labs command opens this
same panel, retaining its explicit proof subcommand. No second weather window.

Sway has no renderer on main. Its catalog slot is visibly unavailable and
cannot activate. A future renderer can use its reserved flag and density; this
surface must not claim an absent effect works.

## Consequences

The settings surface is L1; it does not promote Labs weather to L1. There is
no master flag that silently enables effects. The panel shows unavailable
weather in normal builds and explains prerequisites next to each option.
No external dependency, service, shell call or downloadable content is added.
Profiles that explicitly enabled particles keep that choice; this is not a
reset of existing preferences. Water ripples now require explicit opt-in even
when a non-Classic water style was previously selected, as the owner requested.

## Validation

Smoke runs `UoPlusProbe.tscn` without client data: old/new defaults, legacy
particle migration, independent JSON flag/density round trips, clamp/zero
behavior, unavailable features, water prerequisites and temporary-light
density. Existing particle, water and weather contracts remain applicable.
`client.uoplus.options` runs the native scratch-client panel proof: staged
edits, Apply, Defaults, Cancel, controller focus and scrolling to the last
option, plus screenshots. It does not save or log into a player profile.
The existing spell-event contract checks cast/impact/heal lighting and cleanup;
live-world appearance and absent sway rendering are outside this PR's proof.

## ADR Dependencies

ADR-0001 (sorting and picking), ADR-0027 (L1 default-off options), ADR-0037
(water modes), ADR-0039 (temporary lights), ADR-0042 (ambient particles),
ADR-uoplus-weather-lab (L4 weather boundaries).

## Engine Compatibility

Pinned Godot 4.7.2 mono. Existing native gump and shader-uniform APIs only;
no filtering changes or new engine dependency. Native pixels stay nearest.

## GDD Requirements Addressed

Owner's 2026-10-21 UO+ toggles-surface assignment (#12070); no registered GDD.
