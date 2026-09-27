# port_drift

Measures rule 2 ("port faithfully") instead of relying on memory: for every
`.cs` under `godot/GUO/src`, finds its upstream original under
`sources/ClassicUO/src` (same filename, longest common path suffix), and
diffs the two after CRLF/BOM/renamespace/shim-import normalisation.

```
python tools/port_drift/run.py [--strict] [--verbose]
launchers\dev\port_drift.bat
```

Reports files identical after normalisation, files changed with a
`PORT DEVIATION` / `PORT GAP` marker, files changed with **no** marker (the
ones rule 2 says to reconcile or mark), and new files with no upstream
counterpart. `--strict` exits 1 if any unmarked change exists, for a CI gate.

Baseline in this repo (2026-09-26, work/render): 7 unmarked, all outside the
renderer -- `Configuration/ConfigurationResolver.cs`, `Configuration/Profile.cs`,
`Game/GameObjects/RenderedText.cs`, `Game/UI/Gumps/MiniMapGump.cs`,
`Game/UI/Gumps/OptionsGump.cs`, `Network/Socket/TcpSocketWrapper.cs`,
`Network/Socket/WebSocketWrapper.cs`. `--strict` fails until their owners mark
or reconcile them; a change that raises the count is the one to look at.

Matching is by filename and the longest common path suffix, so a tie picks
arbitrarily: `Render/Gumps/Gump.cs` can be diffed against the UI `Gump.cs`
rather than `ClassicUO.Renderer/Gumps/Gump.cs`. Check a surprising diff by hand.
