# Water

GUO can draw the sea, rivers and lakes as moving water instead of the still
tiles of the original client. It is off by default: **Classic tiles** draws the
water exactly as ClassicUO does.

## Turn it on

- In game: **Options > Video > Water**, choose **Realtime (UO colours)**, then
  Apply. The change shows on the next frame.
- On a phone or in the modern Options: **Video > Water**.
- In the editor's UO World tab: **Layers > Realtime water**.

## What it does

The water keeps the tiles' own colours, so hued water, night and the seasons
look as they did. On top of that a slow swell crosses it, crests catch the
light, and surf breaks where the water meets the shore. It changes only how
water is coloured: what is drawn, in which order, and what a click picks are
the same as with Classic tiles.

ClassicUO's own **Animated water effect** option still works with Classic
tiles. A water style replaces it while the style is on.

## Coming next

Ripples behind the player and boats, more styles (pixel art, cel shaded,
realistic) as Store packs, and a setting a shard owner can use to choose which
styles their players may use. The decision behind all of it is ADR-0037 in
`docs/architecture/`.

## For testers

`--water-style uo` turns the style on for one run without saving it. The
scenario `client.render.water` visits the Britain docks and the Trinsic bay,
checks the draw list against ClassicUO's, and takes stills. The perf probe has
`britain-docks` and `trinsic-bay` scenes for frame time with the style off and
on.
