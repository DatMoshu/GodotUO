"""Write the dev shard's configuration from the tracked templates.

ModernUO asks for its data directory, its listeners and its expansion at a
console prompt on first run, and refuses to prompt when stdin is redirected --
which is every scripted or agent-driven run. Writing the files up front is how
the shard boots unattended.

Existing files are left alone, except for the listener: the shard binds
UO_SHARD_BIND (loopback unless set otherwise on purpose), and that one setting
is put back in an existing modernuo.json at every run, so an older file that
listened on every address is closed too. Delete a file to have it rebuilt, or
edit it in place when another setting should differ on this machine only.

The owner and game master passwords are generated here on first run into the
per-user secrets file (tools/guo/shard_secrets.py); nothing ships a default.

`launchers\\shard\\run.bat` calls this. Run directly it reads the same settings
from config.bat, as every tool here does.
"""

from __future__ import annotations

import ipaddress
import json
import sys
from pathlib import Path

sys.path.insert(0, str(Path(__file__).resolve().parents[1]))

from guo import shard_secrets  # noqa: E402
from guo.config import load_config  # noqa: E402

TEMPLATES = Path(__file__).resolve().parent / "config"


def listener(bind: str, port: int) -> str:
    """ModernUO's "address:port" for UO_SHARD_BIND; an address, never a host name."""
    try:
        address = ipaddress.ip_address(bind.strip().strip("[]"))
    except ValueError:
        raise SystemExit(f"[shard] UO_SHARD_BIND must be an IP address (127.0.0.1, 0.0.0.0, ...), not {bind!r}")
    return f"[{address}]:{port}" if address.version == 6 else f"{address}:{port}"


def ensure_listener(main_cfg: Path, wanted: str) -> bool:
    """Put the configured listener in an existing modernuo.json; True when it changed."""
    data = json.loads(main_cfg.read_text(encoding="utf-8"))
    if data.get("listeners") == [wanted]:
        return False
    data["listeners"] = [wanted]
    main_cfg.write_text(json.dumps(data, indent=2) + "\n", encoding="utf-8", newline="\n")
    return True


def ensure_passwords(cfg) -> None:
    """Generate the passwords nothing else sets, into the workspace secrets file."""
    path, added = shard_secrets.ensure(cfg.workspace_dir)
    # A value from the environment or config.local.bat wins over the file;
    # only say "generated" for what the shard will actually use.
    configured = {shard_secrets.OWNER_KEY: cfg.shard_owner_password, shard_secrets.GM_KEY: cfg.shard_gm_password,
                  shard_secrets.ADMIN_TOKEN_KEY: cfg.bridge_admin_token}
    used = [key for key in added if not configured[key]]
    if used:
        print(f"[shard] Generated {', '.join(used)} into {path}")
    else:
        print(f"[shard] Passwords: {path}")


def main() -> int:
    return configure(load_config())


def configure(cfg) -> int:
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
    wanted = listener(cfg.shard_bind, cfg.shard_port)

    if not main_cfg.exists():
        text = (TEMPLATES / "modernuo.template.json").read_text(encoding="utf-8")

        # The server reads this as JSON, so the path is escaped as JSON.
        text = text.replace("@UO_CLIENT_DATA@", json.dumps(str(cfg.client_data))[1:-1])
        text = text.replace("@UO_SHARD_NAME@", cfg.shard_name)
        text = text.replace("@UO_SHARD_LISTENER@", wanted)

        # Parse it back before writing: a template that no longer produces
        # valid JSON should fail here rather than inside the server.
        json.loads(text)

        main_cfg.write_text(text, encoding="utf-8", newline="\n")
        wrote.append(main_cfg.name)
    elif ensure_listener(main_cfg, wanted):
        print(f"[shard] {main_cfg.name}: listeners set to {wanted} (UO_SHARD_BIND)")

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

    ensure_passwords(cfg)
    if not wanted.startswith(("127.", "[::1]")):
        print(f"[shard] Listening on {wanted}: reachable from other machines (UO_SHARD_BIND).")

    return 0


if __name__ == "__main__":
    raise SystemExit(main())
