# Working with AI Agents

GUO is built with AI coding agents working alongside people. Every agent and
every person follows the same rules, skills and checks. Using an agent is
optional; the repository works the same without one.

## One set of instructions, in every tool

| Tool | Reads | Skills |
|---|---|---|
| Claude Code | `CLAUDE.md`, which imports `AGENTS.md` | `.claude/skills/` |
| Codex | `AGENTS.md` | `.agents/skills/`: run `launchers\dev\agent_skills.bat` once per checkout |
| GitHub Copilot | `AGENTS.md` | `.claude/skills/` |
| Cursor | `AGENTS.md` | `.claude/skills/` |

`AGENTS.md` holds the project's instructions: the porting model, the rules,
the commands and the roles. Change rules there, not in a tool-specific file.

## Skills

A skill is a folder with a `SKILL.md`: a name, a description that says when to
use it, and the steps to follow. It's the open Agent Skills format, so one
copy serves every tool. Some you'll use most:

| Skill | Does |
|---|---|
| `port-status` | Measures port progress: the audit, upstream drift, the client data. |
| `port-file` | Ports one upstream file or subsystem by its tier, builds it and re-measures. |
| `parity-check` | Compares GUO's frame against the reference renderer, pixel by pixel. |
| `android-build` | Doctor, export, install and smoke on the attached device. |
| `uo-data` | Authors new art, gumps or animations into a staged data set and proves them in game. |
| `uo-multi`, `uo-decorate` | Build and furnish new multis ([Building Multis](Building-Multis.md)). |
| `editor-smoke` | Proves the editor add-on panel by panel. |
| `uo-world-authoring`, `localize` | World authoring flows, and UI and shard translations. |

The rest come from Claude Code Game Studios (MIT): design, QA, release and
production workflows. `launchers\dev\agent_skills.bat --check` validates every
skill's name and description.

## Roles

`.claude/agents/<name>.md` each define one specialist. In Claude Code they run
as subagents. In other tools, read the role's file when the work falls in its
area. The port-specific roles:

| Role | Owns |
|---|---|
| `uo-port-strategist` | What gets ported next, tier arbitration, the plan |
| `fna-migration-specialist` | The mechanical verbatim/shim bulk, and `GUO.Compat` |
| `uo-fileformat-engineer` | `.mul`/`.uop` readers, loaders, the decode cache |
| `uo-network-engineer` | Packets, handshake, encryption, compression |
| `uo-render-engineer` | The renderer, hues, input and audio (rewrite tier) |
| `mobile-web-engineer` | Android, web, touch, and their CI |
| `uo-editor-engineer` | The editor add-on and the world tools |
| `uo-multi-architect` | Authoring new multis |
| `localization-lead` | UI and shard translations, new languages and onboarding |

## Rules agents work under

The same rules as people ([Contributing](Contributing.md)), plus:
- **Pick the feature layer first** ([Feature Layers](Feature-Layers.md)), and
  fill in the same review template as people: layer, toggle, parity proof,
  scenario, doctor and docs.
- **AI is optional in the editor.** Every AI feature is behind Editor Settings
  > GUO > AI > Enabled. With it off, the AI and automation surfaces are hidden
  and their work stops.
- **Never edit `sources/`.** The upstream reference is read-only.
- **Measure, don't estimate.** Progress claims come from the audit.
  "Ported" isn't "working": say whether there's a smoke test or a screenshot.
- **No game data, credentials or machine paths** in anything committed. CI and
  the pre-push hook check this.
- **Scripted runs never take focus**, so an agent can run the client while a
  person uses the machine.
- **A person reviews and merges.** Agents work on branches; nothing they make
  goes to `main` without passing the same checks as any change.
- **Scripted proofs** run through `tools/scenario_run`
  (`launchers\dev\scenario_run.bat`); the scenario files are in
  `tools/scenarios/`.

## MCP

`tools/guoasset` is an MCP server that renders UO art with upstream's own
loaders, as the parity reference. Any MCP-capable agent can use it; see its
README.

`tools/guo_mcp` drives a running client (`launchers\dev\mcp.bat`).
`tools/editor_mcp` drives the editor. It is off unless the editor is started
with a port and token in its environment. Editor Settings > GUO > MCP Access
has one switch per group of tools; anything that changes your server starts
off, and each call is checked against your choices. See
`tools\editor_mcp\README.md`.
