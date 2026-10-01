@AGENTS.md

## Claude Code only

`AGENTS.md` above is the project's one set of instructions, shared with Codex,
GitHub Copilot and Cursor. Change rules there, not here. This section holds
only what is specific to Claude Code:

- Skills in `.claude/skills/` are slash commands here (`/port-status`, ...).
- The roles in `.claude/agents/` run as subagents.
- `.claude/hooks/` and `.claude/settings.json` add session hooks and the
  status line; other tools rely on the git hooks and CI instead.
