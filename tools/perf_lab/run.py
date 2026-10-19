#!/usr/bin/env python3
"""The perf lab: GUO's frame rate on your machine, as a table and a report you can share.

    python tools/perf_lab/run.py [--preset 1080p --preset 1440p-far] [--size W,H --zoom Z]
                                 [--scene britain-bank ...] [--label NAME] [--as-built] [--port PORT]
    python tools/perf_lab/run.py --doctor
    python tools/perf_lab/run.py --report build/perf/perf_A.json ...   (re-report earlier probe runs)

For a player, not a developer: checks what the run needs (the doctor), builds
GUO optimised once, then runs tools/perf_probe at each window size and zoom
(by default 1920x1080 at zoom 1.0 and 2560x1440 at zoom 2.5, the camera's
widest), with vsync, the frame cap and upstream's draw pacing off. Prints the
FPS and frame-time table and writes build/perf/perf_lab_<UTC time>.json and
.md. The JSON is the one to paste or attach in Discord: GPU, driver, CPU and OS
fields and the numbers per scene, and nothing else -- no paths and no account
or computer names (the report is checked for them before it is written).

It needs a running shard where the probe account is a GM: the local dev shard
(launchers\\shard\\run.bat) is one. The doctor says what is missing.
"""
from __future__ import annotations

import argparse
import datetime as dt
import getpass
import json
import os
import platform
import re
import shutil
import socket
import subprocess
import sys
from pathlib import Path

sys.path.insert(0, str(Path(__file__).resolve().parents[1]))

from guo import load_config  # noqa: E402

FORMAT = "guo-perf-lab/1"
PRESETS = {
    "1080p": ("1920,1080", 1.0),
    "1440p-far": ("2560,1440", 2.5),
}
DEFAULT_PRESETS = ("1080p", "1440p-far")
# The fields a reader needs to compare two machines; the probe's own JSON has
# forty more, kept in build/perf for whoever wants them.
SCENE_FIELDS = ("scene", "fps", "mean_ms", "p95_ms", "p99_ms", "draw_calls", "batcher_items",
                "texture_switches", "world_prepare_ms", "world_draw_ms", "render_gpu_ms")
SYSTEM_FIELDS = ("gpu", "gpu_vendor", "gpu_api", "driver", "driver_version", "rendering_method", "cpu",
                 "cpu_threads", "ram_gib", "os", "os_version", "screen", "refresh_hz", "engine")
DOCTOR = r"launchers\dev\perf_lab_doctor.bat"
ME = r"launchers\dev\perf_lab.bat"


# --- the doctor -------------------------------------------------------------

LOCAL_HOSTS = {"127.0.0.1", "localhost", "::1"}
LISTEN_PORT = re.compile(r"[:.](\d{1,5})\s")


def listening_ports() -> set[int] | None:
    """The TCP ports something listens on here, from the OS's own listing; None if it cannot say.

    Never by connecting: the dev shard's AutoDenylist bans 127.0.0.1 for 15
    minutes after a connection that sends nothing, so a bare connect to see
    whether it is up locks every client on this machine out of it.
    """
    if sys.platform == "win32":
        cmd = ["netstat", "-ano", "-p", "TCP"]
    elif sys.platform == "darwin":
        cmd = ["netstat", "-anv", "-p", "tcp"]
    else:
        cmd = ["ss", "-Hltn"]
    try:
        out = subprocess.run(cmd, capture_output=True, text=True, timeout=10).stdout
    except (OSError, subprocess.SubprocessError):
        return None
    return parse_listening(out)


def parse_listening(text: str) -> set[int]:
    """Ports in the LISTEN(ING) lines of netstat or ss output: the first address on the line is the local one."""
    ports = set()
    for line in text.splitlines():
        if "LISTEN" not in line:
            continue
        m = LISTEN_PORT.search(line + " ")
        if m:
            ports.add(int(m.group(1)))
    return ports


def shard_up(host: str, port: int) -> bool | None:
    """Whether something listens on the shard's port here; None when that cannot be told without connecting."""
    if host not in LOCAL_HOSTS:
        return None
    ports = listening_ports()
    return None if ports is None else port in ports


def doctor(cfg, port: int | None = None) -> list[tuple[bool, str, str]]:
    """(ok, what, the line to print): each thing a perf lab run needs."""
    port = port or cfg.shard_port
    checks = []
    dotnet = shutil.which("dotnet")
    checks.append((dotnet is not None, ".NET SDK",
                   "found" if dotnet else "not found: install the .NET 8 SDK (https://dotnet.microsoft.com/download), "
                                          "then open a new terminal"))
    exe = Path(cfg.godot_console_exe)
    checks.append((exe.is_file(), "Godot",
                   "found" if exe.is_file() else r"not found: run launchers\pipeline\00_bootstrap.bat once"))
    data = Path(cfg.client_data) if cfg.client_data else None
    ok = data is not None and data.is_dir()
    checks.append((ok, "UO client data",
                   "found" if ok else r"not set or missing: set UO_CLIENT_DATA in launchers\_shared\config.local.bat "
                                      "to your Ultima Online folder"))
    up = shard_up(cfg.shard_host, port)
    checks.append((up is not False, "shard",
                   f"listening on {cfg.shard_host}:{port}" if up else
                   f"not checked on {cfg.shard_host}:{port} (only a local shard can be checked without connecting); "
                   "the run's login says whether it answers" if up is None else
                   f"nothing listens on {cfg.shard_host}:{port}. The perf lab plays on a shard where its account "
                   r"is a GM: start the local dev shard with launchers\shard\run.bat and leave it running "
                   r"(first time: launchers\shard\fetch.bat, launchers\shard\build.bat; see the wiki page Dev-Shard)"))
    return checks


def print_doctor(checks) -> bool:
    for ok, what, line in checks:
        print(f"[perf_lab] {'ok  ' if ok else 'FAIL'} {what:15} {line}")
    return all(ok for ok, _, _ in checks)


# --- the report -------------------------------------------------------------

def forbidden_words(cfg=None) -> set[str]:
    """Words a shared report must not carry: who and where this machine is, and the shard's accounts."""
    words = set()
    accounts = (cfg.shard_owner, *cfg.shard_gm_accounts) if cfg is not None else ()
    for v in (os.environ.get("USERNAME"), os.environ.get("USER"), os.environ.get("COMPUTERNAME"),
              os.environ.get("UO_SHARD_OWNER"), platform.node(), *accounts):
        if v and len(v) >= 3:
            words.add(v.lower())
    try:
        words.add(getpass.getuser().lower())
    except Exception:  # noqa: BLE001 - no login name is fine
        pass
    words.add(str(Path.home()).lower())
    for gm in (os.environ.get("UO_SHARD_GM_ACCOUNTS") or "").split(","):
        if len(gm.strip()) >= 3:
            words.add(gm.strip().lower())
    words.add("guoprobe")
    return {w for w in words if w}


PATHLIKE = re.compile(r"[A-Za-z]:[\\/]|\\\\|(^|\s)/(home|Users|mnt|srv)/")


def privacy_problems(report, words: set[str]) -> list[str]:
    """Every string in the report that looks like a path or names someone."""
    found = []

    def walk(v, where):
        if isinstance(v, dict):
            for k, x in v.items():
                walk(x, f"{where}.{k}")
        elif isinstance(v, list):
            for i, x in enumerate(v):
                walk(x, f"{where}[{i}]")
        elif isinstance(v, str):
            low = v.lower()
            if PATHLIKE.search(v):
                found.append(f"{where}: looks like a path")
            for w in words:
                if re.search(rf"(?<![a-z0-9]){re.escape(w)}(?![a-z0-9])", low):
                    found.append(f"{where}: carries a local name")
                    break

    walk(report, "report")
    return found


def guo_version(root: Path) -> str:
    try:
        r = subprocess.run(["git", "-C", str(root), "describe", "--always", "--dirty", "--exclude", "*"],
                           capture_output=True, text=True, timeout=10)
        return r.stdout.strip() or "unknown"
    except (OSError, subprocess.SubprocessError):
        return "unknown"


def build_report(probes: list[tuple[str, str, dict]], version: str, when: dt.datetime) -> dict:
    """probes: (preset, asked window, probe JSON) per run, in order."""
    system = {}
    runs = []
    for preset, asked, probe in probes:
        system = system or {k: probe.get("system", {}).get(k, "") for k in SYSTEM_FIELDS}
        runs.append({
            "preset": preset,
            "window_asked": asked.replace(",", "x"),
            "window": probe.get("window", ""),
            "zoom": probe.get("zoom", 0),
            "optimized": probe.get("optimized", False),
            "postfx": probe.get("postfx", ""),
            # Columns once and a row per scene: short enough to paste in a
            # Discord message, which takes 2,000 characters.
            "columns": list(SCENE_FIELDS),
            "rows": [[s.get(k) for k in SCENE_FIELDS] for s in probe.get("scenes", [])],
        })
    return {"format": FORMAT, "when": when.strftime("%Y-%m-%dT%H:%MZ"), "guo": version, "system": system, "runs": runs}


def scenes(run: dict) -> list[dict]:
    """A run's rows as one dict per scene."""
    return [dict(zip(run["columns"], row)) for row in run["rows"]]


def window_note(run: dict, screen: str) -> str:
    """Why the window is not the size asked for, when it is not."""
    if run["window"] == run["window_asked"]:
        return ""

    def area(v):
        try:
            w, h = (int(x) for x in v.split("x"))
            return w, h
        except ValueError:
            return 0, 0

    (aw, ah), (sw, sh) = area(run["window_asked"]), area(screen)
    if sw and (aw > sw or ah > sh):
        return f" (asked {run['window_asked']}, larger than the screen)"
    return f" (asked {run['window_asked']}; the world scenes did not run)" if not any(
        sc.get("scene") != "login" for sc in scenes(run)) else f" (asked {run['window_asked']})"


def table(report: dict) -> str:
    s = report["system"]
    lines = [f"# GUO perf lab, {report['when']}", "",
             f"GUO {report['guo']}; {s.get('gpu', '?')} (driver {s.get('driver_version', '?')}, {s.get('rendering_method', '?')}); "
             f"{s.get('cpu', '?')}, {s.get('cpu_threads', '?')} threads, {s.get('ram_gib', '?')} GiB; "
             f"{s.get('os', '?')} {s.get('os_version', '')}; screen {s.get('screen', '?')} at {s.get('refresh_hz', '?')} Hz."]
    for run in report["runs"]:
        lines += ["", f"## {run['preset']}: window {run['window']}, zoom {run['zoom']}"
                  + window_note(run, s.get("screen", ""))
                  + ("" if run["optimized"] else ", UNOPTIMISED build"),
                  "", "| Scene | FPS | mean ms | p95 ms | p99 ms | draw calls | batcher items | world prepare ms | world draw ms | GPU ms |",
                  "|---|---:|---:|---:|---:|---:|---:|---:|---:|---:|"]
        for sc in scenes(run):
            lines.append(f"| {sc.get('scene')} | {sc.get('fps')} | {sc.get('mean_ms')} | {sc.get('p95_ms')} | {sc.get('p99_ms')} | "
                         f"{sc.get('draw_calls')} | {sc.get('batcher_items')} | {sc.get('world_prepare_ms')} | "
                         f"{sc.get('world_draw_ms')} | {sc.get('render_gpu_ms')} |")
    lines += ["", "FPS is 1000 / mean ms with vsync and the frame cap off; p95/p99 are the slow frames. "
              "World prepare and draw are GUO's own CPU time; GPU ms is the graphics card's."]
    return "\n".join(lines) + "\n"


def write_report(out: Path, report: dict, words: set[str]) -> Path:
    problems = privacy_problems(report, words)
    if problems:
        raise SystemExit("[perf_lab] the report would carry something local, so it was not written:\n  "
                         + "\n  ".join(problems))
    stem = out / f"perf_lab_{report['when'].replace(':', '').replace('-', '')}"
    stem.with_suffix(".json").write_text(json.dumps(report, separators=(",", ":")) + "\n", encoding="utf-8")
    stem.with_suffix(".md").write_text(table(report), encoding="utf-8")
    return stem.with_suffix(".json")


# --- the run ----------------------------------------------------------------

def main(argv=None) -> int:
    ap = argparse.ArgumentParser(description=__doc__.splitlines()[0])
    ap.add_argument("--doctor", action="store_true", help="check what a run needs, measure nothing")
    ap.add_argument("--preset", action="append", choices=sorted(PRESETS), help="repeatable; default both")
    ap.add_argument("--size", help="a window size W,H instead of the presets (with --zoom)")
    ap.add_argument("--zoom", type=float, default=1.0, help="camera zoom with --size")
    ap.add_argument("--scene", action="append", default=[], help="these probe scenes only (repeatable)")
    ap.add_argument("--label", default="lab", help="prefix for the probe's own files in build/perf")
    ap.add_argument("--port", type=int, help="shard port (default UO_SHARD_PORT)")
    ap.add_argument("--args", default="", help="extra client flags, passed to the probe")
    ap.add_argument("--as-built", action="store_true", help="skip the optimised rebuild")
    ap.add_argument("--report", nargs="+", type=Path, metavar="PROBE_JSON",
                    help="write the shareable report from probe JSON files already measured")
    args = ap.parse_args(argv)
    cfg = load_config()
    out = cfg.build / "perf"
    out.mkdir(parents=True, exist_ok=True)
    now = dt.datetime.now(dt.timezone.utc)

    if args.report:
        probes = [(p.stem.removeprefix("perf_"), json.loads(p.read_text(encoding="utf-8")).get("window", "").replace("x", ","),
                   json.loads(p.read_text(encoding="utf-8"))) for p in args.report]
        path = write_report(out, build_report(probes, guo_version(cfg.root), now), forbidden_words(cfg))
        print(path.with_suffix(".md").read_text(encoding="utf-8"))
        print(f"[perf_lab] share this file: {path}")
        return 0

    ready = print_doctor(doctor(cfg, args.port))
    if args.doctor:
        return 0 if ready else 1
    if not ready:
        print(f"[perf_lab] not ready: fix the FAIL lines above, then run {ME} again ({DOCTOR} checks without measuring).")
        return 2

    if args.size:
        runs = [(f"{args.size.replace(',', 'x')}-z{args.zoom:g}", args.size, args.zoom)]
    else:
        runs = [(p, *PRESETS[p]) for p in (args.preset or DEFAULT_PRESETS)]

    if not args.as_built:
        from guo.build import build_client
        build_client(cfg)

    probe = Path(__file__).resolve().parents[1] / "perf_probe" / "run.py"
    probes, failed = [], []
    for name, size, zoom in runs:
        label = f"{args.label}_{name}".replace(".", "_")
        extra = " ".join([args.args, *(f"--perf-scene {s}" for s in args.scene)]).strip()
        cmd = [sys.executable, str(probe), "--as-built", "--label", label, "--size", size, "--zoom", str(zoom),
               *(["--port", str(args.port)] if args.port else []), *(["--args", extra] if extra else [])]
        print(f"[perf_lab] {name}: window {size.replace(',', 'x')}, zoom {zoom:g} (a minute or two; the window stays in the background)",
              flush=True)
        r = subprocess.run(cmd, stdout=subprocess.DEVNULL)
        js = out / f"perf_{label}.json"
        if r.returncode != 0 or not js.exists():
            failed.append(name)
            print(f"[perf_lab] {name}: the probe failed (exit {r.returncode}); its log is build/perf/perf_{label}.log. "
                  f"Most often the shard is not running or the probe account is not a GM there: {DOCTOR}")
            if not js.exists():
                continue
        probes.append((name, size, json.loads(js.read_text(encoding="utf-8"))))

    if not probes:
        return 1
    path = write_report(out, build_report(probes, guo_version(cfg.root), now), forbidden_words(cfg))
    print()
    print(path.with_suffix(".md").read_text(encoding="utf-8"))
    print(f"[perf_lab] share this file in Discord (paste its text or attach it): {path}")
    return 1 if failed else 0


if __name__ == "__main__":
    sys.exit(main())
