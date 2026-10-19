# Contributing

The short version is `CONTRIBUTING.md` at the repository root. This page
adds how the branches are run.

## Branches

- Work happens on **`work/<topic>`** branches (`work/ui`, `work/editor`,
  `work/docs`). One topic per branch, and it arrives on `main` as a pull
  request.
- Nobody pushes straight to `main`. Open a pull request; it is reviewed
  and merged once the checks pass.
- One feature per pull request. Aim below about 1,500 lines or 20 files
  unless it was agreed beforehand; split larger work and keep the author's
  credit.
- Several branches may be in flight at once; ADR numbers are claimed in
  `docs\architecture\README.md` before an ADR is written so they do not
  collide.
- The owner keeps a worktree per branch (`.claude\worktrees\<name>`) so
  branches do not tread on each other's build output. A worktree uses the
  main checkout's engine and upstream reference through `UO_GODOT_HOME` and
  `UO_UPSTREAM_DIR` (no directory links); run
  `launchers\dev\worktree_setup.bat` once in a fresh one. Set `UO_BUILD_DIR`
  in `config.local.bat` (with `%UO_CHECKOUT%`) to keep generated output
  outside the checkout.

## Before you commit

```bat
dotnet build godot\GUO\GUO.csproj     REM 0 errors
launchers\dev\smoke.bat               REM must end with OK
launchers\pipeline\03_port_audit.bat  REM if you touched porting progress; commit docs\port_status.md
python tools\privacy_scan\run.py --staged   REM no private paths, names, tokens or addresses
```

In the pull request, record the commands you actually ran and what they
printed. Say which checks you could not run, and why.

CI (`.github\workflows\ci.yml` and `build.yml`) repeats the build, compiles
the Python tools, checks that `docs\port_status.md` is current, and rejects
game data, machine paths outside `docs\`, and a committed
`config.local.bat`.

## Choose the feature layer first

Before you write code, pick the layer your change belongs to: L0 core port,
L1 client option, L2 pack, L3 editor package, L4 Labs, or a tool. The layer
decides what you must prove. See [Feature Layers](Feature-Layers.md).

- A new chat command, profile field, Options entry, shader or edit to a ported
  file starts with a Discussion or a short ADR.
- Generation belongs in the editor or Labs; the client consumes packs.
- An external dependency needs a doctor launcher that says what to install.
- L1 and L4 features need a scenario under `tools/scenarios/` and a smoke line.
- Fill in the review template: layer, toggle, parity proof, scenario, doctor
  and docs section.

Packable content (art, voices, splats and the like) goes in the Store as a
pack: see [Content Packs](Content-Packs.md).

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
8. **AI is optional in the editor.** Every AI feature uses the shared
   `AiFeatures` switch; with it off the AI surfaces are hidden.

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
