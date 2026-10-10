# tools/render_diff

Compares a ClassicUO render dump with a GUO one taken in the same place. Both
clients compile `src/Bootstrap/RenderDump.cs` (ClassicUO through
`tools/render_dump/inject.targets`); say `renderdump NAME` in game, or pass
`--dump NAME` to `launchers\dev\side_by_side.bat`. It reports objects on a
tile in one client only, the same object with different fields, and what was
drawn in one and not the other, and writes `build/render_dump/NAME/diff.md`.

## Run

```
launchers\dev\render_diff.bat NAME
launchers\dev\render_diff.bat --list
```

## Tests

No unit tests. The dump format is section-documented in `docs/data_formats.md`.
