"""Where the runner finds its shared locations: environment, then config.local.bat, then config.bat.

Nothing here has a default path. A setting that is not configured means "do not do that part"
(no registry row, no shared copy, no Drive master), and the run says so in its events.

    GUO_RUNS_DB          the registry (SQLite file shared by every project's runs)
    GUO_RUNS_SHARED_DIR  folder that gets run.json, events.jsonl, summary.md and the stills
    GUO_RUNS_VIDEO_DIR   folder that gets the master video (the Drive folder)
"""

from __future__ import annotations

import os
import sys
from pathlib import Path

sys.path.insert(0, str(Path(__file__).resolve().parents[1]))

from guo.config import find_repo_root, parse_config_bat  # noqa: E402

KEYS = ("GUO_RUNS_DB", "GUO_RUNS_SHARED_DIR", "GUO_RUNS_VIDEO_DIR")


def read_setting(key: str, root: Path | None = None) -> str:
    """Environment first, then the project's launcher config files; '' when nobody set it."""
    value = os.environ.get(key, "")
    if value:
        return value
    root = (root or find_repo_root()).resolve()
    shared = root / "launchers" / "_shared"
    values: dict[str, str] = {"UO_ROOT": str(root)}
    for name in ("config.local.bat", "config.bat"):
        if (shared / name).is_file():
            values = parse_config_bat(shared / name, values)
    return values.get(key.upper(), "")
