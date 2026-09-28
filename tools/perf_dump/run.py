r"""World prepare and world draw, ClassicUO against GUO, in the same scenes (Epic B, B4).

    python tools/perf_dump/run.py [--port 2598] [--size 2560,1440] [--zoom 2.5] [--build]
    python tools/perf_dump/run.py --report

Each client runs once per scene with GUO_PERF_DUMP set (see
godot/GUO/src/Bootstrap/PerfDump.cs, compiled into both). Once in the world,
an action queued on its game loop turns its own profiler on, sets the zoom
and says "[go X Y Z"; SECONDS later it writes the averages of its last 60
frames to build\perf_dump\<scene>\<client>.json, and this script ends it.
No keystrokes and no focus: ClassicUO logs in with -autologin, GUO with
--play, both as the shard owner (a GM) and the same character, with the same
profile (tools\ab_compare copies GUO's for ClassicUO).

The numbers compare only on one machine, at one window size and zoom. The
ClassicUO build is ab_compare's: out of tree, with tools\render_dump's
inject.targets, sources\ untouched.
"""
from __future__ import annotations

import argparse
import dataclasses
import importlib.util
import json
import os
import subprocess
import sys
import time
from pathlib import Path

HERE = Path(__file__).resolve().parent
sys.path.insert(0, str(HERE.parent))

from guo.config import Config, load_config  # noqa: E402

# tools/ab_compare/run.py, by path: this file is run.py too.
_spec = importlib.util.spec_from_file_location("ab_compare", HERE.parent / "ab_compare" / "run.py")
ab = importlib.util.module_from_spec(_spec)
sys.modules["ab_compare"] = ab  # its dataclasses look their module up
_spec.loader.exec_module(ab)

# The perf probe's world scenes (godot/GUO/src/Bootstrap/PerfProbe.cs); a z
# only where the probe gives one, else the shard puts the character on the ground.
SCENES = [
    ("open-field", 1164, 1668, None),
    ("britain-bank", 1434, 1699, 0),
    ("dense-forest", 633, 858, None),
    ("dungeon", 5401, 629, None),
]
KEYS = ["world_prepare_ms", "world_draw_ms", "render_frame_ms", "render_ui_ms", "update_world_ms"]


def dump_spec(out: Path, scene: tuple, zoom: float, seconds: int) -> str:
    name, x, y, z = scene
    return f"{out};{name};{x};{y};{'' if z is None else z};{zoom};{seconds}"


def wait_for(path: Path, proc: subprocess.Popen, timeout: float) -> bool:
    deadline = time.time() + timeout
    while time.time() < deadline:
        if path.exists():
            return True
        if proc.poll() is not None:
            return False
        time.sleep(1.0)
    return False


def end(proc: subprocess.Popen) -> None:
    proc.terminate()
    try:
        proc.wait(timeout=15)
    except subprocess.TimeoutExpired:
        proc.kill()


def run_guo(cfg: Config, out: Path, scene: tuple, size: str, spec: str, timeout: float) -> bool:
    log = out / scene[0] / "guo.log"
    log.parent.mkdir(parents=True, exist_ok=True)
    env = {**ab.guo_environment(cfg), "GUO_PERF_DUMP": spec}
    proc = subprocess.Popen(
        # --play logs in only with something scripted to do: one shard
        # command (the daylight ab_compare pins too), then --stay.
        [str(cfg.godot_console_exe), "--path", str(cfg.godot_project), "--", "--play", "--silent",
         "--window-size", size, "--account", cfg.shard_owner, "--password", cfg.shard_owner_password,
         "--shard-command", f"[globallight {ab.DAYLIGHT}", "--stay"],
        stdout=log.open("w", encoding="utf-8", errors="replace"), stderr=subprocess.STDOUT, env=env,
    )
    try:
        return wait_for(out / scene[0] / "guo.json", proc, timeout)
    finally:
        end(proc)


def run_cuo(cfg: Config, out: Path, scene: tuple, size: tuple[int, int], spec: str, timeout: float) -> bool:
    log = out / scene[0] / "cuo.log"
    log.parent.mkdir(parents=True, exist_ok=True)
    character = ab.last_played(cfg) or ""
    conf = ab.write_cuo_config(cfg, character, size)
    proc = subprocess.Popen(
        [str(cfg.upstream_exe), "-settings", str(conf / "settings.json"),
         "-username", cfg.shard_owner, "-password", cfg.shard_owner_password,
         "-ip", cfg.shard_host, "-port", str(cfg.shard_port), "-autologin", "true", "-skiploginscreen",
         "-lastcharactername", character, "-music", "false"],
        cwd=str(cfg.upstream_build),
        stdout=log.open("w", encoding="utf-8", errors="replace"), stderr=subprocess.STDOUT,
        env={**os.environ, "FNA_GRAPHICS_ENABLE_HIGHDPI": "1", "__COMPAT_LAYER": "HighDpiAware",
             "GUO_PERF_DUMP": spec},
    )
    try:
        return wait_for(out / scene[0] / "cuo.json", proc, timeout)
    finally:
        end(proc)


def report(out: Path) -> str:
    lines = ["| Scene | client | window | zoom | " + " | ".join(KEYS) + " | frames |",
             "|---|---|---|---:|" + "---:|" * len(KEYS) + "---:|"]
    for name, *_ in SCENES:
        for client in ("cuo", "guo"):
            f = out / name / f"{client}.json"
            if not f.exists():
                lines.append(f"| {name} | {client} | (no result) |" + " |" * (len(KEYS) + 2))
                continue
            r = json.loads(f.read_text(encoding="utf-8"))
            lines.append(f"| {name} | {client} | {r['window']} | {r['zoom']:.1f} | "
                         + " | ".join(str(r[k]) for k in KEYS) + f" | {r['frames_averaged']} |")
    return "\n".join(lines)


def main() -> int:
    ap = argparse.ArgumentParser(description=__doc__, formatter_class=argparse.RawDescriptionHelpFormatter)
    ap.add_argument("--port", type=int, help="shard port (default UO_SHARD_PORT)")
    ap.add_argument("--size", default="2560,1440", help="window size of both clients")
    ap.add_argument("--zoom", type=float, default=2.5, help="camera zoom, clamped by each client")
    ap.add_argument("--seconds", type=int, default=20, help="settle time after [go before the averages are read")
    ap.add_argument("--timeout", type=float, default=150, help="per client and scene")
    ap.add_argument("--build", action="store_true", help="build ClassicUO first (tools/ab_compare)")
    ap.add_argument("--only", choices=["cuo", "guo"], help="run one client")
    ap.add_argument("--report", action="store_true", help="print the table of results already written")
    args = ap.parse_args()

    cfg = load_config()
    if args.port:
        cfg = dataclasses.replace(cfg, shard_port=args.port)
    out = cfg.build / "perf_dump"
    if args.report:
        print(report(out))
        return 0

    size = tuple(int(v) for v in args.size.split(","))
    if args.build or not cfg.upstream_exe.exists():
        ab.build_cuo(cfg)

    failed = 0
    for scene in SCENES:
        spec = dump_spec(out, scene, args.zoom, args.seconds)
        for client in ("cuo", "guo"):
            if args.only and client != args.only:
                continue
            (out / scene[0] / f"{client}.json").unlink(missing_ok=True)
            ok = (run_cuo(cfg, out, scene, size, spec, args.timeout) if client == "cuo"
                  else run_guo(cfg, out, scene, args.size, spec, args.timeout))
            print(f"[perf_dump] {scene[0]} {client}: {'ok' if ok else 'NO RESULT (see its log)'}", flush=True)
            failed += not ok
    print(report(out))
    return 1 if failed else 0


if __name__ == "__main__":
    sys.exit(main())
