---
name: guo-portability-review
description: Audit GUO as a public fresh clone with ordinary contributor tools and no maintainer infrastructure. Check agent instructions, setup, paths, optional services, dependency packaging and shareable documentation.
---

# GUO public-clone review

Read [the review contract](../../../docs/review/contract.md), README, CONTRIBUTING,
AGENTS, CLAUDE, the agent wiki page, config defaults and bootstrap/doctor entry points.
No private fleet, organization CLI, personal skill directory, paid provider, account
or external checkout is a prerequisite for running this review.

Trace the first-run path for a player, contributor and coding agent independently.
List declared prerequisites versus ambient dependencies. Use an isolated checkout,
temporary configuration/home and clean environment for execution when feasible;
never erase the current user's settings to simulate a clean machine. Do not copy
local config, ignored binaries or proprietary fixtures into a clean-clone test.

Check tracked agent hooks, skills, roles, MCP configuration, launchers, MSBuild,
export presets and docs transitively. Follow a referenced helper far enough to see
whether it requires an untracked service or file. Missing optional integration must
not prevent unrelated work. Inspect instructions as well as executable code: asking
an agent to find private handoffs is an onboarding defect even if builds pass.

Check env -> local config -> shared defaults precedence across Python, shell and C#;
worktree fallback; spaces/non-ASCII paths; cwd independence; external generated output;
portable dependency pins/checksums; licenses; offline/cancel/retry behavior. Read local
override values only as needed and never expose secrets or personal paths in reports.

Distinguish owner-specific workflow prose from legitimate upstream attribution,
copyright, public repository URLs and reproducible dependency provenance. Preserve
the latter. Replace personal approval roles with project-maintainer roles where
appropriate, without weakening the review requirement.

For missing game data, report the supported setup message and read-only install rule.
For optional authoring tools, require a usable acquisition/import workflow, not a
hardcoded path to an existing workstation. Use Pixelorama and Sprite/Fit Lab as
separate dependency cases; see [their plan](../../../docs/review/authoring-integrations.md).

Run the relevant docs, skill, launcher and privacy checks. A privacy scan is not a
fresh-clone test. Report source inspection, isolated execution and any unavailable
prerequisites separately; do not certify first-run success using a warm local cache.
