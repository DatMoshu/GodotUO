"""The new-player gate: one command, one exit code, for "a new player can play this build".

It makes up an account nobody has used, then runs the new-player scenarios in order with the scenario
runner (tools/scenario_run), each as its own client launch against the dev shard:

    client.newplayer.create   first login on an empty account, character creation, in the world, log out
    client.newplayer.return   the same account again: its character is listed, back in the world, log out
    client.newplayer.options  ticks two options in the Options gump, Apply; the gate then reads the saved profile
    client.newplayer.kept     a third start: the paperdoll left open at log-out is open again without a key press

All links share one client home made for the run (the runner's --home), so what one start saves the next one finds;
the folder is new each time, so the first link is still a new player's, and it is removed when the chain ends.

The chain stops at the first scenario that fails and exits with that scenario's exit code; 0 means all passed.
The shard must create accounts on first login (the dev shard does; tools/modernuo/config). The password is
generated per run, handed to the runner in its environment and never printed. Runs keep stills and events;
pass --record for the video as well (needs ffmpeg). Other options go to the scenario runner unchanged.

    python tools/newplayer_gate/run.py                 run the chain
    python tools/newplayer_gate/run.py --list          print the chain and stop
    python tools/newplayer_gate/run.py --only create   run one link (return needs --account of an earlier run)
"""

from __future__ import annotations

import argparse
import os
import secrets
import shutil
import tempfile
import subprocess
import sys
import time
from pathlib import Path

ROOT = Path(__file__).resolve().parents[2]
RUNNER = ROOT / "tools" / "scenario_run" / "run.py"

# (short name, scenario id). Order matters: each link may rely on what the links before it left on the shard.
CHAIN = [
    ("create", "client.newplayer.create"),
    ("return", "client.newplayer.return"),
    ("options", "client.newplayer.options"),
    ("kept", "client.newplayer.kept"),
]

# Options the options link ticks in the Options gump (profile.json keys) and that must be saved and still set after the next start.
EXPECTED_OPTIONS = {"always_run": True, "fast_rotation": True}
AFTER_LINKS = ("options", "kept")   # the gate reads the saved profile after these links

MIN_FREE_GB = 10.0   # a client plus its recording; below this the run is refused rather than reaped half way


def new_account(now: float | None = None, token: str | None = None) -> str:
    """An account name no earlier run used: 'np' + minutes since 2026 in base 36 + two random characters."""
    minutes = int(((now if now is not None else time.time()) - 1767225600) // 60)
    digits = "0123456789abcdefghijklmnopqrstuvwxyz"
    out = ""
    while minutes > 0:
        minutes, r = divmod(minutes, 36)
        out = digits[r] + out
    return "np" + (out or "0") + (token if token is not None else secrets.token_hex(1))


def character_name(account: str) -> str:
    """A name the shard accepts (letters only) that the return link can find on the character list."""
    letters = "".join(chr(ord("a") + (ord(c) % 26)) for c in account[2:])
    return ("Guo" + letters)[:12].capitalize()


def free_memory_gb() -> float | None:
    """Free physical memory, or None where it cannot be read cheaply (the check is then skipped)."""
    if sys.platform != "win32":
        return None
    import ctypes

    class Status(ctypes.Structure):
        _fields_ = [("length", ctypes.c_ulong), ("load", ctypes.c_ulong), ("total", ctypes.c_ulonglong),
                    ("avail", ctypes.c_ulonglong), ("a", ctypes.c_ulonglong), ("b", ctypes.c_ulonglong),
                    ("c", ctypes.c_ulonglong), ("d", ctypes.c_ulonglong), ("e", ctypes.c_ulonglong)]

    status = Status()
    status.length = ctypes.sizeof(Status)
    if not ctypes.windll.kernel32.GlobalMemoryStatusEx(ctypes.byref(status)):
        return None
    return status.avail / (1 << 30)


def check_saved_options(home: Path, expected: dict = EXPECTED_OPTIONS) -> str | None:
    """None when every saved character profile under the client home holds the expected option values, else why not."""
    import json
    files = sorted(Path(home).glob("Data/Profiles/*/*/*/profile.json"))
    if not files:
        return "no saved character profile under the client home"
    for f in files:
        try:
            data = json.loads(f.read_text(encoding="utf-8"))
        except (OSError, ValueError) as ex:
            return f"profile unreadable: {ex}"
        bad = [k for k, v in expected.items() if data.get(k) != v]
        if bad:
            return f"saved profile lacks {', '.join(bad)}"
    return None


def select(only: list[str]) -> list[tuple[str, str]]:
    if not only:
        return list(CHAIN)
    known = {name for name, _ in CHAIN}
    bad = [o for o in only if o not in known]
    if bad:
        raise SystemExit(f"newplayer_gate: no link named {bad[0]} (links: {', '.join(n for n, _ in CHAIN)})")
    return [link for link in CHAIN if link[0] in only]


def run_chain(links, account: str, charname: str, env: dict, extra: list[str], run=subprocess.call, out=print, home: Path | None = None) -> int:
    """Run each link; stop at the first non-zero exit and return it."""
    results = []
    code = 0
    for name, scenario in links:
        out(f"[newplayer] {name}: {scenario}")
        started = time.time()
        code = run([sys.executable, str(RUNNER), scenario, "--var", f"account={account}", "--var", f"charname={charname}", *extra],
                   env=env, cwd=str(ROOT))
        if code == 0 and home is not None and name in AFTER_LINKS:
            why = check_saved_options(home)
            if why:
                out(f"[newplayer] {name}: options not kept: {why}")
                code = 1
        results.append((name, code, time.time() - started))
        if code != 0:
            break
    for name, c, seconds in results:
        out(f"[newplayer]   {'ok  ' if c == 0 else 'FAIL'} {name:<8} exit {c}  {seconds:5.0f}s")
    for name, _ in links[len(results):]:
        out(f"[newplayer]   skip {name:<8} (an earlier link failed)")
    out(f"[newplayer] {'PASS' if code == 0 else 'FAIL'}: {sum(1 for _, c, _ in results if c == 0)} of {len(links)} links, account {account}")
    return code


def main(argv=None) -> int:
    ap = argparse.ArgumentParser(description="Run the new-player scenarios in order; exit 0 only if all pass.")
    ap.add_argument("--list", action="store_true", help="print the chain and stop")
    ap.add_argument("--only", action="append", default=[], help="run only this link (repeatable)")
    ap.add_argument("--account", help="use this account instead of a new one (for --only return)")
    ap.add_argument("--min-free-gb", type=float, default=MIN_FREE_GB, help="refuse to start with less free memory (0 = no check)")
    args, extra = ap.parse_known_args(argv)
    links = select(args.only)
    if args.list:
        for name, scenario in links:
            print(f"{name:<8} {scenario}")
        return 0
    free = free_memory_gb()
    if args.min_free_gb > 0 and free is not None and free < args.min_free_gb:
        print(f"newplayer_gate: {free:.1f} GB of memory free, {args.min_free_gb:.0f} GB needed to run a client; nothing was started "
              f"(close something, or pass --min-free-gb 0)")
        return 3
    if "--record" not in extra and "--no-record" not in extra:
        extra = ["--no-record", *extra]   # a gate must not need ffmpeg; pass --record for the video
    account = args.account or new_account()
    env = dict(os.environ)
    if not args.account:
        env["GUO_SCENARIO_PASSWORD"] = secrets.token_urlsafe(9)   # a new account: the shard keeps whatever it is first given
    elif not env.get("GUO_SCENARIO_PASSWORD"):
        print("newplayer_gate: --account needs that account's password in GUO_SCENARIO_PASSWORD")
        return 2
    if "--home" in extra:
        return run_chain(links, account, character_name(account), env, extra)
    home = Path(tempfile.mkdtemp(prefix="guo_newplayer_home_"))   # may hold a copy of saved settings: never left behind
    try:
        return run_chain(links, account, character_name(account), env, [*extra, "--home", str(home)], home=home)
    finally:
        shutil.rmtree(home, ignore_errors=True)


if __name__ == "__main__":
    sys.exit(main())
