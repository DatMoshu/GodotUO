#!/usr/bin/env python3
"""Run a GUO scenario with the AI driver and record what happened.

    python tools/scenario_run/run.py editor.tabs.sweep
    python tools/scenario_run/run.py tools/scenarios/editor/tabs.scenario.json --var account=gm1
    python tools/scenario_run/run.py validate [NAME ...]     # check scenario files, run nothing
    python tools/scenario_run/run.py list                    # scenarios, then the registry's recent runs

A scenario (docs/data_formats.md, "Scenario runs") is a list of steps. The runner performs each step's `do`
through an MCP of the program it started (the editor's, this build: launch, wait, shot, note, tour_segment,
editor_invoke), polls the step's `expect`, and writes everything to build/runs/<run_id>/:

    run.json  events.jsonl  summary.md  editor.log (redacted)  shot_<step>.png  segments/<step>/...

then copies run.json, events.jsonl, summary.md and the stills to GUO_RUNS_SHARED_DIR and adds the run to the
registry GUO_RUNS_DB, when those are configured (settings.py). Nothing here has a default path.

Watchdogs: a step is failed after its timeout (timeouts.step_s, or do.timeout_s); the run stops after
timeouts.run_s; an MCP call with no reply for 45 s, or an editor that exits, is a hang: the editor this runner
started (only that process tree) is killed, a `hang` event is written and the exit code is 3.

Exit codes: 0 every step passed (or was skipped), 1 a step failed or the run was cut short (run timeout, the editor
exited), 2 the run could not start (bad scenario, missing variable), 3 hang.
"""

from __future__ import annotations

import argparse
import hashlib
import os
import shutil
import json
import socket
import sys
import threading
from datetime import datetime, timezone
from pathlib import Path

sys.path.insert(0, str(Path(__file__).resolve().parent))
sys.path.insert(0, str(Path(__file__).resolve().parents[1]))

import events as ev  # noqa: E402
import publish  # noqa: E402
import registry  # noqa: E402
import scenario as sc  # noqa: E402
import settings  # noqa: E402
from driver import Runner  # noqa: E402
from mcp_client import McpClient  # noqa: E402
from redact import load_deny  # noqa: E402
import capture  # noqa: E402
from session import ClientSession, EditorSession  # noqa: E402

EXIT = {"ok": 0, "failed": 1, "timeout": 1, "error": 1, "hang": 3}


def machine_key() -> str:
    """A stable name for this PC that is neither a path nor the host name."""
    return hashlib.sha1(socket.gethostname().encode()).hexdigest()[:8]


def parse_vars(items: list[str]) -> dict[str, str]:
    out: dict[str, str] = {}
    for item in items:
        if "=" not in item:
            raise sc.ScenarioError(f"--var needs name=value, got {item}")
        k, v = item.split("=", 1)
        out[k] = v
    return out


HARD_KILL_GRACE_S = 60       # past run_s plus this, a wedged runner is cut down from outside (the process watchdog)


def shard_address(scen: sc.Scenario, root: Path) -> tuple[str, int] | None:
    """The shard a scenario needs, or None when it needs none or the launcher config does not name one."""
    if not scen.requires.get("shard") or scen.surface != "client":
        return None
    host = settings.read_setting("UO_SHARD_HOST", root)
    port = settings.read_setting("UO_SHARD_PORT", root)
    return (host, int(port)) if host and port.isdigit() else None


def arm_hard_timer(scen: sc.Scenario, session, log: ev.EventLog) -> threading.Timer:
    """Last line of defence: if the runner itself wedges, kill the program it started (only that) and exit non-zero."""
    def cut_down() -> None:
        try:
            log.emit("hang", detail={"reason": "the runner did not finish within run_s plus the grace period"})
        finally:
            if session is not None:
                session.stop()
            os._exit(4)
    timer = threading.Timer(float(scen.timeouts["run_s"]) + HARD_KILL_GRACE_S, cut_down)
    timer.daemon = True
    timer.start()
    return timer


def finalize_video(session, run_dir: Path, run_id: str, video_dir: str) -> tuple[str | None, str]:
    """The movie the engine wrote, as the master mp4. Returns (video_path or None, a line for the summary)."""
    if not getattr(session, "record", False):
        return None, "No video: this run recorded stills and events only."
    ffmpeg = capture.find_ffmpeg()
    if ffmpeg is None:
        return None, "The engine's movie is in raw/run.avi; ffmpeg was not found, so no master was made (video pending)."
    mp4 = run_dir / "run.mp4"
    ok, note = capture.transcode(ffmpeg, session.avi, mp4)
    if not ok:
        return None, f"No master: {note}."
    if video_dir and Path(video_dir).is_dir():
        target = Path(video_dir) / f"{run_id}.mp4"
        try:
            shutil.copy2(mp4, target)
        except OSError as ex:
            return None, f"Master: run.mp4 in the run folder ({note}); copying to the video folder failed ({type(ex).__name__}), so it is pending there."
        session.avi.unlink(missing_ok=True)
        return str(target), f"Master: {target.name} in the video folder ({note}); 60 fps, H.264 CRF 16, recorded by the engine's MovieWriter."
    session.avi.unlink(missing_ok=True)                       # the master exists; the raw movie is several GB
    return None, f"Master: run.mp4 in the run folder ({note}); the video folder is not configured or mounted, so it is pending there."


def execute(scen: sc.Scenario, cfg, variables: dict[str, str], *, size: str | None, scale: float, register: bool,
            record: bool | None = None) -> tuple[dict, Path]:
    """One run, start to finish: the folder, the driver, the manifest, the shared copy, the registry row."""
    started = datetime.now(timezone.utc)
    run_id = ev.make_run_id(scen.id, "ai", started)
    run_dir = cfg.build / "runs" / run_id
    run_dir.mkdir(parents=True, exist_ok=True)
    log = ev.EventLog(run_dir / "events.jsonl", run_id, "ai")
    video_dir = settings.read_setting("GUO_RUNS_VIDEO_DIR", cfg.root)
    record = (scen.surface == "client") if record is None else record
    if record and scen.surface != "client":
        print("recording is the client's MovieWriter; an editor run records stills and events only", file=sys.stderr)
        record = False
    problems = capture.preflight(record=record, build_dir=cfg.build, video_dir=video_dir if record else "",
                                 shard=shard_address(scen, cfg.root))
    if problems:
        for problem in problems:
            log.emit("error", detail={"preflight": problem})
        log.close()
        raise sc.ScenarioError("pre-flight: " + "; ".join(problems))
    if scen.surface == "editor":
        session = EditorSession(cfg, run_dir, run_dir / "scratch", size or "3840x2160", scale)
    elif scen.surface == "client":
        session = ClientSession(cfg, run_dir, record, size)
    else:
        session = None
    runner = Runner(scen, run_dir, log, repo_root=cfg.root, session=session, variables=variables,
                    connect=lambda s: McpClient(s.port, s.token))
    timer = arm_hard_timer(scen, session, log)
    try:
        result = runner.run()
    finally:
        timer.cancel()
    log.close()
    video_path, video_note = finalize_video(session, run_dir, run_id, video_dir)
    manifest = {
        "run_id": run_id, "project": "guo", "scenario": scen.id, "title": scen.title, "surface": scen.surface,
        "driver": "ai", "commit": publish.git_commit(cfg.root), "build": scen.requires.get("build", "debug"),
        "shard": scen.requires.get("shard"), "machine": machine_key(), "started": result["started"],
        "ended": result["ended"], "ok": result["ok"], "recorded": bool(getattr(session, "record", False)), "aborted": result["aborted"], "exit_kind": result["exit_kind"],
        "steps": result["steps"], "artifacts": sorted(set(result["artifacts"])), "video_path": video_path, "video_note": video_note,
    }
    deny = load_deny(cfg.root)
    summary = publish.render_summary(manifest, scen.title)
    manifest["summary"] = summary
    (run_dir / "summary.md").write_text(summary, encoding="utf-8")
    publish.write_manifest(run_dir, manifest)
    shared = settings.read_setting("GUO_RUNS_SHARED_DIR", cfg.root)
    if shared:
        publish.copy_shared(run_dir, Path(shared), run_id, deny)
    else:
        print("GUO_RUNS_SHARED_DIR is not set: nothing copied to the shared area")
    db = settings.read_setting("GUO_RUNS_DB", cfg.root)
    if db and register:
        publish.register_run(Path(db), manifest, deny)
    elif not db:
        print("GUO_RUNS_DB is not set: the run is not registered")
    return manifest, run_dir


def cmd_validate(root: Path, names: list[str]) -> int:
    files = [sc.find(root, n) for n in names] if names else sorted((root / "tools" / "scenarios").rglob("*.scenario.json"))
    bad = 0
    for f in files:
        try:
            sc.load(f)
            print(f"ok    {f.name}")
        except sc.ScenarioError as ex:
            bad += 1
            print(f"FAIL  {ex}")
    return 1 if bad else 0


def cmd_list(root: Path) -> int:
    for f in sorted((root / "tools" / "scenarios").rglob("*.scenario.json")):
        try:
            s = sc.load(f)
            print(f"{s.id:40} {s.surface:7} {len(s.steps):3} steps  {s.title}")
        except sc.ScenarioError as ex:
            print(f"{f.name:40} INVALID: {ex}")
    db = settings.read_setting("GUO_RUNS_DB", root)
    if db and Path(db).is_file():
        con = registry.connect(Path(db))
        print()
        print("recent runs:")
        for rid, ok, nfail, total in con.execute(
                "SELECT run_id, ok, steps_failed, steps_total FROM runs WHERE project='guo' ORDER BY started DESC LIMIT 10"):
            print(f"  {rid}  {'PASS' if ok else 'FAIL'}  {total - (nfail or 0)}/{total}")
        con.close()
    return 0


def main(argv: list[str] | None = None) -> int:
    ap = argparse.ArgumentParser(description=__doc__, formatter_class=argparse.RawDescriptionHelpFormatter)
    ap.add_argument("scenario", nargs="*", help="a scenario id or file; or validate / list")
    ap.add_argument("--driver", choices=("ai", "human"), default="ai")
    ap.add_argument("--var", action="append", default=[], help="name=value for $name in the scenario")
    ap.add_argument("--size", default=None, help="window WxH (editor default 3840x2160; client: the project default, 1280x720, unless given)")
    rec = ap.add_mutually_exclusive_group()
    rec.add_argument("--record", dest="record", action="store_true", default=None,
                     help="record the client with the engine's MovieWriter (the default for a client scenario)")
    rec.add_argument("--no-record", dest="record", action="store_false", help="stills and events only")
    ap.add_argument("--scale", type=float, default=1.5, help="editor display scale")
    ap.add_argument("--no-register", action="store_true", help="do not add the run to the registry")
    args = ap.parse_args(argv)

    from guo import load_config
    cfg = load_config()
    names = args.scenario
    if names and names[0] == "validate":
        return cmd_validate(cfg.root, names[1:])
    if not names or names[0] == "list":
        return cmd_list(cfg.root)
    if len(names) != 1:
        ap.error("give one scenario")
    if args.driver == "human":
        print("the human driver is build step 5 and is not in this runner yet", file=sys.stderr)
        return 2
    try:
        scen = sc.load(sc.find(cfg.root, names[0]))
        variables = parse_vars(args.var)
        sc.substitute([s["do"] for s in scen.steps] + [s.get("expect", {}) for s in scen.steps], variables)  # missing variable stops before launch
    except sc.ScenarioError as ex:
        print(f"error: {ex}", file=sys.stderr)
        return 2
    try:
        manifest, run_dir = execute(scen, cfg, variables, size=args.size, scale=args.scale,
                                    register=not args.no_register, record=args.record)
    except sc.ScenarioError as ex:
        print(f"error: {ex}", file=sys.stderr)
        return 2
    print(f"{'PASS' if manifest['ok'] else 'FAIL'} {manifest['run_id']}  {run_dir}")
    return EXIT.get(manifest["exit_kind"], 1)


if __name__ == "__main__":
    sys.exit(main())
