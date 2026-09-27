# Grid container gump — assessment (GUO addition)

Status: **proposed, not built.** Owner: GUO-UI (branch `work/ui`). Written
2026-09-26 for the coordinator (GUO-Fable) before any code.

## What is asked

A grid-slot view of a container, the backpack first, as the default on
mobile: square slots in rows, one item per slot, big enough for a finger.
The model is TazUO's grid container. ClassicUO has nothing like it.

## What upstream ClassicUO already has

| Upstream | What it does | Grid backpack? |
|---|---|---|
| `Profile.GridLootType` (0 none, 1 grid only, 2 both) | Corpses only. With 1 or 2, `PacketHandlers.OpenContainer` records `_requestedGridLoot`; the contents packet then opens `GridLootGump`. | No |
| `Game/UI/Gumps/GridLootGump.cs` (591 lines) | A paged grid of `GridLootItem` slots (50 px, amount slider, tooltip, click to loot to the grab bag), "Set loot bag", prev/next pages, and the corpse name. | No; it is a looting UI, and a click moves the item *out* |
| `ContainerGump.cs` (765 lines) | The classic container: gump art with items at the server's x,y. `UseLargeContainerGumps` swaps the art for nine chest/crate/box graphics; the backpack (0x3C) has no large variant. `ContainersScale` (50–200 %) scales the gump, and optionally the items. | This is what a player gets today |

**Shipped in the first pass** (`PlatformDefaults`, mobile table): `GridLootType = 2`
(corpses open both views), `UseLargeContainerGumps`, `ContainersScale = 130`,
and `ScaleItemsInsideContainers`. On the Thor that makes the backpack 299x265
client px, and chests use the large art. Nothing new is built for this; every
value is an Options setting upstream already has.

## The addition: `GridContainerGump`

A new gump. It is not a change to `ContainerGump`, which stays and is one
tap away.

**Behaviour**

- Opened in place of `ContainerGump` for ordinary containers when the new
  profile option `GridContainers` is on (default: off on desktop, **on on
  mobile** via `PlatformDefaults`). It never opens for corpses (those keep
  `GridLootGump`), spellbooks, vendor/buy gumps, the bank's special cases, or
  secure trade.
- Slots: 44 client px by default (88 physical px at screen scale 2, about
  6 mm on a 369 dpi panel), in 6 columns. The row count follows the item
  count, and the grid scrolls vertically past the height the screen allows.
  Each slot shows the item art centred and hued, the amount bottom-right,
  the tooltip on hold, and a selection outline.
- Header row: title, a filter field (raises the soft keyboard, work/android),
  a sort toggle (none / name / graphic), and a button that switches to the
  classic view (reopens the same container as `ContainerGump`, and remembers
  the choice per container serial for the session).
- Interaction reuses upstream's paths, and there is no new network code:
  double-tap uses `GameActions.DoubleClick`; drag out uses
  `GameActions.PickUp`, the same as ContainerGump's `ItemGump`; dropping
  onto the grid uses `GameActions.DropItem(serial, 0xFFFF, 0xFFFF, 0,
  container)`, which lets the server place it; dropping onto a slot holding
  a container drops into that container. Long-press is the touch layer's
  right-click; on a slot it opens upstream's context menu for the item.
- Order is client-side only. The server keeps its own x,y, and the grid
  never writes them back. A slot order stored per container (TazUO's
  "locked slots") is out of scope for the first build.

**Upstream files it would touch** (each hunk marked `// PORT DEVIATION (GUO):`)

| File | Change | Size |
|---|---|---|
| `Network/PacketHandlers.cs` `OpenContainer` | one branch before `new ContainerGump(...)`: when the option is on and the container qualifies, add `GridContainerGump` instead | ~10 lines |
| `Network/PacketHandlers.cs` (contained-item updates, ~1071, ~1826, ~6127) and `Game/World.cs` (~283, ~499) | beside each `GetGump<ContainerGump>(serial)?.RequestUpdateContents()`, the same call for `GridContainerGump` | 5 one-line hunks |
| `Game/GameObjects/Item.cs` (~190) | dispose the grid gump where the container gump is disposed | 1 line |
| `Game/GameActions.cs` `OpenBackpack` (~242), `MacroManager.cs` (~852) | "is the backpack already open" also checks the grid gump | 2 small hunks |
| `Configuration/Profile.cs` | `GridContainers`, `GridContainerSlotSize` (and, optionally, a restore entry for gumps.xml `GumpType`) | 2–3 properties |
| `Game/UI/Gumps/OptionsGump.cs` | a checkbox and a slot-size slider next to the existing container options | ~20 lines |
| `Configuration/PlatformDefaults.cs` (GUO's own) | mobile entry `GridContainers = true`; bump `CurrentVersion` to 2 | 2 lines |

New GUO-only code: `Game/UI/Gumps/GridContainerGump.cs` (~400 lines),
built from upstream controls (`AlphaBlendControl`, `HitBox`, `StbTextBox`,
`NiceButton`, `ScrollArea`) and the same item-art draw call `GridLootItem`
uses. `GridLootItem` is `private` to `GridLootGump`, so the slot control is
written anew rather than shared; making it `internal` would be a gratuitous
upstream edit.

A narrower alternative is a subclass of `ContainerGump` that only re-lays
out its `ItemGump` children in a grid. It is fewer upstream hunks: every
`GetGump<ContainerGump>` call site keeps working unchanged. It still has to
override the art, the hit boxes and scrolling, and `ContainerGump` was not
built to be subclassed (private fields, a `sealed`-in-spirit constructor
path). It is probably a smaller diff and a more fragile gump. If the
coordinator prefers the fewest upstream edits, choose this.

**Risks**

- `ContainerGump` restores itself from gumps.xml on login. A grid gump that
  does not restore reopens as classic after relog. That is acceptable for a
  first build.
- Drag and drop is the part most exposed to the touch layer (work/android).
  Verify it with `--touch-probe`-style synthetic drags before any device
  run.
- Shards that rely on the container art (quest containers, custom gumps)
  lose it in the grid. That is why the classic view is one tap away.

**Estimate:** 1–2 sessions. Verification: the desktop `--ui-probe` extended
to open the grid, then the Thor.

## Mockup

Requested from Codex in `codex_message.md` msg-005: wireframes only, no UO
art, at 960x540 client px. Frames: open over the world, long-press menu,
drag to the paperdoll. The paths go here when they land.
