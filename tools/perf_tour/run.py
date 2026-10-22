r"""The benchmark tour: GUO against ClassicUO in the same places, at the same window and zoom.

One command measures everything a frame-rate claim needs and writes one
report, so it can be repeated after every renderer change without thinking:

- GUO through tools/perf_probe (--perf-probe: 360 frames per scene, uncapped,
  vsync off) in each MODE: `plain` (what ClassicUO draws and nothing more:
  --perf-profile plain, --merged-land=off --merged-cover=off, no post-processing),
  `defaults` (GUO as shipped) and `uoplus-max` (realtime lights, every UO+
  feature at 100%, realistic water, HD filter, a post-processing look).
- ClassicUO through tools/perf_dump (GUO_PERF_DUMP compiled into the
  out-of-tree build): its own profiler's averages over 60 drawn frames at the
  same [go spots and zoom, with its draw pacing set to its 250 FPS cap.

Each PRESET is a window and zoom: `gate` 1280x720 at zoom 1.0 (the perf gate's
own), `community` 2560x1440 at zoom 1.0 (the community's reports), `far`
2560x1440 at zoom 2.5 (the camera's maximum, where the sorted world costs most).

    python tools/perf_tour/run.py [--label NAME] [--preset gate|community|far ...]
                                  [--mode plain|defaults|uoplus-max ...] [--no-cuo]
                                  [--scene NAME ...] [--as-built] [--out DIR] [--port PORT]
    python tools/perf_tour/run.py --report DIR          redraw the report from a finished tour

Output under DIR (default build/perf_tour/<label>): perf_probe's md/json per
GUO run, perf_dump's json per ClassicUO scene, and tour.json, tour.md and
tour.png, the one table a reader needs. Frame times compare only on one
machine; draw calls and batcher counts compare anywhere. Needs a shard where
the probe account is a GM; take the Godot runtime lease --benchmark --exclusive
first, nothing else may draw while it runs.
"""
from __future__ import annotations

import argparse
import dataclasses
import importlib.util
import json
import os
import shlex
import subprocess
import sys
import time
from datetime import datetime, timezone
from pathlib import Path

HERE = Path(__file__).resolve().parent
sys.path.insert(0, str(HERE.parent))

from guo.build import build_client  # noqa: E402
from guo.config import Config, load_config  # noqa: E402
from guo.process import no_activate  # noqa: E402

_spec = importlib.util.spec_from_file_location("ab_compare", HERE.parent / "ab_compare" / "run.py")
ab = importlib.util.module_from_spec(_spec)
sys.modules["ab_compare"] = ab
_spec.loader.exec_module(ab)

# The perf probe's world scenes (godot/GUO/src/Bootstrap/PerfProbe.cs), with
# the [go of each so ClassicUO stands in the same spot. `walk` and `login`
# are GUO-only (ClassicUO is not scripted to run).
SCENES = [
    ("open-field", 1164, 1668, None),
    ("britain-bank", 1434, 1699, 0),
    ("dense-forest", 633, 858, None),
    ("dungeon", 5401, 629, None),
    ("britain-docks", 1499, 1761, -3),
    ("trinsic-bay", 2087, 2861, -3),
    ("cave-roof", 1010, 760, -60),
    ("open-sea", 3600, 1400, None),
]
GUO_ONLY = ("login", "walk")

PRESETS = {
    "gate": ("1280,720", 1.0),
    "community": ("2560,1440", 1.0),
    "far": ("2560,1440", 2.5),
}

# Mode -> the client flags that make it. --postfx off is perf_probe's default
# unless a look is named; the UO+ run names one so a full-screen pass is in it.
MODES = {
    "plain": "--perf-profile plain --merged-land=off --merged-cover=off",
    "defaults": "--perf-profile defaults",
    "uoplus-max": "--perf-profile uoplus-max --postfx glow",
}

CUO_FPS_CAP = 250


def utc_stamp() -> str:
    return datetime.now(timezone.utc).strftime("%Y%m%d-%H%M")


# --------------------------------------------------------------------------
# GUO: one perf_probe run per (preset, mode)
# --------------------------------------------------------------------------


def run_guo(cfg: Config, out: Path, label: str, preset: str, mode: str, scenes: list[str], port: int | None) -> dict | None:
    size, zoom = PRESETS[preset]
    run_label = f"{label}_{preset}_{mode}"
    args = MODES[mode]
    for s in scenes:
        args += f" --perf-scene {s}"
    cmd = [sys.executable, str(HERE.parent / "perf_probe" / "run.py"), "--label", run_label, "--size", size,
           "--zoom", str(zoom), "--as-built", "--args", args, *(["--port", str(port)] if port else [])]
    print(f"[tour] guo {preset} {mode}: {size} zoom {zoom}: {args}", flush=True)
    log = out / f"guo_{preset}_{mode}.log"
    r = subprocess.run(cmd, stdout=log.open("w", encoding="utf-8", errors="replace"), stderr=subprocess.STDOUT, timeout=2400)
    src = cfg.build / "perf" / f"perf_{run_label}.json"
    if r.returncode != 0 or not src.exists():
        print(f"[tour] guo {preset} {mode}: NO RESULT (exit {r.returncode}, see {log})", flush=True)
        return None
    data = json.loads(src.read_text(encoding="utf-8"))
    (out / f"guo_{preset}_{mode}.json").write_text(json.dumps(data, indent=2), encoding="utf-8")
    md = cfg.build / "perf" / f"perf_{run_label}.md"
    if md.exists():
        (out / f"guo_{preset}_{mode}.md").write_text(md.read_text(encoding="utf-8"), encoding="utf-8")
    return data


# --------------------------------------------------------------------------
# ClassicUO: one session per (preset, scene), timed by its own profiler
# --------------------------------------------------------------------------


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


def run_cuo(cfg: Config, out: Path, preset: str, scene: tuple, seconds: int, timeout: float) -> dict | None:
    size_s, zoom = PRESETS[preset]
    size = tuple(int(v) for v in size_s.split(","))
    name, x, y, z = scene
    folder = out / "cuo" / preset
    folder.mkdir(parents=True, exist_ok=True)
    spec = f"{folder};{name};{x};{y};{'' if z is None else z};{zoom};{seconds}"
    result = folder / name / "cuo.json"
    result.unlink(missing_ok=True)
    character = ab.last_played(cfg) or ""
    conf = ab.write_cuo_config(cfg, character, size, fps=CUO_FPS_CAP)
    log = folder / f"{name}.log"
    proc = subprocess.Popen(
        [str(cfg.upstream_exe), "-settings", str(conf / "settings.json"),
         "-username", cfg.shard_owner, "-password", cfg.shard_owner_password,
         "-ip", cfg.shard_host, "-port", str(cfg.shard_port), "-autologin", "true", "-skiploginscreen",
         "-lastcharactername", character, "-music", "false"],
        cwd=str(cfg.upstream_build),
        stdout=log.open("w", encoding="utf-8", errors="replace"), stderr=subprocess.STDOUT,
        env={**os.environ, "FNA_GRAPHICS_ENABLE_HIGHDPI": "1", "__COMPAT_LAYER": "HighDpiAware", "GUO_PERF_DUMP": spec},
        **no_activate(),
    )
    try:
        ok = wait_for(result, proc, timeout)
    finally:
        end(proc)
    print(f"[tour] cuo {preset} {name}: {'ok' if ok else 'NO RESULT (see its log)'}", flush=True)
    return json.loads(result.read_text(encoding="utf-8")) if ok else None


# --------------------------------------------------------------------------
# The report
# --------------------------------------------------------------------------


def fps(ms: float | None) -> str:
    return "" if not ms else f"{1000.0 / ms:.0f}"


def cuo_cpu_ms(r: dict | None) -> float | None:
    """The CPU frame ClassicUO's profiler saw: world and UI drawing plus the
    world update. Its wall frame is paced at the cap, so this is the number
    that compares with GUO's render frame, and 1000 over it is the frame
    rate ClassicUO could reach uncapped on this CPU (its GPU sits idle too)."""
    if not r:
        return None
    return round(float(r.get("render_frame_ms", 0)) + float(r.get("update_world_ms", 0)), 2)


def build_report(out: Path) -> dict:
    tour = json.loads((out / "tour.json").read_text(encoding="utf-8")) if (out / "tour.json").exists() else {}
    guo: dict[str, dict[str, dict]] = {}
    cuo: dict[str, dict[str, dict]] = {}
    for path in sorted(out.glob("guo_*_*.json")):
        _, preset, mode = path.stem.split("_", 2)
        data = json.loads(path.read_text(encoding="utf-8"))
        guo.setdefault(preset, {})[mode] = data
    for path in sorted(out.glob("cuo/*/*/cuo.json")):
        preset = path.parts[-3]
        cuo.setdefault(preset, {})[path.parts[-2]] = json.loads(path.read_text(encoding="utf-8"))

    lines = [f"# Benchmark tour: {tour.get('label', out.name)}", ""]
    sysinfo = next((d.get("system") for p in guo.values() for d in p.values() if d.get("system")), {})
    lines.append(f"{tour.get('started', '')} UTC. {sysinfo.get('gpu', '?')}, {sysinfo.get('cpu', '?')}, "
                 f"{sysinfo.get('os', '?')}. GUO head {tour.get('head', '?')}; "
                 f"ClassicUO pin {tour.get('cuo_pin', '?')}. GUO: 360 frames per scene, uncapped, vsync off, optimised build. "
                 f"ClassicUO: its profiler's last 60 drawn frames, draw pacing at its {CUO_FPS_CAP} FPS cap; "
                 f"'CUO cpu ms' is render frame + world update, the frame it could reach on this CPU.")
    lines.append("")
    for mode, flags in MODES.items():
        lines.append(f"- `{mode}`: `{flags}`")
    lines.append("")

    summary = {}
    for preset in PRESETS:
        if preset not in guo and preset not in cuo:
            continue
        size, zoom = PRESETS[preset]
        modes = [m for m in MODES if m in guo.get(preset, {})]
        profile_lines = [f"  - {m}: {guo[preset][m].get('profile', '')} ({guo[preset][m].get('postfx', '')})" for m in modes]
        lines.append(f"## {preset}: {size.replace(',', 'x')}, zoom {zoom}")
        lines.append("")
        lines.extend(profile_lines)
        lines.append("")
        head = "| Scene | CUO cpu ms | CUO fps* |"
        sep = "|---|---:|---:|"
        for m in modes:
            head += f" {m} ms | {m} fps | {m} p99 | {m} draw calls |"
            sep += "---:|---:|---:|---:|"
        lines.append(head)
        lines.append(sep)
        names = [s[0] for s in SCENES] + list(GUO_ONLY)
        rows = {}
        for name in names:
            c = cuo.get(preset, {}).get(name)
            cms = cuo_cpu_ms(c)
            row = {"cuo_cpu_ms": cms, "cuo_fps_ceiling": (1000.0 / cms) if cms else None,
                   "cuo_frame_ms": c.get("frame_ms") if c else None}
            text = f"| {name} | {'' if cms is None else f'{cms:.1f}'} | {fps(cms)} |"
            for m in modes:
                r = next((s for s in guo[preset][m].get("scenes", []) if s.get("scene") == name), None)
                if r:
                    text += f" {r['mean_ms']:.1f} | {fps(r['mean_ms'])} | {r['p99_ms']:.1f} | {r['draw_calls']:.0f} |"
                    row[m] = {k: r.get(k) for k in ("mean_ms", "p95_ms", "p99_ms", "draw_calls", "batcher_items",
                                                      "texture_switches", "world_draw_ms", "render_cpu_ms", "gpu_ms")}
                else:
                    text += " | | | |"
            if any(v is not None for v in row.values()):
                lines.append(text)
                rows[name] = row
        summary[preset] = rows
        lines.append("")
    lines.append("*CUO fps: 1000 / CUO cpu ms, the rate its CPU frame allows; its drawn frame is paced at the cap "
                 f"({CUO_FPS_CAP}), so it is a ceiling, not a measurement of presented frames.")
    text = "\n".join(lines) + "\n"
    (out / "tour.md").write_text(text, encoding="utf-8")
    tour["summary"] = summary
    (out / "tour.json").write_text(json.dumps(tour, indent=2), encoding="utf-8")
    try:
        draw_png(out, tour, guo, cuo)
    except Exception as e:  # noqa: BLE001
        print(f"[tour] no png: {e}")
    return tour


def draw_png(out: Path, tour: dict, guo: dict, cuo: dict) -> Path:
    """One image of the tour's FPS, for a chat card: a row per scene, a column
    per client and mode, one block per preset."""
    from PIL import Image, ImageDraw, ImageFont

    try:
        font = ImageFont.truetype("consola.ttf", 18)
        bold = ImageFont.truetype("consolab.ttf", 18)
    except OSError:
        font = bold = ImageFont.load_default()
    blocks = []
    for preset in PRESETS:
        if preset not in guo and preset not in cuo:
            continue
        size, zoom = PRESETS[preset]
        modes = [m for m in MODES if m in guo.get(preset, {})]
        header = ["scene", "CUO"] + [m for m in modes]
        rows = []
        for name in [s[0] for s in SCENES] + list(GUO_ONLY):
            cms = cuo_cpu_ms(cuo.get(preset, {}).get(name))
            cells = [name, fps(cms) or "-"]
            for m in modes:
                r = next((s for s in guo[preset][m].get("scenes", []) if s.get("scene") == name), None)
                cells.append(f"{fps(r['mean_ms'])} ({r['mean_ms']:.1f} ms)" if r else "-")
            if any(c != "-" for c in cells[1:]):
                rows.append(cells)
        blocks.append((f"{preset}: {size.replace(',', 'x')} zoom {zoom}  (FPS; GUO mean ms in brackets)", header, rows))

    col_w = [15, 8] + [20] * (max(len(b[1]) for b in blocks) - 2) if blocks else [15]
    char_w = 11
    line_h = 24
    title = f"GUO benchmark tour {tour.get('label', '')}  {tour.get('started', '')} UTC  head {tour.get('head', '')}"
    footer = "CUO = ClassicUO CPU-frame ceiling (render + update, draw paced at 250). Green >= 60, amber >= 30."
    longest = max([sum(col_w) + 2, len(title), len(footer)] + [len(b[0]) for b in blocks])
    width = longest * char_w + 40
    height = 50 + sum(len(b[2]) * line_h + 3 * line_h for b in blocks) + 40
    im = Image.new("RGB", (width, height), (24, 26, 30))
    d = ImageDraw.Draw(im)
    y = 16
    d.text((20, y), title, font=bold, fill=(235, 235, 235))
    y += 34
    for title, header, rows in blocks:
        d.text((20, y), title, font=bold, fill=(120, 200, 255))
        y += line_h
        x = 20
        for i, h in enumerate(header):
            d.text((x, y), h[: col_w[i] - 1], font=bold, fill=(200, 200, 200))
            x += col_w[i] * char_w
        y += line_h
        for cells in rows:
            x = 20
            for i, c in enumerate(cells):
                colour = (235, 235, 235)
                if i >= 2 and c != "-":
                    f = float(c.split(" ")[0])
                    colour = (120, 220, 120) if f >= 60 else (240, 200, 90) if f >= 30 else (240, 110, 110)
                d.text((x, y), c[: col_w[i] - 1], font=font, fill=colour)
                x += col_w[i] * char_w
            y += line_h
        y += line_h
    d.text((20, y), footer, font=font, fill=(150, 150, 150))
    path = out / "tour.png"
    im.save(path)
    return path


# --------------------------------------------------------------------------


def git_head(path: Path) -> str:
    try:
        return subprocess.run(["git", "-C", str(path), "rev-parse", "--short", "HEAD"], capture_output=True, text=True,
                              check=True).stdout.strip()
    except Exception:  # noqa: BLE001
        return "?"


def main() -> int:
    ap = argparse.ArgumentParser(description=__doc__, formatter_class=argparse.RawDescriptionHelpFormatter)
    ap.add_argument("--label", default=utc_stamp(), help="this tour's name (default: the UTC stamp)")
    ap.add_argument("--preset", action="append", choices=list(PRESETS), help="window and zoom presets (default: all three)")
    ap.add_argument("--mode", action="append", choices=list(MODES), help="GUO modes (default: all three)")
    ap.add_argument("--scene", action="append", choices=[s[0] for s in SCENES] + list(GUO_ONLY), help="these scenes only")
    ap.add_argument("--no-cuo", action="store_true", help="GUO only")
    ap.add_argument("--no-guo", action="store_true", help="ClassicUO only")
    ap.add_argument("--port", type=int, help="shard port (default UO_SHARD_PORT)")
    ap.add_argument("--out", help="output folder (default build/perf_tour/<label>)")
    ap.add_argument("--seconds", type=int, default=20, help="ClassicUO: settle time after [go before its averages are read")
    ap.add_argument("--timeout", type=float, default=200, help="ClassicUO: per scene")
    ap.add_argument("--as-built", action="store_true", help="run the clients as built; do not rebuild GUO optimised or ClassicUO")
    ap.add_argument("--report", metavar="DIR", help="redraw tour.md/json/png from a finished tour folder")
    args = ap.parse_args()

    if args.report:
        build_report(Path(args.report))
        print((Path(args.report) / "tour.md").read_text(encoding="utf-8"))
        return 0

    cfg = load_config()
    if args.port:
        cfg = dataclasses.replace(cfg, shard_port=args.port)
    out = Path(args.out) if args.out else cfg.build / "perf_tour" / args.label
    out.mkdir(parents=True, exist_ok=True)
    presets = args.preset or list(PRESETS)
    modes = args.mode or list(MODES)
    scenes = args.scene or []
    cuo_scenes = [s for s in SCENES if not scenes or s[0] in scenes]

    tour = {"label": args.label, "started": datetime.now(timezone.utc).strftime("%Y-%m-%d %H:%M"),
            "head": git_head(cfg.root),
            "cuo_pin": git_head(cfg.upstream),
            "presets": presets, "modes": modes, "scenes": scenes or "all"}
    (out / "tour.json").write_text(json.dumps(tour, indent=2), encoding="utf-8")

    if not args.as_built:
        if not args.no_guo:
            build_client(cfg)
        if not args.no_cuo and not cfg.upstream_exe.exists():
            ab.build_cuo(cfg)

    failed = 0
    for preset in presets:
        if not args.no_guo:
            for mode in modes:
                failed += run_guo(cfg, out, args.label, preset, mode, scenes, args.port) is None
        if not args.no_cuo:
            for scene in cuo_scenes:
                failed += run_cuo(cfg, out, preset, scene, args.seconds, args.timeout) is None
        build_report(out)  # a partial report after every preset, in case the run is cut short

    build_report(out)
    print((out / "tour.md").read_text(encoding="utf-8"))
    print(f"[tour] {out}")
    return 1 if failed else 0


if __name__ == "__main__":
    sys.exit(main())
