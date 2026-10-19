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
light, and surf breaks where the water meets the shore. Anything in the
water moves it: a creature or a wading character sends out rings, a boat
under way leaves a bow wave and a wake, and something that lands in the
water splashes. It changes only how
water is coloured: what is drawn, in which order, and what a click picks are
the same as with Classic tiles.

ClassicUO's own **Animated water effect** option still works with Classic
tiles. A water style replaces it while the style is on.

## More styles from the Store

Four more styles come as Store packs:

- **Pixel water** (`guo-water-pixel`): chunky dithered water with whole-pixel
  surf and glints, like a game from the early nineties.
- **Cel water** (`guo-water-cel`): flat cartoon colour, bold bands of light,
  white surf with a dark outline, ripples drawn as rings.
- **Realistic water** (`guo-water-realistic`): smooth light, deeper colour out
  at sea, a sharp sun highlight with sparkles and soft surf. In the web build
  it is a little plainer (no fine chop or sparkles), to keep it fast.
- **UO+** (`guo-water-classicplus`): the native water art and its fine dither,
  with a quiet shimmer and small shore lap. It keeps the original colours
  instead of adding white foam or broad highlights.

Surf follows a continuous shore contour in all styles, with no surf below
high cliffs.

Install one from the Store, select a deployment that contains it in the
Store's **Configure deployment** window, restart, then pick it under
**Options > Video > Water**. A pack style is only numbers for one of the
looks built into GUO: it carries no code. If its pack is removed, the water
goes back to Classic tiles.

## Coming next

A setting a shard owner can use to choose which styles their players may use.
The decision behind all of it is ADR-0037 in `docs/architecture/`.

## For testers

`--water-style uo` turns the style on for one run without saving it. The
scenario `client.render.water` visits the Britain docks and the Trinsic bay,
checks the draw list against ClassicUO's, and takes stills. The perf probe has
`britain-docks` and `trinsic-bay` scenes for frame time with the style off and
on. For the pack styles, `python tools/asset_store/water_packs.py --deploy DIR`
installs all four and prints what to set for the scenario
`client.render.water_pack`, which is run once per style.

The pack scenario also checks a low sandy beach and holds the east docks
shore and bay cameras for motion review. Run it with `--var style=classic` for the native
comparison. The synthetic `res://src/Render/Water/WaterShoreProbe.tscn`
checks the diagonal coast contour, falloff, dry-art overlaps and cliffs with
rendered pixels; run it using `godot-console --path godot/GUO
--rendering-method forward_plus res://src/Render/Water/WaterShoreProbe.tscn`
and again with `gl_compatibility`. It needs a real renderer, without `--headless`.
