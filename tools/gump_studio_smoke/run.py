#!/usr/bin/env python3
"""Headless gump authoring/reply checks, optionally including an assembly reload."""
from __future__ import annotations
import argparse
import json
import subprocess
import sys
import time
from pathlib import Path

sys.path.insert(0, str(Path(__file__).resolve().parents[1]))
from guo import load_config
from guo.process import no_activate


def main() -> int:
    ap = argparse.ArgumentParser(description=__doc__)
    ap.add_argument("--reload", action="store_true")
    ap.add_argument("--windowed", action="store_true", help="Visual layout proof; requires permission to open a window")
    args = ap.parse_args()
    cfg = load_config()
    out = cfg.build / "gump_studio_smoke"
    out.mkdir(parents=True, exist_ok=True)
    for name in ("report.json", "before_reload.json", "reload.request", "reload.go"):
        (out / name).unlink(missing_ok=True)
    build = ["dotnet", "build", str(cfg.godot_project / "GUO.csproj"), "--nologo", "-v:q"]
    if subprocess.run(build, stdout=subprocess.DEVNULL).returncode:
        return 1
    project_file = cfg.godot_project / "project.godot"
    original = project_file.read_bytes()
    command = [str(cfg.godot_console_exe), *([] if args.windowed else ["--headless"]), "--editor", "--path", str(cfg.godot_project), "--",
               "--guo-editor-smoke", str(out), "--guo-gump-studio-only"]
    if args.reload:
        command += ["--guo-editor-smoke-reload"]
    if args.windowed:
        command += ["--guo-gump-studio-visual"]
    rebuilt = False
    try:
        with (out / "editor.log").open("w", encoding="utf-8") as log:
            with subprocess.Popen(command, stdout=log, stderr=subprocess.STDOUT, **no_activate()) as process:
                start = time.monotonic()
                while process.poll() is None:
                    if time.monotonic() - start > 150:
                        process.kill()
                        print("FAIL: gump editor timed out; see", out / "editor.log")
                        return 1
                    if args.reload and not rebuilt and (out / "reload.request").exists():
                        rebuild = build + ["-t:Rebuild"]
                        if subprocess.run(rebuild, stdout=subprocess.DEVNULL).returncode:
                            process.kill()
                            return 1
                        (out / "reload.go").write_text("rebuilt", encoding="utf-8")
                        rebuilt = True
                    time.sleep(.2)
    finally:
        project_file.write_bytes(original)
    report_path = out / "report.json"
    if not report_path.exists():
        print("FAIL: no report; see", out / "editor.log")
        return 1
    report = json.loads(report_path.read_text(encoding="utf-8"))
    ok = report.get("ok", False) and report.get("gump_studio", {}).get("ok", False)
    if args.reload:
        ok &= report.get("reloaded", False)
    print(json.dumps({"ok": ok, "gump_studio": report.get("gump_studio"), "reloaded": report.get("reloaded", False)}))
    return 0 if ok else 1


if __name__ == "__main__":
    raise SystemExit(main())
