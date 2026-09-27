# Gump size and screen controls

The touch layer supports independent size controls for paperdolls, classic and grid containers, status windows, and classic/resizable journals. Other gumps retain their existing behavior. This is the floating-gump implementation; companion tabs are still a separate proposal.

## Pinch

- Put two fingers inside the same supported gump, then spread or bring them together to change its size. The first finger must still be a pending touch, rather than an already committed item drag, walk, or double click.
- The gesture stays with that gump until release. Lifting either finger ends resizing; the remaining finger cannot click or drag accidentally.
- Two fingers beginning on exposed world still zoom the world. A World Map pinch uses the map's own zoom handler. The small fixed minimap has no new content zoom.
- Fingers on different displays, mixed world/gump contacts, locked windows, and unsupported windows do not resize or zoom the world.
- Scale ranges from 75% to 300%, limited by available display space where possible. Sampling stays nearest-neighbour. Fractional sizes can have uneven pixel widths; use 100% or 200% when crisp integer pixels matter most.

## UI handle

With two screens active, drag a gump to the top edge of the bottom screen to send it up, or to the bottom edge of the top screen to send it down. Left/right movement stays on the current screen. Transfers preserve horizontal position where space allows, and manual dragging overrides automatic shelving.

A small **UI** handle appears above eligible windows in touch or dual-screen mode. Near the top edge it moves beside the window so the top bar cannot cover it. Previously scaled/locked windows also retain the handle with ordinary mouse input.

Tap/click it for:

- **Smaller / Larger:** change size in 25 percentage-point steps.
- **Reset size:** restore 100% and unlock pinch.
- **Lock / Unlock pinch size:** prevent accidental pinch changes. The explicit size buttons remain available.
- **Send to main / second screen:** transfer the existing window when the second-screen shelf is active. The world viewport is not transferred. Manual placement overrides automatic shelving, and return positions are remembered for the current window instance.
- **Done:** close the controls.

The saved gump layout includes its current scale, pinch lock, and manual-placement flag. This applies to gumps that already participate in profile layout saving; it is not a new persistence mechanism for every transient window. Closing/recreating a gump follows that gump's existing restoration lifecycle.

## Implementation boundaries

GumpPresentation adds a render transform around the existing draw queue, including clipping, and inverses pointer/event coordinates for legacy controls. Scoped pointer coordinates cover controls that read Mouse directly. Server gump layouts, reply IDs, world movement, and the existing global container scale are unchanged.

Scaling is intentionally limited to the supported client-owned types. Arbitrary shard dialogs, spellbooks, anchored macro-button groups, and maps' window sizes are not given a generic scale transform yet. The UI handle is a mouse/touch control; native controller navigation is not introduced by this change.

## Verification

Validated on September 27, 2026: .NET build succeeded with zero errors (seven existing warnings); touch probe passed 44/44 checks, including identical server container-drop coordinates at 100% and 150%; batcher probe passed 253 checks, including three new transform/clipping checks. The desktop dual-screen probe passed transfer, remembered-position, inverse-coordinate, and mixed-display gesture checks.

Use `launchers/dev/touch_probe.bat` against the local dev shard. Its added checks cover gump pinch ownership, inverse hit testing, pointer coordinates, raw/native event deduplication, lock/reset, cancelled gestures, saved scale, and item-drop parity. The screenshot `build/screenshots/gump_scale_150.png` captures a scaled paperdoll for visual inspection.

Use `launchers/game/play.bat --dual-screen 1240x1080 --dual-probe` for the desktop second-display simulator. It additionally checks transfer in both directions, remembered position, prevention of automatic re-shelving, and mixed-display gesture rejection. The simulator does not establish physical Android gesture delivery or controller focus behavior; those still need an on-device pass.

See [the research and design rationale](../second-screen-ui-research.md).
