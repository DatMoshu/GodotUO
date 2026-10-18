@AGENTS.md

## Claude Code only

`AGENTS.md` above is the project's one set of instructions, shared with Codex,
GitHub Copilot and Cursor. Change rules there, not here. This section holds
only what is specific to Claude Code:

- Skills in `.claude/skills/` are slash commands here (`/port-status`, ...).
- The roles in `.claude/agents/` run as subagents.
- `.claude/hooks/` and `.claude/settings.json` add session hooks and the
  status line; other tools rely on the git hooks and CI instead.

## When compacting, keep

Current task and its PR or branch, decisions already made, files in flight, and the next command to run. Drop tool
output and exploration. The project handoff files live in the shared agent area, `_agentShared/handoffs/<project>_*.md`; re-read the
latest one after a compaction instead of re-deriving the brief. Auto-compact is set to 300k tokens fleet-wide: do not
close and reopen a long session to "reset" it, use `/compact focus:<task>` or `/clear` at a task boundary.

## Evidence

Agents save evidence (screenshots, recordings, logs of a run) in the shared agent area, under
`_agentShared/runs/<project>/<run-id>/`, never in this repo or a worktree; run-id is
`<YYYYMMDD>_<seat-or-story>_<topic>` (UTC, lower-case kebab). The fleet's evidence tool starts the folder and names
the files; the naming and retention rules live beside it in `_agentShared/rules/`. Product source assets and test
fixtures keep their own paths.
