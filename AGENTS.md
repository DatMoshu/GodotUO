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
  _shared/        config.local.bat (yours, gitignored) + config.bat + common.bat
  game/play.bat   THE launcher
  editor/         open the Godot project or the upstream reference
  pipeline/       numbered data steps, run in order
  shard/          the local ModernUO dev server: fetch, build, run
  dev/            build, smoke, screenshot, sync, cache
  android/        doctor, export, install, run, smoke on a device (ADR-0017)
  windows/        doctor, export -- the .exe, with the sigil as its icon
  web/            doctor, export, serve, smoke -- on a community C# web build (ADR-0008)
  steamdeck/      doctor, export, push, run, screenshot, smoke over ssh (ADR-0018)
godot/GUO/        the Godot project
  src/Compat/     XNA compatibility shim — read its README first
  src/{IO,Assets,Render,Network,Game,Input,Configuration,Utility}/
sources/ClassicUO/  upstream reference — READ ONLY, never edit
tools/            one folder per job + one per third-party program
  guo/            shared Python package; all tools import from here
  guoasset/       parity reference renderer (MCP), on upstream's loaders
  guo_mcp/        opt-in loopback MCP to drive a running client (data_formats section 29)
  modernuo/       the dev shard: patches, config templates (src/ gitignored)
  server_lab/     the server compatibility lab: cases x backends grid, wiki page
  android/        the Android export tool + preset template
  windows/        the Windows export tool + preset template + icon check
  web/            the web export tool + preset template
  godot_web/      the community Godot 4.7.2 build that exports C# to the web (gitignored; README only)
  ws_bridge/      WebSocket-to-TCP bridge between the browser client and the shard
  steamdeck/      the Linux export tool + preset template; talks to a Deck over ssh
  brand/          builds every app icon and the splash from design/brand/
design/brand/     the GUO sigil, master of every icon (never the engine's logo)
docs/             port_plan.md, data_formats.md, port_status.md (generated)
  architecture/   ADRs — binding decisions. ADR-0001 governs the renderer.
build/            generated artifacts — gitignored
.claude/          agents, skills, rules, hooks
```

---

## Configuration

A user's own paths go in `launchers/_shared/config.local.bat`, which is
gitignored (start from `config.local.bat.example`). `config.bat` holds the
shared defaults and has no machine paths in it. Everything resolves in one
order, everywhere:

**environment variable → `config.local.bat` → `config.bat` → central shared config (optional)**

No launcher, tool or runtime hardcodes a path, and no machine path is ever
committed. `tools/guo/config.py` and `tools/guoasset` read the same values,
parsing the .bat files directly when run outside a launcher, so behaviour is
identical from a launcher, the editor, an agent or CI.

Key settings: `UO_CLIENT_DATA` (your UO install), `UO_CLIENT_VERSION`,
`UO_CACHE_DIR`, `UO_WORKSPACE_DIR` (the per-user server and client
profiles, default `%LOCALAPPDATA%\GUO`), `UO_SHARD_HOST` / `UO_SHARD_PORT`, `UO_GODOT_HOME` (the
engine folder, `tools\godot`) and `UO_UPSTREAM_DIR` (the folder holding
ClassicUO, `sources`).

**Git worktrees need no directory links.** A worktree has no `tools\godot`
or `sources\ClassicUO` (both gitignored); left unset, `UO_GODOT_HOME` and
`UO_UPSTREAM_DIR` resolve to the main checkout's copies, in the launchers,
`tools/guo/config.py` and MSBuild alike. In a fresh worktree run
`python tools\worktree_setup\run.py` (or `launchers\dev\worktree_setup.bat`)
once: it copies your `config.local.bat` and `deny.local.txt` across, checks
both folders resolve and runs the headless import. Never `mklink`.

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
launchers\dev\playtest.bat                 play a session and check it (needs a shard)
launchers\dev\endurance.bat                play on for a while and watch for drift
launchers\dev\plugin_probe.bat             load test plugins and check what they see (needs a shard)
launchers\dev\multi_client.bat             four scripted clients at once, tiled 2x2 (needs a shard)
launchers\dev\side_by_side.bat             ClassicUO and GUO live on one monitor, same spot (needs a shard)
launchers\dev\server_lab.bat row modernuo  run the server lab's cases against a server (tools/server_lab)
launchers\dev\render_diff.bat NAME         compare the two clients' "renderdump NAME" dumps
launchers\dev\sync_upstream.bat            check upstream drift
launchers\dev\worktree_setup.bat           ready a fresh git worktree (no links)
launchers\dev\build_guoasset.bat           build the parity reference MCP
launchers\dev\mcp.bat                      run the client with its automation MCP on (GUO_MCP_PORT + GUO_MCP_TOKEN)
launchers\shard\run.bat                    run the local dev shard
launchers\shard\populate.bat               generate its world (once)
launchers\game\play.bat                    run the client
launchers\android\doctor.bat               what an Android export needs on this machine
launchers\android\smoke.bat                export, install, run on the device, wait for the login gump
launchers\web\doctor.bat                   what the web export needs (the fork in tools/godot_web, ADR-0008)
launchers\windows\export.bat               export the Windows build and check its icon
launchers\steamdeck\doctor.bat             what a Steam Deck build needs, here and on the Deck (ssh)
launchers\steamdeck\smoke.bat              export, push, run on the Deck, wait for the login gump, screenshot
launchers\dev\brand_icons.bat              rebuild every icon from the sigil
```

---

## The porting model

Every upstream file is classified into a tier, measured by the audit:

| Tier | Files | Lines | Treatment |
|---|---:|---:|---|
| `verbatim` | 247 | 74,760 | Renamespace `ClassicUO.*` → `GUO.*` |
| `shim` | 116 | 56,510 | Renamespace + `using GUO.Compat;` / `using GUO.Platform.Sdl;` |
| `rewrite` | 37 | 21,959 | Reimplement on Godot |

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
9. **AI is optional in the editor.** Every AI feature must use the shared
   `AiFeatures` gate (`guo/ai/enabled` in Editor Settings), including UI,
   discovery, agents, queues, model/service requests, image generation and
   editor MCP. Hide AI surfaces and prevent work when disabled; link running
   work to its cancellation lifetime. Keep manual authoring available.

---

## Agents, skills and rules, in every coding agent

This file is the one set of project instructions. Codex, GitHub Copilot and
Cursor read `AGENTS.md` directly; Claude Code reads it through `CLAUDE.md`,
which imports it. Every agent follows the same rules, skills and roles.

**Skills** (`/port-status`, `/port-file`, `/parity-check`, `/android-build`,
`/web-build`, the UO authoring skills and the inherited studio set) live in
`.claude/skills/<name>/SKILL.md`, in the open Agent Skills format.

| Tool | Finds the skills |
|---|---|
| Claude Code | `.claude/skills/`, natively |
| GitHub Copilot, Cursor | `.claude/skills/`, natively |
| Codex | `.agents/skills/`: run `launchers\dev\agent_skills.bat` once per checkout; it links the folder to `.claude/skills/` (gitignored) |

When a task matches a skill's `description`, read that `SKILL.md` and follow
it, whichever tool you are. Skills name Claude Code tools (Task, AskUserQuestion,
subagents); use your own equivalent, or do the step yourself.

**Roles.** `.claude/agents/<name>.md` defines one specialist each. In a tool
without subagents, when work falls in a role's area, read its file and work
by it. The studio roles come from Claude Code Game Studios (MIT); Unity and
Unreal specialists were removed. Port-specific additions:

| Agent | Owns |
|---|---|
| `uo-port-strategist` | What gets ported next, tier arbitration, the plan |
| `fna-migration-specialist` | The verbatim/shim bulk, and `GUO.Compat` |
| `uo-fileformat-engineer` | `.mul`/`.uop` readers, loaders, the decode cache |
| `uo-network-engineer` | Packets, handshake, encryption, compression |
| `uo-render-engineer` | The rewrite tier: renderer, hues, input, audio |
| `mobile-web-engineer` | Android and web exports, the touch layer, CI for both |
| `uo-editor-engineer` | The GUO editor add-on and the world tools |
| `uo-multi-architect` | Authoring new multis: mining, generators, validators, proofs |
| `localization-lead` | GUO UI and shard translations, source drift, new languages and multilingual onboarding; see `docs/localization.md` |

**Path rules.** `.claude/rules/*.md` hold coding rules; each one's `paths:`
front matter says which files it covers. Read the matching rules before
editing those files.

**Checks that bind every agent.** Run `launchers\dev\smoke.bat` before a
commit. `launchers\dev\install_git_hooks.bat` installs the pre-push docs lint
and privacy scan; CI runs the same guards on every push. Claude Code's hooks
in `.claude/hooks/` are extra and only run there.

**MCP.** `guoasset` (see `tools/guoasset/README.md`) renders UO art from the
client data with upstream's loaders, as a parity reference. Any MCP-capable
agent can run it.

---

## Licensing

- **ClassicUO** — BSD 2-Clause. Ported files keep their upstream copyright
  header; licence and originating commit recorded in `docs/upstream/`.
- **Claude Code Game Studios** — MIT; provenance in `docs/upstream/`.
- **UO client data** — proprietary, never redistributed. Users supply their
  own installation.
