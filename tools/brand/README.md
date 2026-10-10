# tools/brand

Builds every app icon and the splash GUO ships from the brand sigil
(`design/brand/guo-sigil.png`), so no build on any platform shows the engine's
logo: `icon.png`, `icon.ico`, `splash.png`, the Android launcher icons and the
intro's sigil. It refuses a master with an opaque corner. `unkey.py` was the
one-off that turned the delivered sigil's painted checkerboard into real
transparency; the clean master is what is committed. The module docstring of
`run.py` lists every file written and where it is used.

## Run

```
python tools\brand\run.py                 build them all
python tools\brand\run.py --sheet <png>   also write a contact sheet
launchers\dev\brand_icons.bat             the same, from a launcher
```

## Tests

No automated tests. `launchers\windows\export.bat` checks the exported executable carries the icon.
