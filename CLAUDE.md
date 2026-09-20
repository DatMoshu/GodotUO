# GUO — porting Classic Ultima Online to Godot

**Goal:** a complete Ultima Online Classic client running on Godot 4 .NET,
off FNA entirely, with behaviour matching the original.

**Approach:** transplant ClassicUO's C# rather than rewrite the game. Keep the
network stack, file readers and game logic; reimplement only what genuinely
binds to FNA.

Read `docs/port_plan.md` before doing port work. It is short and it explains
why the project is structured the way it is.

---

## Technology stack

| | |
|---|---|
| **Engine** | Godot **4.7.2 stable, mono/.NET** — pinned in `tools/godot`, gitignored |
| **Language** | C# (`net8.0`). The non-mono Godot build will not work. |
| **Upstream** | ClassicUO — C#, BSD 2-Clause, read-only under `sources/` |
| **Tooling** | Python 3.12, one shared package (`tools/guo`) |
| **Platform** | Windows first; keep platform-specific code isolated |

`godot` and `godot-console` resolve from `tools/godot` once that folder is on
`PATH`. **Scripts, agents and CI must call `godot-console`** — it blocks and
writes to stdout; plain `godot` returns immediately and prints nothing.

---

## Repository layout

```
launchers/        .bat entry points, grouped by job. Start here.
  _shared/        config.bat (the ONLY file you edit) + common.bat
  game/play.bat   THE launcher
  editor/         open the Godot project or the upstream reference
  pipeline/       numbered data steps, run in order
  dev/            build, smoke, screenshot, sync, cache
godot/GUO/        the Godot project
  src/Compat/     XNA compatibility shim — read its README first
  src/{IO,Assets,Render,Network,Game,Input,Configuration,Utility}/
sources/ClassicUO/  upstream reference — READ ONLY, never edit
tools/            one folder per job + one per third-party program
  guo/            shared Python package; all tools import from here
docs/             port_plan.md, data_formats.md, port_status.md (generated)
  architecture/   ADRs — binding decisions. ADR-0001 governs the renderer.
build/            generated artifacts — gitignored
.claude/          agents, skills, rules, hooks
```

---

## Configuration

`launchers/_shared/config.bat` is **the only file a user edits.** Everything
resolves in one order, everywhere:

**environment variable → `config.bat` → central shared config (optional)**

No launcher, tool or runtime hardcodes a path. `tools/guo/config.py` reads
the same values, parsing `config.bat` directly when run outside a launcher, so
behaviour is identical from a launcher, the editor, an agent or CI.

Key settings: `UO_CLIENT_DATA` (your UO install), `UO_CLIENT_VERSION`,
`UO_CACHE_DIR`, `UO_SHARD_HOST` / `UO_SHARD_PORT`.

---

## Everyday commands

```
launchers\pipeline\00_bootstrap.bat        fresh clone: fetch engine + upstream
launchers\pipeline\01_verify_client_data.bat   check the UO install
launchers\pipeline\03_port_audit.bat       re-measure port progress
python tools\port_bulk\run.py --area X    port a mechanical-tier area
dotnet build godot\GUO\GUO.csproj         fast build loop (~1s, use this)
launchers\dev\build.bat                    build C# only
launchers\dev\smoke.bat                    full health check — run before commit
launchers\dev\screenshot.bat               capture a frame
launchers\dev\sync_upstream.bat            check upstream drift
launchers\game\play.bat                    run the client
```

---

## The porting model

Every upstream file is classified into a tier, measured by the audit:

| Tier | Files | Lines | Treatment |
|---|---:|---:|---|
| `verbatim` | 243 | 74,522 | Renamespace `ClassicUO.*` → `GUO.*` |
| `shim` | 102 | 50,513 | Renamespace + `using GUO.Compat;` |
| `rewrite` | 88 | 31,170 | Reimplement on Godot |

**Four fifths of this port is mechanical.** That ratio is what makes the
project feasible. The main way to lose it is letting `GUO.Compat` grow
engine behaviour — it holds value types only. If a shim file wants a texture
or a device, it is misclassified, not a reason to expand Compat.

---

## Rules

1. **Never edit `sources/`.** Read-only reference.
2. **Port faithfully.** No reformatting, renaming, modernising, or
   opportunistic bug fixing. Every gratuitous edit must be reconciled by hand
   on every future upstream merge. Note bugs; do not fix them mid-port.
3. **Parity before improvement.** Make it identical first; propose changes
   separately.
4. **Build in small batches.** One file's error is cheap to find; fifty
   files' errors are not.
5. **Measure, do not estimate.** Progress claims come from the audit.
   Its tiers are derived from imports and have been wrong before — the
   first pass missed SDL3 entirely. When a file resists its tier, fix the
   classifier rather than working around it.
6. **"Ported" ≠ "working".** The audit matches filenames. Claims of working
   behaviour need a smoke test or a screenshot. Say which you actually have.
7. **Never filter pixel art.** `default_texture_filter=0` is deliberate; any
   new viewport or material must preserve nearest-neighbour sampling.
8. **Never commit game data or credentials.** The UO install is read in place
   and is proprietary.

---

## Agents

The studio agents come from Claude Code Game Studios (MIT); Unity and Unreal
specialists were removed. Port-specific additions:

| Agent | Owns |
|---|---|
| `uo-port-strategist` | What gets ported next, tier arbitration, the plan |
| `fna-migration-specialist` | The verbatim/shim bulk, and `GUO.Compat` |
| `uo-fileformat-engineer` | `.mul`/`.uop` readers, loaders, the decode cache |
| `uo-network-engineer` | Packets, handshake, encryption, compression |
| `uo-render-engineer` | The rewrite tier: renderer, hues, input, audio |

Skills: `/port-status`, `/port-file`, `/parity-check`, plus the inherited
studio set. MCP: `guoasset` (see `tools/guoasset/README.md`) renders UO art
from the client data as a parity reference.

---

## Licensing

- **ClassicUO** — BSD 2-Clause. Ported files keep their upstream copyright
  header; licence and originating commit recorded in `docs/upstream/`.
- **Claude Code Game Studios** — MIT; provenance in `docs/upstream/`.
- **UO client data** — proprietary, never redistributed. Users supply their
  own installation.
