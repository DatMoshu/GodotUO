# Contributing

The short version is `CONTRIBUTING.md` at the repository root. This page
adds how the branches are run.

## Branches

- **`main`** is the owner's. Nobody else commits to it or pushes to it.
- Work happens on **`work/<topic>`** branches (`work/ui`, `work/editor`,
  `work/docs`). One topic per branch; a branch is merged into `main` by the
  owner, usually as a merge commit named after the sprint it closed.
- Agents and contributors never push. They leave a branch with clean commits
  and a report; the owner reviews, merges and pushes.
- Several branches may be in flight at once; ADR numbers are claimed in
  `docs\architecture\README.md` before an ADR is written so they do not
  collide.
- The owner keeps a worktree per branch (`.claude\worktrees\<name>`) so
  branches do not tread on each other's build output.

## Before you commit

```bat
dotnet build godot\GUO\GUO.csproj     REM 0 errors
launchers\dev\smoke.bat               REM 5/5
launchers\pipeline\03_port_audit.bat  REM if you touched porting progress; commit docs\port_status.md
```

CI (`.github\workflows\ci.yml` and `build.yml`) repeats the build, compiles
the Python tools, checks that `docs\port_status.md` is current, and rejects
game data, machine paths outside `docs\`, and a committed
`config.local.bat`.

## The rules a change is checked against

1. **Never edit `sources\`.** It is the upstream reference, read only.
2. **Port faithfully.** No reformatting, renaming, modernising or
   opportunistic bug fixing in ported files. Where a change is unavoidable,
   mark it `PORT DEVIATION (GUO)` with the reason.
3. **Parity before improvement.** Make it behave like ClassicUO first;
   propose improvements in their own PR.
4. **Keep `GUO.Compat` to value types.** If a shim file wants a texture or a
   device, fix the tier; do not grow Compat.
5. **Measure, do not estimate.** Progress claims come from the audit.
   "Ported" is not "working": say whether you have a smoke run, a screenshot
   or a parity diff.
6. **Never filter pixel art.**
7. **Never commit game data or credentials.** No `.mul`, `.uop`, `.idx`,
   `.def` or `.enu` files, no captures rendered from them, no passwords, no
   machine paths, no LAN addresses or device serials. Those belong in
   `config.local.bat` or on the command line.

## Commit hygiene

- Small batches: one subsystem or one fix per commit and per PR. One file's
  error is cheap to find; fifty files' errors are not.
- A plain imperative summary, then a body that says what was verified and
  how: which launcher ran, what it printed, which picture under `build\`
  shows it. Read the existing history for the shape.
- If the engine rewrote `project.godot` during a windowed editor session,
  check that diff before staging it (see [Editor](Editor.md)).
- Visual changes: attach a screenshot, ideally next to ClassicUO's
  (`launchers\dev\ab_compare.bat`).

## Licences

Code here is BSD 2-Clause; opening a PR licenses your contribution the same
way. Ported files keep their upstream `SPDX-License-Identifier` header, and
new C# files start with `// SPDX-License-Identifier: BSD-2-Clause`. The
ModernUO patches are GPL-3.0.

## Reporting bugs

Use the issue templates. Include your client version (`UO_CLIENT_VERSION`),
the shard you connected to, and what ClassicUO does in the same place. A
difference from ClassicUO is a bug. Security issues go through GitHub's
private vulnerability reporting, see `SECURITY.md`; never a public issue.
