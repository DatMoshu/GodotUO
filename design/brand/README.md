# GUO brand

The GUO sigil: an ornate gold-and-silver mark, 1293 x 1216 RGBA on a
transparent background. It is the master every app icon is built from --
the window and taskbar icon, the Windows executable icon, the boot splash
and the Android launcher set -- by `python tools\brand\run.py`
(`launchers\dev\brand_icons.bat`). Change the sigil here, rerun the tool,
commit what it wrote.

The owner selected the cleaned UO emblem on 2026-10-28. The master has
transparent outer and inner spaces and retains the gold shine and silver
knotwork. Both the static boot splash and animated intro use this artwork;
the intro retains its gold light sweep. This replacement was made
with built-in image generation from the owner's supplied logo, followed by
a transparent-background extraction. Concepts are not launch assets.

On 2026-10-28 the owner selected the letter-settle splash as the default.
`SplashIntro.cs` reproduces take 04 natively: original UO emblem, staggered
GODOT letters below, then a crossfade into login (about six seconds plus
loading). Reduced motion uses a short plain fade; input still skips it.
`godot/GUO/assets/brand/godot_letters.png` contains five 82 x 100 glyph cells,
rendered from Bahnschrift at 68 px in ivory (238, 233, 220), matching the
approved video. The font software is not bundled. The letters use smooth
sampling only within the splash; native game art is unchanged.

It is not UO art and it is not pixel art: the tool resamples it with
Lanczos, and rule 7 in AGENTS.md (never filter pixel art) does not apply.
