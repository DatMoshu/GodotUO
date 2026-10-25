# Release version tooling

`version.json` owns the client version and Android update code.

- `python tools/release/run.py check` verifies all platform fields.
- `python tools/release/run.py sync` updates those fields after a version edit.
- `python tools/release/run.py check --tag v0.1.0-alpha.1` also validates a tag.
- `python -m unittest discover -s tools/release` runs the regression tests.

The paired launcher is `launchers/dev/release_version.bat` (`.sh` on Unix).
Pass `check` or `sync`. See [the release process](../../docs/release/README.md).
