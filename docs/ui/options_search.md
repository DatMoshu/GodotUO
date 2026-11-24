# Searchable Options

Owner request, 2026-11-21. Classic Options (`OptionsGump`) has a search box
under its page buttons. Type a setting's name or a word for it (`music`,
`shadow`, `health bar`, `zoom`) and the right side lists every matching row,
with the page and section it lives on. Click a row: Options opens that page,
scrolls to the row and outlines it for a few seconds. Rows in the GUO detail
panels (Lighting and shadows, Weather and ambience, World map and water) are
listed too; clicking one opens that panel at the row. Clearing the box returns
to the page you were on.

- Code: `godot/GUO/src/Game/UI/Gumps/OptionsSearch.cs`, GUO-owned. The ported
  `OptionsGump` gains one `PORT DEVIATION (GUO)` line that attaches it.
- The index is read from the built controls (labels, checkboxes, buttons and
  section titles) each time the query changes, so new settings are found with
  no registration. A short synonym table maps everyday words (audio, hp,
  brightness, controller, ...) onto the words the rows use.
- No profile field, no packet, nothing saved. With the box empty, Options
  behaves exactly as before.
- Not yet in Modern (touch) Options.
- Checks: `GuoOptionsFlowProbe` "options search" step (index coverage,
  synonyms, ranking, page switching, panel results), run by `launchers\dev\smoke.bat`.
  Set `GUO_OPTIONS_CAPTURE` to a folder and run it without `--headless` for
  rendered captures.
