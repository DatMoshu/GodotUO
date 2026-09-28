# GUO's own Godot UI in the UO style

Any Godot UI GUO adds (a card, a menu, a first-run screen) should look like it
came with the 1997 client. This page is the one description of that look. The
code is `src/Input/Touch/UoTheme.cs`; use it rather than restating the rules.

It applies to the window menu, the companion tabs, the command bar's slot
editor and hold popup, and anything new. It also applies to other sessions'
Godot screens, such as GUOWeb's effects menu and GUOeditor's first-run screen.
It does not apply to ported gumps: they already are the client.

## Principles

1. **Only the client's own art.** Every frame, button, box and knob is a gump
   from the player's install, drawn by UoTheme. There are no rounded rectangles,
   soft shadows or gradients. The client never had them.
2. **Whole pixels.** A card is built in art pixels and scaled by a whole number
   (`UoTheme.PixelScale`), sampled nearest-neighbour (rule 7). One art pixel is
   always a square of device pixels, and nothing is smoothed.
3. **The client's font.** Text is unicode font 1, the one ClassicUO draws its
   UI in, as a Godot bitmap font with the client's own advances. It is drawn at
   1x of the card for body text and 2x for titles, never at a fractional size.
4. **Quiet.** Stone, parchment and ink. Colour means state: gold for lit, red
   for danger, notoriety hues for names. Nothing is coloured for decoration.

## Palette

| Name | Hex | Use |
|---|---|---|
| Ink | `#1c1812` | Text on stone and parchment |
| Muted | `#4e463c` | Secondary text, hints |
| Heading | `#5a3a0c` | Titles, a selected tab or choice, the primary action's caption |
| Gold | `#e0b050` | Lit captions on the dark band (the command bar) |
| Cream | `#eeeade` | Text on the dark band and on coloured bars |
| Danger | `#8c1c12` | A destructive action's caption |
| Band | black at 55% | Behind the command bar's rows and the target header |

The target header's names use the notoriety colours: innocent `#3c8cf0`, ally
`#3cc83c`, grey/criminal `#a0a0a0`, enemy `#f0961e`, murderer `#e6281e`,
invulnerable `#f0e61e`. Hits, mana and stamina are `#b8483e`, `#3f6fc4` and
`#c9a23b`.

## Frames

| Frame | Gumps | For |
|---|---|---|
| Stone | 0x13BE–0x13C6 (12 px border) | A card: the window menu, the slot editor, the companion panel |
| Parchment | 0x0BB8–0x0BC0 (4 px border) | Fields, lists, the journal, the character sheet |
| Dark stone | 0x2436–0x243E | A dark panel, when a card sits on the dark band |

A frame is a resizepic: four corners, four edges and a centre, with the edges
and the centre tiled, never stretched (`UoTheme.Frame`).

## Controls and their states

| Control | Art | Normal | Pressed / held | Selected | Disabled |
|---|---|---|---|---|---|
| Button | The marble plate 0x098D, 23 art px tall, ends kept, middle tiled | Ink caption | Plate at 70%, Heading caption | Plate at 86%, Heading caption | Plate at 55%, ink at half alpha |
| Check box | 0x00D2 / 0x00D3 | Box, ink label | – | Ticked | – |
| Slider | Bar 0x00D5–0x00D7, knob 0x00D8 | | | | |
| Scrollbar | Track 0x0100, thumb 0x00FE, no arrows (a finger drags the list) | | | | |
| Field | Parchment frame, ink text, a gold selection | | | | |
| Rule | One art pixel of `#5c554a` | | | | |

A button is never taller than its plate (`SizeFlags.ShrinkCenter`), because the
plate's rounded ends do not stretch. For a target larger than 23 art px, leave
space around the plate and hit-test the space as well, as the command bar does
with its cells.

The command bar and its popup draw their plates directly, not through a Godot
theme. They follow the same states: held means one art pixel down, 70% and a
gold caption; lit means a gold caption for 140 ms.

## Type scale

| Use | Size |
|---|---|
| Body, labels, buttons | Font 1 at 1x of the card (16 px nominal, cap height 10 art px) |
| Card titles, the size readout | 2x |
| The command bar's captions | 2x, on plates at 3x (the one mixed scale; see `command_bar.md`) |

## Sizes

- **Scale.** `UoTheme.PixelScale` comes from the screen's density: about 3 on
  the Thor and the Odin 2 Mini, 1 on a desktop monitor. A plate is then about
  5 mm tall. A card on the second screen, where it holds running text, uses one
  step less.
- **Override.** `GUO_UI_SCALE=N` sets the scale for a desktop run.
- **Spacing.** Spacing is 4 art px between controls, 5–6 between groups, and 12
  inside a stone frame.

## Building a card

1. Build it only once `UoTheme.Ready` is true, because the gumps must be loaded.
   A theme built earlier is a flat stand-in and is not kept.
2. Put the controls in a `SubViewport` with
   `CanvasItemDefaultTextureFilter = Nearest`. Set the root control's `Scale`
   to `UoTheme.PixelScale` and `Theme = UoTheme.Theme`.
3. Show it with a `TextureRect` (filter Nearest) on a `CanvasLayer`, or on the
   second screen through `DualScreen.Draw`.
4. Keep art pixels and client pixels apart. `Client.Game.DpiScale` is 1 on the
   Thor, so it is not the card's scale.
5. GameController marks every event handled first. Push pointer events into
   the viewport yourself, as `WindowMenu.HandleInput` and
   `BarEditor.HandleInput` do: a tap is a press and a release, and a drag over
   a list scrolls it by hand.

## Words

Captions are short, in sentence case, and say what happens: "Save",
"Move to top screen", "Reset size". Use the client's clilocs where it has the
word. A string that is not localised yet goes on `unlocalized_strings.md`.
