## What this changes

<!-- One subsystem or one fix. What and why. -->

## Kind of change

- [ ] Ported upstream files (tier: verbatim / shim / rewrite)
- [ ] Renderer, input or audio (rewrite tier)
- [ ] Parity fix — behaves more like ClassicUO
- [ ] Bug fix
- [ ] Tooling, launchers or CI
- [ ] Docs

## Evidence

<!-- "Ported" is not "working". Say which of these you actually have. -->

- [ ] `dotnet build godot\GUO\GUO.csproj` passes
- [ ] `launchers\dev\smoke.bat` passes against a real install
- [ ] Screenshot / parity diff attached (for anything visible)
- [ ] Played it on a shard (which one?)

## Checklist

- [ ] Nothing under `sources/` edited
- [ ] Ported files kept faithful; unavoidable changes marked `PORT DEVIATION (GUO)`
- [ ] `docs/port_status.md` regenerated if porting progress changed
- [ ] No game data, captures from game data, or personal paths committed
