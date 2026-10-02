#!/usr/bin/env python3
"""A scripted tour of the GUO editor, recorded as a video.

Opens the real Godot editor on the project with the addon's tour flag. The
addon (addons/guo_editor/EditorTour.cs) walks every editor feature on the
real client install, puts a caption over each, asserts that it worked and
saves a frame of the editor window. This tool then stitches the frames into
an MP4 with ffmpeg and writes summary.md: each segment and whether it worked.

    python tools/editor_tour/run.py              # the whole tour, live part included
    python tools/editor_tour/run.py --no-live    # skip the private shard
    python tools/editor_tour/run.py --no-build   # skip the first dotnet build
    python tools/editor_tour/run.py --size 2560x1440 --scale 1 --segments layout,maps --no-video

By default the tour records at 3840x2160 with the editor's display scale at
1.5, so the interface is laid out like a 2560x1440 screen and is readable in
the video. The scale is not set in the user's editor settings: the editor is
started on a scratch settings folder (build/editor_tour/appdata, through
APPDATA) holding just that scale, so the user's own settings, layout and
theme are neither read nor changed.

Output: build/editor_tour/<stamp>/ (frames/, tour.json, editor.log,
editor_tour.mp4, summary.md). Frames are renders of client art: they stay
under build/ and are never committed.

The editor window opens without taking focus (tools/guo/process.py
no_activate; the addon sets NoFocus itself). It is a real window on the
desktop for a few minutes, though. The tour never presses Start server or
Start client.

The live segment uses only this checkout's private shard
(tools/editor_shard) on its own ports, never the shared dev shard. If the
private shard cannot be set up the tour runs without it and summary.md says so.

Exit codes: 0 every segment passed or was skipped with a reason,
1 a segment failed, 2 the editor could not run.
"""

from __future__ import annotations

import argparse
import json
import os
import socket
import subprocess
import sys
import time
from datetime import datetime
from pathlib import Path

sys.path.insert(0, str(Path(__file__).resolve().parents[1]))

from guo import load_config  # noqa: E402
from guo.process import no_activate  # noqa: E402

TIMEOUT_S = 1500
PORT, BRIDGE = 2606, 2607   # this checkout's private shard; never the dev shard (2593)
SHARD_TOOL = Path(__file__).resolve().parents[1] / "editor_shard" / "run.py"


def listening(port: int) -> bool:
    with socket.socket() as s:
        s.settimeout(0.5)
        return s.connect_ex(("127.0.0.1", port)) == 0


def shard(*args: str) -> int:
    r = subprocess.run([sys.executable, str(SHARD_TOOL), *args], capture_output=True, text=True)
    if r.returncode != 0:
        print(r.stdout[-1500:], r.stderr[-800:])
    return r.returncode


def prepare_shard(cfg) -> tuple[bool, str]:
    """Sets up and starts the private shard for the live segment. (started_by_us, why not or '')."""
    home = cfg.build / "shard_private"
    state_file = home / "state.json"
    if listening(PORT) or listening(BRIDGE):
        return False, f"ports {PORT}/{BRIDGE} are already in use"
    if state_file.exists():
        state = json.loads(state_file.read_text(encoding="utf-8"))
        if state.get("port") != PORT:
            return False, f"this checkout's private shard is already set up on port {state.get('port')}, not {PORT}"
    elif shard("setup", "--port", str(PORT)) != 0:
        return False, "editor_shard setup failed (is ModernUO built? launchers/shard/build.bat)"
    if shard("bridge", "--bridge-port", str(BRIDGE)) != 0:
        return False, "editor_shard bridge failed to build or install"
    if shard("start") != 0:
        return False, "the private shard did not start"
    for _ in range(60):
        if listening(BRIDGE):
            return True, ""
        time.sleep(1)
    shard("stop")
    return False, f"the private shard's editor bridge never listened on {BRIDGE}"


def scratch_settings(root: Path, scale: float) -> dict:
    """Environment for an editor with its own settings folder: display scale only, single window
    (so popup menus are drawn inside the window the tour captures)."""
    folder = root / "Godot"
    folder.mkdir(parents=True, exist_ok=True)
    custom = 2 if scale == 1.0 else 6   # 2 = 100%, 6 = custom
    (folder / "editor_settings-4.7.tres").write_text(
        "[gd_resource type=\"EditorSettings\" format=3]\n\n[resource]\n"
        f"interface/editor/appearance/display_scale = {custom}\n"
        f"interface/editor/appearance/custom_display_scale = {scale}\n"
        "interface/multi_window/enable = false\n"
        "interface/editor/display/single_window_mode = true\n",
        encoding="utf-8")
    env = dict(os.environ)
    env["APPDATA"] = str(root)
    return env


def build(project: Path) -> bool:
    print("[editor_tour] building C#")
    r = subprocess.run(["dotnet", "build", str(project / "GUO.csproj"), "-nologo", "-v", "q"], capture_output=True, text=True)
    if r.returncode != 0:
        print(r.stdout[-4000:], r.stderr[-2000:])
    return r.returncode == 0


def make_video(out: Path, report: dict) -> tuple[Path | None, float, str]:
    frames = report.get("frames", [])
    if not frames:
        return None, 0.0, "no frames"
    lst = out / "frames.txt"
    with lst.open("w", encoding="utf-8") as f:
        for fr in frames:
            f.write(f"file 'frames/{fr['file']}'\nduration {max(fr['seconds'], 0.05):.3f}\n")
        f.write(f"file 'frames/{frames[-1]['file']}'\n")
    mp4 = out / "editor_tour.mp4"
    vf = ("scale=trunc(iw/2)*2:trunc(ih/2)*2:flags=neighbor,fps=30,format=yuv420p")
    cmd = ["ffmpeg", "-y", "-loglevel", "error", "-f", "concat", "-safe", "0", "-i", str(lst),
           "-vf", vf, "-c:v", "libx264", "-preset", "medium", "-crf", "20", "-movflags", "+faststart", str(mp4)]
    r = subprocess.run(cmd, capture_output=True, text=True)
    if r.returncode != 0 or not mp4.exists():
        return None, 0.0, r.stderr[-800:]
    return mp4, sum(fr["seconds"] for fr in frames), ""


def write_summary(out: Path, report: dict, mp4: Path | None, video_s: float, live_note: str, run_s: float) -> None:
    segs = report.get("segments", [])
    worked = [s for s in segs if not s["skipped"] and not s["failures"]]
    skipped = [s for s in segs if s["skipped"]]
    failed = [s for s in segs if s["failures"]]
    video = f"{mp4.name}, {video_s:.0f} s, {len(report['frames'])} frames" if mp4 else "none"
    lines = ["# GUO editor tour", "",
             f"Run {datetime.now():%Y-%m-%d %H:%M}, {run_s:.0f} s wall clock. Video: {video}.",
             f"Window {report.get('window')}. Live shard: {live_note}.", "",
             f"{len(worked)} worked, {len(skipped)} skipped, {len(failed)} failed, of {len(segs)} segments. "
             "Each segment is a set of checks run in the editor, not just screenshots.", "",
             "| # | Segment | Result | Frames | What was checked |", "|---|---|---|---|---|"]
    for i, s in enumerate(segs, 1):
        result = "FAILED" if s["failures"] else ("skipped" if s["skipped"] else "worked")
        notes = "; ".join(s["passed"]) or "-"
        if s["failures"]:
            notes = "FAILED: " + "; ".join(s["failures"]) + ". Passed: " + notes
        if s["skipped"]:
            notes = "Skipped: " + s["skipped"] + ". " + ("" if notes == "-" else notes)
        lines.append(f"| {i} | {s['title']} | {result} | {s['frames']} | {notes.replace('|', '/')} |")
    lines += ["", "Not covered: the run bar's Start server / Start client buttons are shown with their tooltips "
              "(drawn from the controls' own tooltip text) but never pressed; the video has no audio "
              "(the Sounds segment does play the sound in the editor).",
              "The frames are renders of client art and stay under build/."]
    if report.get("fatal"):
        lines += ["", f"The tour stopped early: {report['fatal']}"]
    (out / "summary.md").write_text("\n".join(lines) + "\n", encoding="utf-8")


def main() -> int:
    ap = argparse.ArgumentParser(description=__doc__.splitlines()[0])
    ap.add_argument("--no-live", action="store_true", help="do not start the private shard; show the Shard dock offline")
    ap.add_argument("--no-build", action="store_true")
    ap.add_argument("--size", default="3840x2160", help="window size, default 3840x2160")
    ap.add_argument("--scale", type=float, default=1.5, help="editor display scale for the run, default 1.5")
    ap.add_argument("--segments", help="comma separated segment ids to run (default all)")
    ap.add_argument("--no-video", action="store_true", help="frames only")
    ap.add_argument("--out", type=Path, help="output folder (default build/editor_tour/<stamp>)")
    args = ap.parse_args()

    cfg = load_config()
    project = cfg.godot_project
    out = (args.out or cfg.build / "editor_tour" / datetime.now().strftime("%Y%m%d_%H%M%S")).resolve()
    out.mkdir(parents=True, exist_ok=True)
    started = time.monotonic()

    if not args.no_build and not build(project):
        print("[editor_tour] FAILED: the C# does not build")
        return 2

    live_note, started_shard = "off (--no-live)", False
    if not args.no_live:
        print("[editor_tour] starting this checkout's private shard for the live segment")
        started_shard, why = prepare_shard(cfg)
        live_note = f"private shard, bridge port {BRIDGE}" if started_shard else f"off ({why})"
        print(f"[editor_tour] live: {live_note}")

    project_godot = project / "project.godot"
    before = project_godot.read_bytes()
    cmd = [str(cfg.godot_console_exe), "--editor", "--path", str(project), "--", "--guo-editor-tour", str(out),
           "--guo-editor-tour-size", args.size]
    if args.segments:
        cmd += ["--guo-editor-tour-segments", args.segments]
    env = scratch_settings(cfg.build / "editor_tour" / "appdata", args.scale)
    if started_shard:
        cmd += ["--guo-editor-tour-live-port", str(BRIDGE)]
    log = out / "editor.log"
    print(f"[editor_tour] editor window opens now (no focus taken); frames go to {out / 'frames'}")
    code = None
    try:
        with log.open("w", encoding="utf-8", errors="replace") as lf:
            proc = subprocess.Popen(cmd, stdout=lf, stderr=subprocess.STDOUT, env=env, **no_activate())
            while proc.poll() is None:
                if time.monotonic() - started > TIMEOUT_S:
                    proc.kill()
                    print("[editor_tour] FAILED: timed out; killed the editor")
                    break
                time.sleep(0.5)
            code = proc.returncode
    except OSError as ex:
        print(f"[editor_tour] FAILED: could not start the editor: {ex}")
        return 2
    finally:
        if project_godot.read_bytes() != before:
            project_godot.write_bytes(before)
            print("[editor_tour] restored project.godot (the editor rewrote it on exit)")
        if started_shard:
            shard("stop")

    report_path = out / "tour.json"
    if not report_path.exists():
        print(f"[editor_tour] FAILED: no tour.json (editor exit {code}); see {log}")
        return 2
    report = json.loads(report_path.read_text(encoding="utf-8"))
    mp4, video_s, err = (None, 0.0, "skipped (--no-video)") if args.no_video else make_video(out, report)
    if mp4 is None:
        print(f"[editor_tour] video FAILED: {err}")
    write_summary(out, report, mp4, video_s, live_note, time.monotonic() - started)
    segs = report["segments"]
    for s in segs:
        mark = "FAIL" if s["failures"] else ("skip" if s["skipped"] else "ok  ")
        print(f"[editor_tour]   {mark} {s['title']}  ({s['frames']} frames)")
        for f in s["failures"]:
            print(f"[editor_tour]        {f}")
    print(f"[editor_tour] video   : {mp4}")
    print(f"[editor_tour] summary : {out / 'summary.md'}")
    print(f"[editor_tour] run time: {time.monotonic() - started:.0f} s")
    return 1 if any(s["failures"] for s in segs) or report.get("fatal") else 0


if __name__ == "__main__":
    sys.exit(main())
