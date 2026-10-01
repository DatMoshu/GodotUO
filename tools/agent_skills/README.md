# agent_skills

Makes the project's skills visible to every coding agent from one copy in git.

| Tool | Reads skills from |
|---|---|
| Claude Code | `.claude/skills/` |
| GitHub Copilot, Cursor | `.claude/skills/` (also `.agents/skills/`) |
| Codex | `.agents/skills/` only |

The skills live in `.claude/skills/<name>/SKILL.md` (the open Agent Skills
format). `run.py` links `.agents/skills` to that folder. The link is gitignored,
so nothing is duplicated: a directory junction on Windows, a relative symlink
elsewhere.

```
launchers\dev\agent_skills.bat            create or repair the link
launchers\dev\agent_skills.bat --check    validate every SKILL.md and report the link
launchers\dev\agent_skills.bat --remove   remove the link only (never the skills)
```

`--check` fails if a skill folder has no `SKILL.md`, or if its front matter
`name` doesn't match the folder or it has no `description`. Those are the
fields every tool uses to find a skill. Python standard library only.
