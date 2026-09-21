"""Write the dev shard's configuration from the tracked templates.

ModernUO asks for its data directory, its listeners and its expansion at a
console prompt on first run, and refuses to prompt when stdin is redirected --
which is every scripted or agent-driven run. Writing the files up front is how
the shard boots unattended.

Existing files are left alone. Delete one to have it rebuilt, or edit it in
place when a setting should differ on this machine only.

`launchers\\shard\\run.bat` calls this. Run directly it reads the same settings
from config.bat, as every tool here does.
"""

from __future__ import annotations

import json
import sys
from pathlib import Path

sys.path.insert(0, str(Path(__file__).resolve().parents[1]))

from guo.config import load_config  # noqa: E402

TEMPLATES = Path(__file__).resolve().parent / "config"


def main() -> int:
    cfg = load_config()

    if not cfg.shard_dist.is_dir():
        print(f"[shard] Not built: {cfg.shard_dist}", file=sys.stderr)
        print("[shard] Run launchers\\shard\\build.bat first.", file=sys.stderr)
        return 1

    if not cfg.client_data.is_dir():
        print(f"[shard] UO_CLIENT_DATA does not exist: {cfg.client_data}", file=sys.stderr)
        return 1

    target = cfg.shard_dist / "Configuration"
    target.mkdir(parents=True, exist_ok=True)

    wrote = []
    main_cfg = target / "modernuo.json"

    if not main_cfg.exists():
        text = (TEMPLATES / "modernuo.template.json").read_text(encoding="utf-8")

        # The server reads this as JSON, so the path is escaped as JSON.
        text = text.replace("@UO_CLIENT_DATA@", json.dumps(str(cfg.client_data))[1:-1])
        text = text.replace("@UO_SHARD_NAME@", cfg.shard_name)
        text = text.replace("@UO_SHARD_PORT@", str(cfg.shard_port))

        # Parse it back before writing: a template that no longer produces
        # valid JSON should fail here rather than inside the server.
        json.loads(text)

        main_cfg.write_text(text, encoding="utf-8", newline="\n")
        wrote.append(main_cfg.name)

    expansion = target / "expansion.json"

    if not expansion.exists():
        expansion.write_text(
            (TEMPLATES / "expansion.json").read_text(encoding="utf-8"),
            encoding="utf-8",
            newline="\n",
        )
        wrote.append(expansion.name)

    if wrote:
        print(f"[shard] Wrote {', '.join(wrote)} to {target}")
    else:
        print(f"[shard] Configuration already present in {target}")

    return 0


if __name__ == "__main__":
    raise SystemExit(main())
