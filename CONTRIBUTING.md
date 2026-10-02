# Contributing to GUO

GUO ports the ClassicUO client to Godot 4 .NET. Contributions are welcome:
ported files, renderer and input work, parity fixes, tooling, and bug reports
with a clear reproduction.

Read [the port plan](docs/port_plan.md) first. It explains why the project
is structured the way it is.

Questions and ideas go to [Discussions](https://github.com/DatMoshu/GodotUO/discussions);
bugs go to Issues. Look for `good first issue` and `help wanted` labels for a
way in. The [wiki](https://github.com/DatMoshu/GodotUO/wiki) is generated from
`docs/wiki/`, so a wiki fix is a pull request against those files. AI coding
agents work under the same rules; see `AGENTS.md`.

## Setting up

```bat
launchers\pipeline\00_bootstrap.bat          REM engine + upstream reference
if not exist launchers\_shared\config.local.bat copy launchers\_shared\config.local.bat.example launchers\_shared\config.local.bat
notepad launchers\_shared\config.local.bat   REM point UO_CLIENT_DATA at your install
launchers\dev\smoke.bat                      REM must pass before you open a PR
```

You need your own Ultima Online Classic install. Your paths go in
`config.local.bat`, which is gitignored; do not put them in `config.bat`.
Use the pinned Godot .NET build and a .NET SDK compatible with
`godot/GUO/GUO.csproj`. Keep an existing local configuration when updating.
The [data formats contract](docs/data_formats.md) describes shared settings
and file formats; architecture decisions live in [the ADR index](docs/architecture/README.md).

## The rules a PR is checked against

These come from `AGENTS.md` and apply to every change.

1. **Never edit `sources/`.** It is the upstream reference, read only.
2. **Port faithfully.** No reformatting, renaming, modernising or
   opportunistic bug fixing in ported files. Every gratuitous edit has to be
   reconciled by hand on every upstream merge. Where a change is unavoidable,
   mark it `PORT DEVIATION (GUO)` with the reason.
3. **Parity before improvement.** Make it behave like ClassicUO first. Propose
   improvements separately, in their own PR.
4. **Keep `GUO.Compat` to value types.** If a shim file wants a texture or a
   device, it is misclassified; fix the tier, do not grow Compat.
5. **Measure, do not estimate.** Progress claims come from
   `launchers\pipeline\03_port_audit.bat`. "Ported" is not "working": say
   whether you have a smoke run, a screenshot or a parity diff.
6. **Never filter pixel art.** Nearest-neighbour sampling everywhere.
7. **Never commit game data or credentials.** No `.mul`, `.uop`, `.idx`,
   `.def` or `.enu` files, and no captures rendered from them. CI rejects them.

## Licences

- Code in this repository is **BSD 2-Clause** (see `LICENSE`). By opening a PR
  you agree your contribution is licensed the same way.
- Ported ClassicUO files keep their upstream `SPDX-License-Identifier` header.
  New C# files carry `// SPDX-License-Identifier: BSD-2-Clause` as their
  first line too.
- `tools/modernuo/patches/` modify ModernUO and are **GPL-3.0**.

## Pull requests

- Small batches. One subsystem or one fix per PR builds and reviews cleanly;
  fifty files at once do not.
- `dotnet build godot\GUO\GUO.csproj` and `launchers\dev\smoke.bat` pass.
- If you touched porting progress, re-run the audit and commit the updated
  `docs/port_status.md`. CI checks that it is current.
- Visual changes: attach a screenshot, ideally next to ClassicUO's.
- Use `launchers\dev\screenshot.bat` for scripted captures without taking
  desktop focus. Keep generated evidence under `build/`; attach reviewed
  screenshots to the PR instead of committing them.
- Run `python tools/privacy_scan/run.py --staged` before committing. Remove
  private addresses, account names, tokens, personal paths and device IDs
  from logs and images as well as code. Never attach `config.local.bat`.
- Record the commands actually run and their outcomes. Mark unavailable
  checks with a reason; distinguish inherited warnings from new failures.
- Commit messages: a plain imperative summary of what changed, as in the
  existing history.

## Reporting bugs

Use the issue templates. Include your client version (`UO_CLIENT_VERSION`),
whether you used a local or public shard, and what ClassicUO does in the same place. A
difference from ClassicUO is a bug; a difference from the original client is
worth an issue too, but say which one you compared against.

For Store problems, include the pack ID/version, action and error text.
Prefer a minimal synthetic pack with clearly licensed content; do not attach
client archives or private store URLs. For visual problems include display
size, scale and whether another screen was active.

Security issues: see [SECURITY.md](SECURITY.md). Do not open a public issue.

## Asset contributions

Include the creator, licence, attribution and provenance of contributed
media. The Store licence allowlist and pack validation rules are in the
[data formats contract](docs/data_formats.md). A licence label alone is not
permission to redistribute someone else's work. Keep game-install data,
executable code and credentials out of packs. Pixel art uses nearest-neighbour
sampling throughout the web catalogue and client.
