# GUO

Porting **Classic Ultima Online** to **Godot 4 .NET** — getting the client off
FNA while keeping the game behaviour intact.

The strategy is a transplant rather than a rewrite: ClassicUO's C# network
stack, file readers and game logic are carried across; only the parts that
genuinely bind to FNA are reimplemented on Godot.

> **You supply your own Ultima Online installation.** No game data is
> distributed with this project. The client reads your install in place and
> never writes to it.

---

## Requirements

| | |
|---|---|
| **Godot** | 4.7.2 stable, **mono/.NET** build — fetched by the bootstrap step |
| **.NET SDK** | 8.0 or newer |
| **Python** | 3.12+ (tooling) |
| **A UO client install** | Any modern Classic client; 7.0.107 is what this was scaffolded against |

---

## Getting started

```bat
REM 1. Fetch the pinned engine and the upstream reference
launchers\pipeline\00_bootstrap.bat

REM 2. Point the project at your UO install
notepad launchers\_shared\config.bat      REM set UO_CLIENT_DATA

REM 3. Confirm everything resolves
launchers\dev\smoke.bat

REM 4. Run it
launchers\game\play.bat
```

Optionally add the engine folder to `PATH` so `godot` resolves everywhere:

```
<your clone>\tools\godot
```

Use `godot` for the interactive editor and **`godot-console` for scripts and
CI** — the console build blocks and writes to stdout.

---

## Layout

```
launchers/        .bat entry points grouped by job — start here
  _shared/        config.bat (the only file you edit) + common.bat
  game/play.bat   THE launcher
  pipeline/       numbered data steps, run in order
  dev/            build, smoke, screenshot, upstream sync
godot/GUO/        the Godot project (C#)
  src/Compat/     XNA compatibility shim
sources/ClassicUO/  upstream reference — read only, not committed
tools/            one folder per job, plus the pinned engine
docs/             the plan, the data contract, generated status
```

---

## How the port is tracked

Every upstream file is classified by how tightly it binds to FNA, and the
audit re-measures it from source on demand:

```bat
launchers\pipeline\03_port_audit.bat     REM writes docs/port_status.md
```

| Tier | Files | Lines | Treatment |
|---|---:|---:|---|
| `verbatim` | 243 | 74,522 | Renamespace and compile |
| `shim` | 102 | 50,513 | Swap `using` to `GUO.Compat` |
| `rewrite` | 88 | 31,170 | Reimplement on Godot |

About **80% of the port is mechanical** — most of ClassicUO's FNA usage turns
out to be maths and colour structs, not rendering. `GUO.Compat` supplies
API-compatible versions of those, so a third of the codebase needs one line
changed per file.

`docs/port_plan.md` explains the reasoning and the phase order.

---

## Keeping up with upstream

ClassicUO is actively developed. This reports commits since the last reviewed
pin, and flags any touching files already ported:

```bat
launchers\dev\sync_upstream.bat
```

---

## Licensing

| Component | Licence |
|---|---|
| [ClassicUO](https://github.com/ClassicUO/ClassicUO) | BSD 2-Clause — ported files keep their upstream copyright headers |
| [Claude Code Game Studios](https://github.com/Donchitos/Claude-Code-Game-Studios) | MIT — the agent and skill system in `.claude/` |
| [Godot](https://godotengine.org) | MIT |
| **UO client data** | Proprietary. Never redistributed; supply your own. |

Provenance for the vendored upstreams — licence text and originating commits
— is recorded in `docs/upstream/`.

This project is not affiliated with, endorsed by, or associated with
Electronic Arts or Broadsword Online Games.
