"""A staff character online on the private instance, for the god view's actions (AD2b).

Go there, Bring here, Open paperdoll and Follow act with the admin's own
logged-in staff character, so their checks need one in the world. This starts
a headless GUO client on the private instance as the third game master lane
account (UO_SHARD_GM_ACCOUNTS, "guosweep" by default: the shard gives those
accounts GameMaster; their password is UO_SHARD_GM_PASSWORD, generated per
user and never printed) and waits until it stands at a known spot on Felucca.
Its character is named after the account, so no player's name reaches the
evidence.

    with StaffClient(cfg, shard_port, out_dir) as c:   # c.character -> "Guosweep"
        ...
"""

from __future__ import annotations

import subprocess
import time
from pathlib import Path

from guo.process import build_child_env, no_activate

# Where the character starts: west of Britain, beside the god view checks' test spawner.
START = (1166, 1666)


class StaffClient:
    def __init__(self, cfg, shard_port: int, out: Path, account_index: int = 2):
        accounts = cfg.shard_gm_accounts
        if len(accounts) <= account_index or not cfg.shard_gm_password:
            raise RuntimeError("no game master lane account or password configured "
                               "(UO_SHARD_GM_ACCOUNTS, UO_SHARD_GM_PASSWORD; launchers\\shard\\run.bat makes the password)")
        self.cfg = cfg
        self.account = accounts[account_index]
        self.character = self.account.capitalize()
        self.port = shard_port
        self.out = out
        self.proc: subprocess.Popen | None = None
        self.log = out / "staff_client.log"

    def __enter__(self) -> "StaffClient":
        self.start()
        return self

    def __exit__(self, *exc) -> None:
        self.stop()

    def start(self, timeout: float = 240.0) -> None:
        cfg = self.cfg
        home = self.out / "staff_client_home"
        (home / "cache").mkdir(parents=True, exist_ok=True)
        watch = self.out / "staff_client_watch"
        watch.mkdir(parents=True, exist_ok=True)
        for f in watch.iterdir():
            f.unlink()
        cmd = [str(cfg.godot_console_exe), "--headless", "--path", str(cfg.godot_project), "--", "--play",
               "--account", self.account, "--password", cfg.shard_gm_password, "--character", self.character,
               # The login gump's password box holds 16 characters (as upstream) and the
               # generated passwords are longer, so a typed login is cut short: hand it over.
               "--autologin",
               "--objects-watch", str(watch),
               "--shard-command", "[self set map felucca", "--shard-command", f"[go {START[0]} {START[1]}"]
        env = build_child_env()
        env.update({"UO_CLIENT_DATA": str(cfg.client_data), "UO_CACHE_DIR": str(home / "cache"),
                    "UO_CLIENT_VERSION": cfg.client_version, "UO_SHARD_HOST": "127.0.0.1", "UO_SHARD_PORT": str(self.port)})
        self.proc = subprocess.Popen(cmd, stdout=self.log.open("w", encoding="utf-8", errors="replace"),
                                     stderr=subprocess.STDOUT, env=env, **no_activate())
        end = time.monotonic() + timeout
        while time.monotonic() < end:
            if self.proc.poll() is not None:
                raise RuntimeError(f"the staff client exited ({self.proc.returncode}); see {self.log}")
            if (watch / "watching").exists():
                # The [go commands follow the login; give them a moment to land.
                time.sleep(3)
                return
            time.sleep(0.5)
        self.stop()
        raise RuntimeError(f"the staff client never reached the world within {timeout:.0f} s; see {self.log}")

    def stop(self) -> None:
        if self.proc and self.proc.poll() is None:
            self.proc.kill()
            try:
                self.proc.wait(timeout=10)
            except subprocess.TimeoutExpired:
                pass
        self.proc = None
