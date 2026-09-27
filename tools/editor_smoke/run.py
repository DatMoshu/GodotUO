#!/usr/bin/env python3
"""Prove the GUO editor addon works: open the real editor and check it.

Builds the C#, starts the pinned Godot editor on the project with the addon's
smoke flag, and lets the addon check itself against the real client install
(`addons/guo_editor/EditorSmoke.cs`): the UO docks are in the editor, the
client data loaded through the ported loaders, an art search selects the art
and the inspector decoded real pixels. It writes a report, the decoded art
and a capture of the editor window to build/editor_smoke/<mode>/.

    python tools/editor_smoke/run.py                 # windowed: with a screenshot
    python tools/editor_smoke/run.py --headless      # no window, no screenshot
    python tools/editor_smoke/run.py --reload        # also rebuild + hot-reload
    python tools/editor_smoke/run.py --art 0x0E75

Or through the launcher:

    launchers\\dev\\editor_smoke.bat

--reload is open question 1 of docs/editor_plan.md made mechanical: after
the first pass the C# is rebuilt with the editor still open, the editor is
asked to reload the assembly, and the checks run again against the reloaded
addon. The editor log must say the old assembly context unloaded.

The windowed editor rewrites project.godot on exit (it drops the comments;
it does so with or without the addon). The file is restored afterwards so a
smoke run never leaves the tree dirty.

Exit codes:
    0  every check passed
    1  a check failed (see report.json and editor.log)
    2  the editor could not be started or timed out
"""

from __future__ import annotations

import argparse
import json
import shutil
import subprocess
import sys
import time
from pathlib import Path

sys.path.insert(0, str(Path(__file__).resolve().parents[1]))

from guo import load_config  # noqa: E402

TIMEOUT_S = 600


def build(project: Path) -> bool:
    print("[editor_smoke] building C#")
    result = subprocess.run(
        ["dotnet", "build", str(project / "GUO.csproj"), "-nologo", "-v", "q"],
        capture_output=True,
        text=True,
    )
    if result.returncode != 0:
        print(result.stdout[-4000:])
        print(result.stderr[-4000:])
    return result.returncode == 0


def main() -> int:
    ap = argparse.ArgumentParser(description=__doc__.splitlines()[0])
    ap.add_argument("--headless", action="store_true", help="no window: checks only, no screenshot")
    ap.add_argument("--reload", action="store_true", help="also rebuild and hot-reload the assembly")
    ap.add_argument("--art", default="0x0E75", help="art id (static) the dock searches for")
    ap.add_argument("--out", type=Path, help="output folder (default build/editor_smoke/<mode>)")
    ap.add_argument("--no-build", action="store_true", help="skip the first C# build")
    args = ap.parse_args()

    cfg = load_config()
    project = cfg.godot_project
    mode = ("headless" if args.headless else "windowed") + ("_reload" if args.reload else "")
    out = (args.out or cfg.build / "editor_smoke" / mode).resolve()

    # Old markers from an earlier run would make the addon think it is past a
    # reload already.
    if out.exists():
        shutil.rmtree(out)
    out.mkdir(parents=True)

    if not args.no_build and not build(project):
        print("[editor_smoke] FAILED: the C# does not build")
        return 1

    project_godot = project / "project.godot"
    before = project_godot.read_bytes()

    cmd = [str(cfg.godot_console_exe), "--editor", "--path", str(project)]
    if args.headless:
        cmd.insert(1, "--headless")
    cmd += ["--", "--guo-editor-smoke", str(out), "--guo-editor-smoke-art", args.art]
    if args.reload:
        cmd.append("--guo-editor-smoke-reload")

    log_path = out / "editor.log"
    print(f"[editor_smoke] {mode}: {' '.join(cmd)}")
    started = time.monotonic()
    rebuilt = None
    try:
        with log_path.open("w", encoding="utf-8", errors="replace") as log:
            proc = subprocess.Popen(cmd, stdout=log, stderr=subprocess.STDOUT)
            while proc.poll() is None:
                if time.monotonic() - started > TIMEOUT_S:
                    proc.kill()
                    print(f"[editor_smoke] FAILED: editor still running after {TIMEOUT_S} s; killed")
                    return 2
                if args.reload and rebuilt is None and (out / "reload.request").exists():
                    # Change the assembly on disk the way an edit would: touch a
                    # source so the incremental build recompiles and rewrites it.
                    (project / "addons" / "guo_editor" / "EditorSmoke.cs").touch()
                    rebuilt = build(project)
                    (out / "reload.go").write_text("rebuilt" if rebuilt else "build failed")
                time.sleep(0.5)
            code = proc.returncode
    except OSError as ex:
        print(f"[editor_smoke] FAILED: could not start {cmd[0]}: {ex}")
        return 2
    finally:
        if project_godot.read_bytes() != before:
            project_godot.write_bytes(before)
            print("[editor_smoke] restored project.godot (the editor rewrote it on exit)")

    report_path = out / "report.json"
    if not report_path.exists():
        print(f"[editor_smoke] FAILED: no report (editor exit {code}); see {log_path}")
        return 1

    report = json.loads(report_path.read_text(encoding="utf-8"))
    failures = list(report.get("failures", []))

    if args.reload:
        log = log_path.read_text(encoding="utf-8", errors="replace")
        if rebuilt is not True:
            failures.append("the rebuild between the two passes failed or never ran")
        if not report.get("reloaded"):
            failures.append("the second pass never ran: no reload reached the addon")
        if "Assembly load context unloaded successfully" not in log:
            failures.append("editor log does not show the old assembly context unloading")
        if "Failed to unload assemblies" in log:
            failures.append("editor log says the old assemblies failed to unload")

    if code not in (0, None) and not failures:
        failures.append(f"editor exited with {code}")

    print(f"[editor_smoke] client data : {report.get('client_data')} (loaded in {report.get('load_ms')} ms)")
    print(f"[editor_smoke] art         : {report.get('art_query')} -> {report.get('art_index')}, "
          f"{report.get('art_size')}, {report.get('art_opaque_pixels')} opaque px")
    for key in ("art_png", "screenshot"):
        if report.get(key):
            print(f"[editor_smoke] {key:<12}: {report[key]}")
    if args.reload and report.get("reloaded"):
        print("[editor_smoke] reload      : assembly rebuilt and reloaded; checks passed again")
    print(f"[editor_smoke] report      : {report_path}")

    if failures:
        for f in failures:
            print(f"[editor_smoke] FAIL: {f}")
        return 1

    print("[editor_smoke] OK")
    return 0


if __name__ == "__main__":
    sys.exit(main())
