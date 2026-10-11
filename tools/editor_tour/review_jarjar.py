#!/usr/bin/env python3
"""Watch PR33 editor controls and offline client dialogs; save a local gallery and captioned video."""
from __future__ import annotations

import argparse
import html
import importlib.util
import json
import os
from pathlib import Path
import shutil
import subprocess
import sys
import tempfile

sys.path.insert(0, str(Path(__file__).resolve().parents[1]))
from guo import load_config


def package(run_dir: Path, video: bool = True) -> tuple[Path, Path | None]:
    frames = []
    for line in (run_dir / "events.jsonl").read_text(encoding="utf-8").splitlines():
        event = json.loads(line)
        if event["kind"] == "step_end":
            frames.extend(event.get("detail", {}).get("frame_info", []))
    gallery = run_dir / "review.html"
    titles = ["Overview", "Regions: missing setup", "Regions: music generation", "Regions: SFX layer", "Static Studio: themes and splats", "Art: variants and artifacts", "Terrain Layers", "Setup gaps", "Client: music Audio Zones", "Client: SFX Audio Zones", "Client: Repaint", "Client: Splat Placer", "Client: Voice"]
    cards = "".join(f'<article><h2>{i+1}. {html.escape(titles[i] if i < len(titles) else Path(f["file"]).stem)}</h2><p>{html.escape(f.get("caption", ""))}</p><a href="{html.escape(f["file"], quote=True)}"><img src="{html.escape(f["file"], quote=True)}" alt="Review frame {i+1}"></a></article>' for i, f in enumerate(frames))
    outcome = json.loads((run_dir / "run.json").read_text(encoding="utf-8"))["ok"]
    gallery.write_text('<!doctype html><meta charset="utf-8"><title>GUO JarJar PR33 review</title><style>body{background:#17191b;color:#eee;font:18px system-ui;max-width:1500px;margin:40px auto;padding:0 24px}h1{font-size:32px}article{margin:50px 0}img{width:100%;image-rendering:auto}a{color:#8adccc}p{line-height:1.6}</style><h1>GUO: JarJar PR33 interface review</h1><p>Script checks: '+('PASS' if outcome else 'FAIL; inspect summary.md')+'. Editor controls and actual client dialogs rendered offline. Generation, live login, walk-in/out and TTS are not claimed. Client art stays local under build.</p>'+cards, encoding="utf-8")
    mp4 = None
    if video and frames and shutil.which("ffmpeg"):
        spec = importlib.util.spec_from_file_location("guo_review_video", Path(__file__).with_name("run.py"))
        module = importlib.util.module_from_spec(spec); spec.loader.exec_module(module)
        # The existing stitcher prepends frames/; ../ keeps the captured frames in their original folders.
        (run_dir / "frames").mkdir(exist_ok=True)
        report = {"frames": [{"file":"../"+f["file"], "seconds":f.get("seconds",8)} for f in frames]}
        mp4, _, error = module.make_video(run_dir, report)
        if error: print("Video: " + error)
    return gallery, mp4


def main() -> int:
    ap = argparse.ArgumentParser(description=__doc__)
    ap.add_argument("--no-build", action="store_true")
    ap.add_argument("--no-video", action="store_true")
    ap.add_argument("--package", type=Path, help="package an existing run without opening GUO again")
    args = ap.parse_args()
    if args.package:
        gallery, video = package(args.package.resolve(), not args.no_video)
        print(f"Gallery: {gallery}"); print(f"Video: {video}")
        return 0
    cfg = load_config()
    env = os.environ.copy(); env["UseSharedCompilation"] = "false"
    if not args.no_build:
        built = subprocess.run(["dotnet","build",str(cfg.godot_project/"GUO.csproj"),"-v:q"], cwd=cfg.root, env=env, capture_output=True, text=True)
        if built.returncode:
            print(built.stdout[-2500:]); return 2
    cfg.build.mkdir(parents=True, exist_ok=True)
    env["UO_WORLD_PROJECT"] = tempfile.mkdtemp(prefix="jarjar-review-world-", dir=cfg.build)
    before = set((cfg.build/"runs").glob("*_editor.jarjar.review_ai"))
    print("Opening a NoFocus GUO review window; each screen holds eight seconds.", flush=True)
    result = subprocess.run([sys.executable,str(cfg.root/"tools/scenario_run/run.py"),"editor.jarjar.review","--size","2560x1800","--scale","1","--no-register"], cwd=cfg.root, env=env)
    created = set((cfg.build/"runs").glob("*_editor.jarjar.review_ai")) - before
    if created:
        gallery, video = package(sorted(created)[-1], not args.no_video)
        print(f"Gallery: {gallery}"); print(f"Video: {video}")
    return result.returncode


if __name__ == "__main__":
    raise SystemExit(main())
