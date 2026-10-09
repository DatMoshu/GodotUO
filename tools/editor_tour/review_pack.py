#!/usr/bin/env python3
"""Packs an editor tour run for review: step timeline, per-feature-group clips, key frames, overview video.

    python tools/editor_tour/review_pack.py build/editor_review/<date>/run

Reads tour.json (frames with hold times, and the captions the tour put up) and writes, beside the run:
  timeline.md / timeline.json  every caption with its start time in editor_tour.mp4
  groups/<name>.mp4            the recording cut by feature group, for a reviewer with a length limit
  key_frames/                  the last frame of each captioned step of the map-editing segments
  overview.mp4                 a few seconds per feature, the captions kept, full resolution

The overview leaves out steps whose caption or detail box names a file, and the segments that show command
output, so nothing on screen carries a machine path, a file path or an internal name.
"""

from __future__ import annotations

import argparse
import json
import re
import shutil
import subprocess
import sys
from pathlib import Path

GROUPS = {
    "A_place_select_view_edit_ground": ["we_stamp", "we_select", "we_stack", "we_view", "we_edit", "we_land",
                                        "we_heights"],
    "B_brush_settings_rules_presets": ["we_brush", "we_settings", "we_variations", "we_rules", "we_presets"],
    "C_objects_maptools_views_layers_undo_export": ["we_objects", "we_maptools", "we_layers", "we_modes",
                                                    "we_maplayers", "we_undo", "we_export"],
}
# Segments the overview skips entirely: they show tool output or file names in a detail box.
OVERVIEW_SKIP = {"overlay", "we_export", "export"}
# Segments whose detail box is up until the last frame: only that frame is used.
LAST_FRAME_ONLY: set[str] = set()
# Steps whose result names a folder in the line under the map: the overview takes the step's first frame instead.
FIRST_FRAME_CAPTIONS = ("Scene pack saves",)
FILE_LIKE = re.compile(r"\.json|\.mul|\.mp4|\.png|\b[A-Za-z]:[\\/]|\\|\b(?:build|tools|docs)/")
HOLD = 3.0


def mmss(t: float) -> str:
    return f"{int(t // 60)}:{t % 60:04.1f}"


def main() -> int:
    ap = argparse.ArgumentParser()
    ap.add_argument("run", type=Path)
    ap.add_argument("--height", type=int, default=1080, help="height of the group clips for review (default 1080)")
    args = ap.parse_args()
    run = args.run.resolve()
    tour = json.loads((run / "tour.json").read_text(encoding="utf-8"))
    frames = tour["frames"]
    start, t = [], 0.0
    for fr in frames:
        start.append(t)
        t += fr["seconds"]
    total = t

    # captions -> frame ranges
    steps = tour.get("steps", [])
    seg_end: dict[str, int] = {}
    for i, fr in enumerate(frames):
        seg_end[fr["segment"]] = i + 1
    rows = []
    for i, s in enumerate(steps):
        a = s["next_frame"]
        nxt = steps[i + 1]["next_frame"] if i + 1 < len(steps) and steps[i + 1]["segment"] == s["segment"] else seg_end.get(s["segment"], a)
        if a >= len(frames) or nxt <= a or frames[a]["segment"] != s["segment"]:
            continue
        rows.append({"segment": s["segment"], "caption": s["caption"], "start_s": round(start[a], 2),
                     "end_s": round(start[nxt - 1] + frames[nxt - 1]["seconds"], 2), "frames": [a, nxt - 1]})
    (run / "timeline.json").write_text(json.dumps(rows, indent=2), encoding="utf-8")
    md = ["# Step timeline", "", f"Video: editor_tour.mp4, {total:.0f} s. Start times are positions in the video.", "",
          "| Start | Segment | Caption |", "|---|---|---|"]
    for r in rows:
        md.append(f"| {mmss(r['start_s'])} | {r['segment']} | {r['caption'].replace('|', '/')} |")
    (run / "timeline.md").write_text("\n".join(md) + "\n", encoding="utf-8")

    # group clips
    video = run / "editor_tour.mp4"
    gdir = run / "groups"
    gdir.mkdir(exist_ok=True)
    for name, segs in GROUPS.items():
        idx = [i for i, fr in enumerate(frames) if fr["segment"] in segs]
        if not idx:
            continue
        a, b = start[idx[0]], start[idx[-1]] + frames[idx[-1]]["seconds"]
        out = gdir / f"{name}.mp4"
        vf = f"scale=-2:{args.height}:flags=neighbor,format=yuv420p"
        subprocess.run(["ffmpeg", "-y", "-loglevel", "error", "-ss", f"{a:.2f}", "-t", f"{b - a:.2f}", "-i", str(video),
                        "-vf", vf, "-c:v", "libx264", "-preset", "medium", "-crf", "24", "-an", str(out)], check=True)
        print(f"{name}: {mmss(a)}-{mmss(b)} -> {out.name}")
        (gdir / f"{name}.range.txt").write_text(f"{a:.2f} {b:.2f}\n", encoding="utf-8")

    # key frames and overview
    kdir = run / "key_frames"
    if kdir.exists():
        shutil.rmtree(kdir)
    kdir.mkdir()
    chosen = []
    for r in rows:
        if not r["segment"].startswith("we_") or r["segment"] in OVERVIEW_SKIP:
            continue
        last = r["frames"][0] if r["caption"].startswith(FIRST_FRAME_CAPTIONS) else r["frames"][1]
        shutil.copy(run / "frames" / frames[last]["file"], kdir / f"{start[last]:07.1f}s_{r['segment']}.png")
    seen_last = set()
    for r in rows:
        seg = r["segment"]
        if not seg.startswith("we_") or seg in OVERVIEW_SKIP:
            continue
        if seg in LAST_FRAME_ONLY:
            if seg in seen_last or r is not [x for x in rows if x["segment"] == seg][-1]:
                continue
            seen_last.add(seg)
        elif FILE_LIKE.search(r["caption"]):
            continue
        pick = r["frames"][0] if r["caption"].startswith(FIRST_FRAME_CAPTIONS) else r["frames"][1]
        chosen.append(frames[pick]["file"])
    lst = run / "overview.txt"
    with lst.open("w", encoding="utf-8") as f:
        for name in chosen:
            f.write(f"file 'frames/{name}'\nduration {HOLD}\n")
        f.write(f"file 'frames/{chosen[-1]}'\n")
    subprocess.run(["ffmpeg", "-y", "-loglevel", "error", "-f", "concat", "-safe", "0", "-i", str(lst), "-vf",
                    "scale=trunc(iw/2)*2:trunc(ih/2)*2:flags=neighbor,fps=30,format=yuv420p", "-c:v", "libx264",
                    "-preset", "medium", "-crf", "20", "-movflags", "+faststart", str(run / "overview.mp4")], check=True)
    print(f"overview.mp4: {len(chosen)} steps x {HOLD} s")
    return 0


if __name__ == "__main__":
    sys.exit(main())
