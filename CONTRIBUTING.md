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

## Choose the feature layer first

The owner approved this layout. Existing client authoring is scheduled for
migration; it is not a precedent for adding generation to the base client.

| Layer | What belongs here | Review requirements |
|---|---|---|
| L0 Core port | Ported ClassicUO files | Parity only; reason and marked deviation, preferably one seam. Preserve login, input, packets and native-art rendering; zero unmarked drift. |
| L1 Client options | GUO-owned runtime features players can enable | Default off, profile field, Options entry, off-state parity proof, scenario and data_formats section; ADR for rendering/input semantics. No external process, Python or network service. |
| L2 Packs | Art, HD art, splats, PostFX, voices, zones, themes, graphs and generated content | Typed Store components, no code, no execution on install. |
| L3 Editor packages | Authoring, generation, AI, MCP and tours | `#if TOOLS`, plus the shared AI gate for AI; absent from exports. |
| L4 Labs | Experimental client code not ready for L1 | `src/Labs/<feature>/`, opt-in `GuoLabs=true` Debug build, shared command/options registration, absent from exports; feature README and doctor. |
| Tools | `tools/<job>/` | Launcher pairs and the existing config, docs and privacy rules. |

1. Propose new chat commands, profile fields, Options entries, shaders and
   ported-file edits through a Discussion or short ADR, decided by Moshu. Ask
   whether ClassicUO would ship it; otherwise use L2–L4.
2. One feature per PR. Aim below about 1,500 lines or 20 files unless agreed
   beforehand; split larger work and keep the author's credit.
3. Generate in the editor or Labs; the client consumes packs. Python processes
   and model downloads do not belong in an L1 runtime feature.
4. Ship a doctor launcher for external dependencies, with installation guidance.
   Failure text must name the launcher instead of exposing raw exceptions.
5. L1 and L4 features need a scenario under `tools/scenarios/` and a smoke line.
6. Put packable content in the Store; keep built-in presets small and curated.
7. Use the same review template as agents: layer, toggle, parity proof, scenario,
   doctor and docs section. The director triages the layer before code review.

Nearest-neighbour sampling remains mandatory for native art. A distinct render
mode needs an owner-approved ADR to authorize any exception.

Roadmap: shard content negotiation will tell connecting clients which packs a
server requires. The protocol and user consent are future work; this does not
authorize automatic downloads or activation.

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
