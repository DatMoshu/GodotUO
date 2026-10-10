"""The Admin tab, driven in a real editor against the private instance (sprint "Admin tab", AD1, AD2a, AD2b).

    python tools/editor_shard/run.py admin-tab [--windowed]

Stops the private instance if `start` runs it, then opens the GUO editor on a
scratch workspace (build/admin_tab/workspace, so the user's own server list is
not touched) with the addon's scripted Admin run (EditorSmokeAdmin.cs). In it:

1. the run bar's "Private shard" profile (added for tools/editor_shard's home)
   is picked and started by the run bar, which hands the bridge this user's
   admin token (ADR-0035);
2. the Admin tab connects on the admin channel and reads Health;
3. Save now writes the world, and Health shows the new last save;
4. Restart saves, the run bar stops and starts the server, and the tab
   reconnects by itself and reads Health again (a fresh uptime);
5. the god view watches the facet again after the restart; a spawner put
   through the bridge arrives in a change-only push with the horses it
   spawned; Find finds it on Felucca; the NPCs filter hides the NPCs; with
   it selected Go there, Respawn and Clear are on (no staff character is
   online, and the hint says so), Respawn replaces its horses and Clear removes them
   (AD2b, pressed as buttons); its delete removes it;
6. the run bar stops the server.

Then it checks that every time in the tab's log is UTC with a Z, and that
neither the token nor a password reached the editor's output, the tab's log,
the server's console or the audit log. --windowed opens
a window (it takes no focus), saves a still of the tab at steps 2-5 and, with
ffmpeg on PATH, a clip of the run from four editor frames a second.
Prints one line per check; exit 0 when all pass.
"""

from __future__ import annotations

import json
import re
import shutil
import subprocess
import time
from pathlib import Path

from guo.process import build_child_env, no_activate

TIMEOUT_S = 900
# The story the evidence is filed under (rule: evidence_naming.md).
STORY = "AD2b"
STILLS = ("connected", "saved", "restarted", "godview", "spawner", "filtered", "respawn", "cleared")


def run(cfg, shard_home: Path, token: str, secrets: list[str], windowed: bool, stamp: str) -> int:
    results: list[tuple[bool, str]] = []

    def check(ok: bool, what: str) -> None:
        results.append((bool(ok), what))
        print(f"[admin-tab] {'PASS' if ok else 'FAIL'}  {what}")

    out = (cfg.build / "admin_tab").resolve()
    if out.exists():
        shutil.rmtree(out)
    workspace = out / "workspace"
    workspace.mkdir(parents=True)

    project = cfg.godot_project
    cmd = [str(cfg.godot_console_exe), "--editor", "--path", str(project),
           "--", "--guo-editor-smoke", str(out), "--guo-editor-admin", str(shard_home)]
    if not windowed:
        cmd.insert(1, "--headless")
    env = build_child_env()
    # A scratch workspace: the run bar makes its "Private shard" profile there, and the user's own list is left alone.
    env["UO_WORKSPACE_DIR"] = str(workspace)
    env["GUO_EDITOR_SCRIPTED"] = "1"
    # A neutral editor name: the audit log and the stills show it, never the user's.
    env["UO_EDITOR_NAME"] = "Admin tab check"
    # The scratch workspace has no secrets file: the token reaches the editor as the environment setting it resolves first.
    env["UO_BRIDGE_ADMIN_TOKEN"] = token
    project_godot = project / "project.godot"
    before = project_godot.read_bytes()
    log_path = out / "editor.log"
    print(f"[admin-tab] {'windowed' if windowed else 'headless'} editor on {shard_home.name}, scratch workspace {workspace}")
    started = time.monotonic()
    try:
        with log_path.open("w", encoding="utf-8", errors="replace") as log:
            proc = subprocess.Popen(cmd, stdout=log, stderr=subprocess.STDOUT, env=env, **no_activate())
            while proc.poll() is None:
                if time.monotonic() - started > TIMEOUT_S:
                    proc.kill()
                    print(f"[admin-tab] FAILED: editor still running after {TIMEOUT_S} s; killed")
                    break
                time.sleep(0.5)
    finally:
        if project_godot.read_bytes() != before:
            project_godot.write_bytes(before)

    report_path = out / "report.json"
    report = json.loads(report_path.read_text(encoding="utf-8")) if report_path.is_file() else {}
    admin = report.get("admin") or {}
    for failure in report.get("failures") or []:
        check(False, failure)
    if not report:
        check(False, f"the editor wrote no report; see {log_path}")

    def status(key: str) -> dict:
        raw = admin.get(key) or {}
        return {k: json.loads(v) if v is not None else None for k, v in raw.items()}

    first, after_save, after_restart, save = (status("status_first"), status("status_after_save"),
                                              status("status_after_restart"), status("save"))
    check(admin.get("offline_plain_words") and admin.get("offline_restart_blocked"),
          "before connecting, the tab says so in plain words and Restart is off")
    check(admin.get("started_by_run_bar"), f"the run bar started '{admin.get('server')}'")
    check(admin.get("granted") == "Administrator" and first.get("world") == "Running" and (first.get("items") or 0) > 0,
          f"the tab got the admin channel ({admin.get('granted')}) and Health: uptime {first.get('uptime_s')} s, "
          f"{first.get('items')} items, {first.get('mobiles')} mobiles, {first.get('memory_mb')} MB, {first.get('online')} online, "
          f"{first.get('server')} {first.get('version')}")
    words = admin.get("plain_words") or ""
    check("has been running for" in words and "The world holds" in words and "Restart saves the world" in words,
          "the plain-words panel describes the server and what Restart does")
    check(admin.get("restart_enabled"), "Restart is on for a server the run bar started")
    check(save.get("ok") is True and after_save.get("last_save") == save.get("last_save") and (after_save.get("last_save_s") or 0) < 60,
          f"Save now wrote the world in {save.get('ms')} ms and Health shows it ({after_save.get('last_save')})")
    restart_s = admin.get("restart_s") or 0
    check(restart_s > 0 and after_restart.get("uptime_s") is not None and after_restart["uptime_s"] <= restart_s + 5
          and admin.get("managed_after_restart"),
          f"Restart: saved, stopped and started by the run bar, reconnected after {restart_s} s, "
          f"uptime {after_restart.get('uptime_s')} s, still managed")
    first_view = admin.get("godview_first") or {}
    check(admin.get("godview_resubscribed") and (first_view.get("rows") or 0) > 0,
          f"the god view watched {first_view.get('facet')} again after the restart: {first_view.get('players')} players, "
          f"{first_view.get('npcs')} NPCs, {first_view.get('spawners')} spawners")
    push = admin.get("godview_push") or {}
    spawner = json.loads(push["spawner"]) if push.get("spawner") else {}
    check(push.get("full_lists") == 0 and (push.get("pushes") or 0) >= 1 and (push.get("horses") or 0) >= 1
          and 0 < (push.get("last_push_rows") or 0) < (push.get("rows") or 0) and spawner.get("count") == 3,
          f"a spawner put through the bridge arrived in {push.get('pushes')} change-only push(es) (last one {push.get('last_push_rows')} "
          f"of {push.get('rows')} rows, no new full list) with {push.get('horses')} horses, "
          f"{spawner.get('spawned')}/{spawner.get('count')} spawned")
    details = admin.get("godview_details") or ""
    check("Spawner" in details and "Horse" in details and "spawned" in details,
          "selecting the spawner shows what it spawns and how many, in plain words")
    check(admin.get("godview_found"), "Find found the spawner on Felucca (searched every facet)")
    check(admin.get("godview_filtered"), f"the NPCs filter hid the NPCs: \"{admin.get('godview_filter_status')}\"")
    buttons = admin.get("godview_spawner_buttons") or {}
    check(admin.get("godview_spawner_buttons_ok"),
          f"with the spawner selected Go there, Respawn and Clear are on ({', '.join(k for k, on in buttons.items() if on)}), "
          f"and the hint says Go there needs a staff character")
    check(admin.get("godview_npc_hint_ok"), f"with a horse selected and no staff online the hint says why: \"{admin.get('godview_npc_hint')}\"")
    respawn = json.loads(admin["godview_respawn"]) if admin.get("godview_respawn") else {}
    check(admin.get("godview_respawned"),
          f"Respawn, pressed in the tab, replaced the horses with new ones ({respawn.get('before')} -> {respawn.get('spawned')})")
    clear = json.loads(admin["godview_clear"]) if admin.get("godview_clear") else {}
    check(admin.get("godview_cleared"), f"Clear, pressed in the tab, removed the horses and kept the spawner ({clear.get('before')} -> {clear.get('spawned')})")
    check(admin.get("godview_removed"), "deleting the spawner removed it and its horses from the god view")
    check(admin.get("stopped"), "the run bar stopped the server at the end")

    # One clock in the tab: every log line and every audit time it shows is UTC, with a Z.
    tab_log = str(admin.get("log") or "")
    lines = [ln for ln in tab_log.splitlines() if ln.strip()]
    stamped = [ln for ln in lines if re.match(r"^\d\d:\d\d:\d\dZ ", ln)]
    check(lines and len(stamped) == len(lines) and " UTC" not in tab_log,
          f"the tab's log shows one clock, UTC with a Z ({len(stamped)}/{len(lines)} lines)")

    texts = [log_path.read_text(encoding="utf-8", errors="replace") if log_path.is_file() else "",
             str(admin.get("log") or "")]
    texts += [p.read_text(encoding="utf-8", errors="replace") for p in workspace.rglob("server.console.log")]
    audit = shard_home / "Logs" / "GUO" / "admin_audit.jsonl"
    texts.append(audit.read_text(encoding="utf-8", errors="replace") if audit.is_file() else "")
    leaked = [s for s in [token, *secrets] if s and any(s in t for t in texts)]
    check(not leaked, f"no token or password in the editor output, the tab's log, the server console or the audit log "
                      f"({len([s for s in [token, *secrets] if s])} secrets, {len(texts)} files)")

    # The stills, under the evidence naming rule.
    for name in STILLS:
        shot = out / f"admin_{name}.png"
        if shot.is_file():
            named = out / f"guo_{STORY}_admintab-{name}_still_{stamp}.png"
            shot.replace(named)
            print(f"[admin-tab] still: {named}")

    # The whole windowed run as one clip, from the frames the editor kept (four a second).
    frames = out / "clip_frames"
    if frames.is_dir() and any(frames.glob("f_*.png")) and shutil.which("ffmpeg"):
        clip = out / f"guo_{STORY}_admintab_clip_{stamp}.mp4"
        subprocess.run(["ffmpeg", "-y", "-loglevel", "error", "-framerate", "4", "-i", str(frames / "f_%04d.png"),
                        "-vf", "scale=trunc(iw/2)*2:trunc(ih/2)*2", "-c:v", "libx264", "-pix_fmt", "yuv420p", str(clip)],
                       check=False, **no_activate())
        if clip.is_file():
            print(f"[admin-tab] clip: {clip}")

    failed = [w for ok, w in results if not ok]
    print(f"[admin-tab] {len(results) - len(failed)}/{len(results)} passed")
    return 0 if not failed else 1
