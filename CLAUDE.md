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
Save all evidence in `D:/_agentShared/runs/<project>/<run-id>/`; run-id is `<YYYYMMDD>_<seat-or-story>_<topic>` (UTC, lower-case kebab).
Name files by `D:/_agentShared/rules/evidence_naming.md`; storage and retention follow `D:/_agentShared/rules/evidence_central.md`.
Start: `python D:/_fable/tools/evidence/run.py start --project PROJECT --seat SEAT --story STORY --topic TOPIC --repo REPO` (prints the folder).
Capture there or use `python D:/_fable/tools/evidence/run.py add FILE --seat SEAT --subject SUBJECT --kind KIND`; finish with `python D:/_fable/tools/evidence/run.py done --seat SEAT --summary RESULT` and put the run id in DONE.
Never save evidence in this repo/worktree; product source assets and test fixtures keep their own paths. Weekly purge removes only reviewed done runs older than 14 days; working/blocked/unreviewed runs are protected.
