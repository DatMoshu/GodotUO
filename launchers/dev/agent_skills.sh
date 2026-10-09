#!/usr/bin/env bash
# Links .agents/skills to .claude/skills so Codex finds the project skills, and prints what it linked.
set -euo pipefail
. "$(dirname "$0")/../_shared/common.sh" || exit 1
exec "$UO_PYTHON" "$UO_TOOLS/agent_skills/run.py" "$@"
