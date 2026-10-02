#!/usr/bin/env python3
"""The resolved configuration as POSIX shell exports, for the .sh launchers.

    eval "$(python3 tools/shellenv/run.py)"

launchers/_shared/common.sh runs this; it is the shell twin of common.bat
calling config.bat. Every setting config.local.bat and config.bat define is
exported, resolved in the usual order (environment, config.local.bat,
config.bat) by tools/guo/config.py, so the .bat files stay the one place
settings are written on every OS. A value that still holds an unexpanded
%VAR% -- a Windows-only default such as %LOCALAPPDATA% -- is left out, and
the derived paths the launchers need (the engine, the project, the cache)
come from Config, which knows this OS.
"""
from __future__ import annotations

import os
import shlex
import sys
from pathlib import Path

sys.path.insert(0, str(Path(__file__).resolve().parents[1]))

from guo import load_config  # noqa: E402
from guo.config import native_path, parse_config_bat  # noqa: E402


def exports() -> dict[str, str]:
    cfg = load_config()
    shared = cfg.root / "launchers" / "_shared"
    values = {"UO_ROOT": str(cfg.root)}
    local = shared / "config.local.bat"
    if local.is_file():
        values = parse_config_bat(local, values)
    values = parse_config_bat(shared / "config.bat", values)
    out = {k: os.environ.get(k) or native_path(v) for k, v in values.items() if "%" not in v}

    out.update({
        "UO_ROOT": str(cfg.root),
        "UO_GODOT_PROJECT": str(cfg.godot_project),
        "UO_SOURCES": str(cfg.sources),
        "UO_TOOLS": str(cfg.tools),
        "UO_DOCS": str(cfg.docs),
        "UO_BUILD": str(cfg.build),
        "GODOT_VERSION": cfg.godot_version,
        "GODOT_FLAVOR": cfg.godot_flavor,
        "UO_GODOT_DIR": str(cfg.godot_dir),
        "GODOT_EXE": os.environ.get("GODOT_EXE") or str(cfg.godot_exe),
        "GODOT_CONSOLE": os.environ.get("GODOT_CONSOLE") or os.environ.get("GODOT_EXE") or str(cfg.godot_console_exe),
        "UO_CLIENT_VERSION": cfg.client_version,
        "UO_CACHE_DIR": str(cfg.cache_dir),
        "UO_WORLD_PROJECT": str(cfg.world_project),
        "UO_SHARD_SRC": str(cfg.shard_src),
        "UO_SHARD_DIST": str(cfg.shard_dist),
        "UO_STORE_DIR": str(cfg.store_dir),
        "UO_SHARD_HOST": cfg.shard_host,
        "UO_SHARD_PORT": str(cfg.shard_port),
        # config.bat's "python" is Windows' name for it.
        "UO_PYTHON": os.environ.get("UO_PYTHON") or sys.executable,
    })
    if str(cfg.client_data) not in ("", "."):
        out["UO_CLIENT_DATA"] = str(cfg.client_data)
    return out


def main() -> int:
    for key, value in sorted(exports().items()):
        print(f"export {key}={shlex.quote(value)}")
    return 0


if __name__ == "__main__":
    sys.exit(main())
