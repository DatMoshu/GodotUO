# Contributing to GUO

GUO ports the ClassicUO client to Godot 4 .NET. Contributions are welcome:
ported files, renderer and input work, parity fixes, tooling, and bug reports
with a clear reproduction.

Read `docs/port_plan.md` first. It is short, and it explains why the project
is structured the way it is.

## Setting up

```bat
launchers\pipeline\00_bootstrap.bat          REM engine + upstream reference
copy launchers\_shared\config.local.bat.example launchers\_shared\config.local.bat
notepad launchers\_shared\config.local.bat   REM point UO_CLIENT_DATA at your install
launchers\dev\smoke.bat                      REM must pass before you open a PR
```

You need your own Ultima Online Classic install. Your paths go in
`config.local.bat`, which is gitignored; do not put them in `config.bat`.

## The rules a PR is checked against

These come from `CLAUDE.md` and apply to every change.

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
- Commit messages: a plain imperative summary of what changed, as in the
  existing history.

## Reporting bugs

Use the issue templates. Include your client version (`UO_CLIENT_VERSION`),
the shard you connected to, and what ClassicUO does in the same place. A
difference from ClassicUO is a bug; a difference from the original client is
worth an issue too, but say which one you compared against.

Security issues: see `SECURITY.md`. Do not open a public issue.
