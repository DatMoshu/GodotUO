r"""Run several scripted clients at once, tiled across the screen, and report on each.

    launchers\dev\multi_client.bat                 the default 2x2
    launchers\dev\multi_client.bat --only session,sweep
    launchers\dev\multi_client.bat --list

Every check so far runs one client at a time, so a full pass is the sum of
its parts and a shard with more than one player on it is never seen. This
starts four clients in one go -- each in its own scripted mode, each on its
own character, each in its own quarter of the screen -- waits for all of them,
and writes one summary. It exits 0 only when every lane did.

THE LANES

  session     the ordinary playtest: log in, walk, gumps, drag, speak, trade
              (it starts its own second client, guomate, to trade with)
  effects     the effects probe: frame cost of a crowd of blended effects
  highlight   the mesh highlight check, which "[go"es away and back
  sweep       "[go" to a Britain street and take the picture the sweep takes

The session lane is the owner account (UO_SHARD_OWNER) on its last-played
character, exactly as playtest.bat does, so it stands wherever that character
was left -- somewhere with people, vendors and things to attack. The other
three each have their own account: the shard refuses a second character from
one account, and "[go" takes staff access, so the shard makes the accounts in
UO_SHARD_GM_ACCOUNTS on a headless boot with game master access (password =
name). Each lane's character is made on first use.

Sound is off in every scripted run; pass --sound through to hear one.

WHAT IT WRITES

  build\multi_client\<stamp>\<lane>.log     the client's output
  build\multi_client\<stamp>\<lane>.png     the frame it left behind
  build\multi_client\<stamp>\contact.png    the four frames in a 2x2 sheet
  build\multi_client\<stamp>\summary.md     the table printed at the end

Needs the dev shard running (launchers\shard\run.bat). The clients share the
real client home, as every scripted run does; only the window position and
size are pinned per lane, and neither is saved back.
"""

from __future__ import annotations

import argparse
import ctypes
import datetime as dt
import os
import re
import subprocess
import sys
import time
from dataclasses import dataclass, field
from pathlib import Path

sys.path.insert(0, str(Path(__file__).resolve().parents[1]))

from guo.config import Config, load_config  # noqa: E402


@dataclass(frozen=True)
class Lane:
    name: str
    what: str
    # Index into UO_SHARD_GM_ACCOUNTS, or None for the owner account on its
    # last-played character, as the ordinary playtest does. A lane on a GM
    # account plays a character named after it (letters only: the shard's
    # name check refuses digits), made on first use.
    account: int | None
    args: tuple[str, ...]
    # Lines in the client's output that say what happened, shown in the
    # summary when present.
    tell: tuple[str, ...] = ()


LANES: list[Lane] = [
    Lane(
        "session",
        "playtest: login, walk, gumps, drag, speech, trade",
        None,
        ("--input-probe",),
        tell=("input probe: ", "probe check: FAIL"),
    ),
    Lane(
        "effects",
        "effects probe: cost of 40 blended effects, in Minoc",
        0,
        ("--shard-command", "[go 2500 560", "--effects-probe", "40"),
        tell=("effects probe: ",),
    ),
    Lane(
        "highlight",
        "highlight probe: mesh highlight survives a chunk reload, on a Britain street",
        1,
        ("--shard-command", "[go 1602 1591", "--highlight-probe"),
        tell=("highlight probe: ",),
    ),
    Lane(
        "sweep",
        "britain-street: [go 1602 1591 and photograph it",
        2,
        ("--shard-command", "[globallight 0", "--shard-command", "[go 1602 1591"),
        tell=("shard command: ",),
    ),
]


@dataclass
class Result:
    lane: Lane
    code: int | None = None
    seconds: float = 0.0
    shot: Path | None = None
    said: list[str] = field(default_factory=list)
    timed_out: bool = False

    @property
    def ok(self) -> bool:
        return self.code == 0 and not self.timed_out


def guo_environment(cfg: Config) -> dict[str, str]:
    """The settings GUO reads from its environment, resolved the launcher's way."""
    return {
        **os.environ,
        "UO_CLIENT_DATA": str(cfg.client_data),
        "UO_CACHE_DIR": str(cfg.cache_dir),
        "UO_CLIENT_VERSION": cfg.client_version,
        "UO_SHARD_HOST": cfg.shard_host,
        "UO_SHARD_PORT": str(cfg.shard_port),
    }


def work_area() -> tuple[int, int, int, int]:
    """The primary monitor minus the taskbar, as left, top, width, height."""
    if os.name != "nt":
        return 0, 0, 2560, 1440

    class Rect(ctypes.Structure):
        _fields_ = [("left", ctypes.c_long), ("top", ctypes.c_long),
                    ("right", ctypes.c_long), ("bottom", ctypes.c_long)]

    ctypes.windll.shcore.SetProcessDpiAwareness(2) if hasattr(ctypes.windll, "shcore") else None
    rect = Rect()
    ctypes.windll.user32.SystemParametersInfoW(0x0030, 0, ctypes.byref(rect), 0)   # SPI_GETWORKAREA
    return rect.left, rect.top, rect.right - rect.left, rect.bottom - rect.top


def quadrants(n: int) -> list[tuple[int, int, int, int]]:
    """Left, top, width, height for n windows: a 2x2 for four, a row for fewer."""
    left, top, width, height = work_area()
    cols = 2 if n > 1 else 1
    rows = 2 if n > 2 else 1
    w, h = width // cols, height // rows
    return [(left + (i % cols) * w, top + (i // cols) * h, w, h) for i in range(n)]


def shard_up(cfg: Config) -> bool:
    import socket

    with socket.socket() as s:
        s.settimeout(1.0)
        return s.connect_ex((cfg.shard_host, cfg.shard_port)) == 0


def login_for(cfg: Config, lane: Lane) -> tuple[str, str, str | None]:
    """(account, password, character) for a lane; character None = last played."""
    if lane.account is None:
        return cfg.shard_owner, cfg.shard_owner_password, None
    if lane.account >= len(cfg.shard_gm_accounts):
        sys.exit(f"[multi_client] lane {lane.name} needs UO_SHARD_GM_ACCOUNTS[{lane.account}]; "
                 f"only {len(cfg.shard_gm_accounts)} configured")
    account = cfg.shard_gm_accounts[lane.account]
    return account, account, account.capitalize()


def start(cfg: Config, lane: Lane, at: tuple[int, int, int, int], out: Path) -> tuple[subprocess.Popen, Path]:
    log = out / f"{lane.name}.log"
    shot = out / f"{lane.name}.png"
    shot.unlink(missing_ok=True)
    account, password, character = login_for(cfg, lane)
    argv = [
        str(cfg.godot_console_exe),
        "--path", str(cfg.godot_project),
        "--",
        "--play",
        "--account", account,
        "--password", password,
        *(("--character", character) if character else ()),
        "--window-position", f"{at[0]},{at[1]}",
        "--window-size", f"{at[2]},{at[3]}",
        "--screenshot-dir", str(out),
        "--screenshot-name", lane.name,
        *lane.args,
    ]
    handle = open(log, "w", encoding="utf-8")
    proc = subprocess.Popen(argv, stdout=handle, stderr=subprocess.STDOUT, env=guo_environment(cfg))
    return proc, shot


def read_back(lane: Lane, log: Path) -> list[str]:
    said: list[str] = []
    for line in log.read_text(encoding="utf-8", errors="replace").splitlines():
        if "FATAL" in line or any(t in line for t in lane.tell):
            said.append(line.replace("[GUO] ", "").strip())
    # The session lane narrates every step; keep its verdict and its failures.
    if lane.name == "session":
        said = [s for s in said if "checks passed" in s or "FAIL" in s or "FATAL" in s]
    return said[-8:]


def contact_sheet(results: list[Result], path: Path) -> Path | None:
    try:
        from PIL import Image, ImageDraw
    except ImportError:
        return None

    shots = [(r, Image.open(r.shot).convert("RGB")) for r in results if r.shot and r.shot.exists()]
    if not shots:
        return None

    cell_w = max(im.width for _, im in shots)
    cell_h = max(im.height for _, im in shots)
    cols = 2 if len(shots) > 1 else 1
    rows = (len(shots) + cols - 1) // cols
    sheet = Image.new("RGB", (cols * cell_w, rows * cell_h + 0), (24, 24, 24))
    draw = ImageDraw.Draw(sheet)
    for i, (r, im) in enumerate(shots):
        x, y = (i % cols) * cell_w, (i // cols) * cell_h
        sheet.paste(im, (x, y))
        label = f"{r.lane.name}  {'OK' if r.ok else 'FAILED'}  {r.seconds:.0f}s"
        draw.rectangle([x, y, x + 8 + 7 * len(label), y + 18], fill=(0, 0, 0))
        draw.text((x + 4, y + 3), label, fill=(120, 255, 120) if r.ok else (255, 110, 110))
    sheet.save(path)
    return path


def summary(results: list[Result], out: Path, sheet: Path | None) -> str:
    lines = [
        f"# multi_client {out.name}", "",
        "| lane | result | time | says |", "|---|---|---:|---|",
    ]
    for r in results:
        verdict = "OK" if r.ok else ("TIMED OUT" if r.timed_out else f"FAILED ({r.code})")
        says = "<br>".join(r.said) if r.said else ""
        lines.append(f"| {r.lane.name} | {verdict} | {r.seconds:.0f}s | {says} |")
    lines += ["", f"Logs and frames: {out}"]
    if sheet:
        lines.append(f"Contact sheet: {sheet}")
    return "\n".join(lines)


def main() -> int:
    ap = argparse.ArgumentParser(description=__doc__, formatter_class=argparse.RawDescriptionHelpFormatter)
    ap.add_argument("--only", help="comma-separated lane names to run (default: all four)")
    ap.add_argument("--timeout", type=int, default=900, help="seconds before a lane is killed (default 900)")
    ap.add_argument("--stagger", type=float, default=3.0, help="seconds between client starts (default 3)")
    ap.add_argument("--list", action="store_true", help="print the lanes and exit")
    ns = ap.parse_args()

    if ns.list:
        for lane in LANES:
            account, _, character = login_for(load_config(), lane)
            print(f"{lane.name:10s} {account:12s} {character or '(last played)':14s} {lane.what}")
        return 0

    lanes = LANES
    if ns.only:
        wanted = [w.strip() for w in ns.only.split(",") if w.strip()]
        unknown = [w for w in wanted if w not in {l.name for l in LANES}]
        if unknown:
            sys.exit(f"[multi_client] unknown lane(s): {', '.join(unknown)}")
        lanes = [l for l in LANES if l.name in wanted]

    cfg = load_config()
    if not shard_up(cfg):
        sys.exit(f"[multi_client] no shard at {cfg.shard_host}:{cfg.shard_port}; start launchers\\shard\\run.bat first")

    out = cfg.build / "multi_client" / dt.datetime.now().strftime("%Y-%m-%d_%H%M%S")
    out.mkdir(parents=True, exist_ok=True)
    print(f"[multi_client] {len(lanes)} lanes on {cfg.shard_owner}@{cfg.shard_host}:{cfg.shard_port} -> {out}")

    running: list[tuple[Result, subprocess.Popen, float]] = []
    for lane, at in zip(lanes, quadrants(len(lanes))):
        proc, shot = start(cfg, lane, at, out)
        account = login_for(cfg, lane)[0]
        print(f"[multi_client] {lane.name:10s} pid {proc.pid:<6d} {account:12s} at {at[0]},{at[1]} {at[2]}x{at[3]}  {lane.what}")
        running.append((Result(lane, shot=shot), proc, time.monotonic()))
        time.sleep(ns.stagger)

    results: list[Result] = []
    pending = list(running)
    while pending:
        for item in list(pending):
            result, proc, began = item
            code = proc.poll()
            elapsed = time.monotonic() - began
            if code is None and elapsed > ns.timeout:
                proc.kill()
                result.timed_out = True
                code = proc.wait()
            if code is not None:
                result.code = code
                result.seconds = elapsed
                result.said = read_back(result.lane, out / f"{result.lane.name}.log")
                verdict = "OK" if result.ok else ("TIMED OUT" if result.timed_out else f"FAILED ({code})")
                print(f"[multi_client] {result.lane.name:10s} {verdict} after {elapsed:.0f}s")
                results.append(result)
                pending.remove(item)
        time.sleep(1.0)

    results.sort(key=lambda r: [l.name for l in lanes].index(r.lane.name))
    sheet = contact_sheet(results, out / "contact.png")
    text = summary(results, out, sheet)
    (out / "summary.md").write_text(text, encoding="utf-8")
    print()
    print(text)

    failed = [r.lane.name for r in results if not r.ok]
    print()
    print("[multi_client] " + ("OK" if not failed else "FAILED: " + ", ".join(failed)))
    return 0 if not failed else 1


if __name__ == "__main__":
    sys.exit(main())
