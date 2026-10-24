# ADR: UX-03 touch inventory

Status: UX-03 direction authorized by the owner's 2026-10-22 sprint handoff;
implementation pending director review.

## Decision

L1 UO+ presentation of the paperdoll and backpack. `Profile.TouchUi` is nullable:
Automatic (`null`) follows the primary touch pointer, On (`true`) enables the
layout explicitly, Off (`false`) retains the existing inventory presentation
and independent Grid containers preference. Automatic is off on mouse-only
desktop startup. UO+ exposes all three choices; reopen inventory after Apply.
This is the handoff's explicit exception to default-off on touch devices.

The paperdoll keeps the existing `PaperDollInteractable`, equipment slots and
equip/drop handlers. A separate partial file supplies a 392x396 layout with
44x44 equipment targets and 44px-high actions. Secondary actions are under
More/Back. The retained native art uses the existing nearest-sampled batcher.
Unlike ADR-0024's Godot-based Modern views, this layout stays in the legacy UI
tree so item dragging, selection, packet updates, saved window identity and
the dual-screen shelf share the existing paths. The desktop Classic build
branch is unchanged when the gate is off.

Touch UI selects the existing grid backpack independently of its older opt-in
setting. It clamps slots to at least 44 client pixels and enlarges grid chrome.
Movement starts an item drag; a stationary long press asks the shard for the
item's context menu without picking it up. A shard may provide no context
entries. Pinch uses the existing presentation transform, including inverse
hit testing, without changing the server's item coordinates.

On portrait single-screen layouts the paperdoll starts near the top and grids
start below it. Pinch clamping reserves that space. Landscape and the second
display retain their existing placement, with the touch paperdoll header kept
below the top bar and narrow landscape grids limited to five columns beside it.
Closing the paperdoll releases the
portrait reserve. No new service, package or external dependency is required.

Real screen-touch events on hybrid desktops enable the existing touch
translator without mouse emulation. A real physical mouse can take over again;
touch-generated mouse events stay suppressed to prevent duplicate clicks.

## Validation

`client.ui.inventory_touch` drives a real dev-shard character through synthetic
finger events: opening backpack, equipment target geometry, stationary context
request, unequip/equip round trip acknowledged by the server, pinch scaling,
and portrait clearance. It captures the running viewport. Run at 640x480,
1920x1080 and 1080x2340; the last is a desktop portrait simulation, not physical
phone proof. Existing `client.input.touch`, touch-gesture checks and
`--presentation-parity` remain required. Shared UO+ smoke checks all three saved
Touch UI states. Physical Fold/Odin/Thor checks remain a MobileDeveloper handoff.

## Dependencies

ADR-0017 (touch translator and integer scale), ADR-0024 (Classic/Modern choice),
ADR-uoplus-settings (shared options), `docs/ui/grid_container.md`, and
`docs/data_formats.md` section "Touch inventory (UX-03)".
