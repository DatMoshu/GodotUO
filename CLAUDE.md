@AGENTS.md

## Claude Code only

`AGENTS.md` above is the project's one set of instructions, shared with Codex,
GitHub Copilot and Cursor. Change rules there, not here. This section holds
only what is specific to Claude Code:

- Skills in `.claude/skills/` are slash commands here (`/port-status`, `/parity-check`, `/uo-data`, ...): only the
  GUO-specific ones live in the repo.
- The roles in `.claude/agents/` run as subagents: the UO, port and Godot specialists only.
- The generic game-studio kit (`/sprint-plan`, `/code-review`, `/team-*`, producer/designer roles, coding-standard
  rules) left the repo on 2026-11-08 to keep every session's context small. It is the fleet's
  `gamedev-kit@moshu-fleet` plugin; enable it in your own `.claude/settings.local.json` when a task needs it.
- `.claude/hooks/` and `.claude/settings.json` add session hooks and the
  status line; other tools rely on the git hooks and CI instead.

## When compacting, keep

Keep the current task and its PR or branch, decisions already made, files in flight,
checks run and the next command. Drop repetitive tool output and exploration.
Recover context from the conversation, working-tree diff and task documents that
actually exist. A private handoff directory or coordination service is not required.
Use the current tool's compaction support; do not assume a shared token threshold.

## Evidence

Follow the Evidence section in `AGENTS.md`: use configured external output, record
reproduction context and sanitize public reports. No private evidence tool is needed.
