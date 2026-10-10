"""The Admin tab, driven in a real editor against the private instance (sprint "Admin tab", AD1, AD2a, AD2b, AD4, AD5).

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
6. the Settings form (AD4) reads the shard's configuration with plain labels
   and its secrets masked, marks values out of range and will not save them,
   then Save and restart writes one account fewer per address, saves every
   7 minutes and a test mail password, keeping the previous files; the
   restarted server reports the new values, the password is in the scratch
   workspace's secrets file and the server's own file, and not in the
   previous copies;
7. the Accounts list (AD5) shows every account with its level, last login
   and characters, keeps the shard's owner out of reach (the hint says why),
   makes an account with a generated 16-character password, gives it the
   Counselor level and a typed password (one too short is not sent), bans it
   and lifts the ban, and reads the list again;
8. the run bar stops the server.

The shard's Configuration folder is copied aside first and put back after, so
the check leaves the private shard's settings as they were.

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
import secrets as secret_source
import shutil
import subprocess
import time
from pathlib import Path

from guo.process import build_child_env, no_activate

TIMEOUT_S = 900
# The story the evidence is filed under (rule: evidence_naming.md).
STORY = "AD5"
STILLS = ("connected", "saved", "restarted", "godview", "spawner", "filtered", "respawn", "cleared",
          "settings-form", "settings-diff", "settings-saved", "accounts-list", "accounts-created", "accounts-banned")


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
    # The Settings form's test mail password (AD4): made here so the leak grep below knows what to look for. The
    # environment must not carry a real one, which would win over the form's secrets file.
    mail_secret = "Ad4" + "".join(secret_source.choice("ABCDEFGHJKLMNPQRSTUVWXYZabcdefghijkmnpqrstuvwxyz23456789") for _ in range(17))
    env["GUO_SMOKE_SETTINGS_SECRET"] = mail_secret
    env.pop("UO_SHARD_EMAIL_PASSWORD", None)
    # The Accounts list's typed password (AD5), for the same leak grep; the generated one never leaves the editor.
    account_secret = "".join(secret_source.choice("ABCDEFGHJKLMNPQRSTUVWXYZabcdefghijkmnpqrstuvwxyz23456789") for _ in range(14))
    env["GUO_SMOKE_ACCOUNT_SECRET"] = account_secret
    # The form writes the shard's own files: keep them to put back after.
    config_dir = shard_home / "Configuration"
    config_kept = out / "configuration_before"
    shutil.copytree(config_dir, config_kept)
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
        # The previous copies the form kept, for the leak grep, before the shard's own settings go back.
        previous = config_dir / "GUO-previous"
        previous_texts = [f.read_text(encoding="utf-8", errors="replace") for f in previous.rglob("*.json")] if previous.is_dir() else []
        mail_in_config = any(mail_secret in f.read_text(encoding="utf-8", errors="replace")
                             for f in config_dir.glob("*.json"))
        shutil.rmtree(config_dir)
        shutil.copytree(config_kept, config_dir)
        shutil.rmtree(config_kept)

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
    check(admin.get("settings_labels_ok") and admin.get("settings_secrets_masked"),
          f"the Settings form read {admin.get('settings_fields')} settings with plain labels and every secret masked "
          f"(not there yet: {admin.get('settings_missing_files') or 'none'})")
    problems = admin.get("settings_problems") or {}
    check(admin.get("settings_invalid_refused"),
          "the form marked 0 accounts and \"soon\" and would not save: " + "; ".join(problems.values()))
    check(admin.get("settings_diff_ok") and admin.get("settings_save_enabled") and mail_secret not in str(admin.get("settings_diff")),
          "the list of changes names all three, the password only as changed: "
          + " | ".join(ln for ln in str(admin.get("settings_diff") or "").splitlines() if ln.strip()))
    live = json.loads(admin["settings_live"]) if admin.get("settings_live") else {}
    values = live.get("values") or {}
    check(admin.get("settings_live_matches") and values.get("autosave.saveDelay") == "00:07:00"
          and values.get("accountHandler.maxAccountsPerIP") == str(admin.get("settings_max_accounts_target")),
          f"Save and restart wrote {admin.get('settings_written')}; the restarted server runs with "
          f"{admin.get('settings_max_accounts_before')} -> {values.get('accountHandler.maxAccountsPerIP')} accounts per address, "
          f"saves every {admin.get('settings_save_delay_before')} -> {values.get('autosave.saveDelay')}")
    check(admin.get("settings_previous_ok") and previous_texts and not any(mail_secret in t for t in previous_texts),
          f"the previous files are kept in {admin.get('settings_previous')} ({admin.get('settings_previous_files')}), "
          f"without the password")
    check(admin.get("settings_secret_in_secrets_file") and admin.get("settings_secret_in_server_file") and mail_in_config,
          "the mail password is in the scratch workspace's secrets file and the server's email-settings.json only")
    check(admin.get("settings_form_reloaded"), f"the form read the saved files back: \"{admin.get('settings_status_saved')}\"")
    restored = not (config_dir / "GUO-previous").exists() and not any(
        mail_secret in f.read_text(encoding="utf-8", errors="replace") for f in config_dir.glob("*.json"))
    check(restored, "the shard's own Configuration is back as it was")
    check(admin.get("accounts_rows_ok"), f"the Accounts list shows all {admin.get('accounts_listed')} accounts with level, last login and characters")
    check(admin.get("accounts_owner_blocked"), f"the shard's owner is out of the tab's reach: \"{admin.get('accounts_owner_hint')}\"")
    check(admin.get("accounts_generated_fits"), "a generated password has 16 characters, the login box's size; 17 is not sent")
    check(admin.get("accounts_created") and admin.get("accounts_access_set"),
          f"the tab made '{admin.get('accounts_name')}' with a generated password and made it a Counselor")
    check(admin.get("accounts_typed_refused_short") and admin.get("accounts_password_set"),
          "the tab gave it a typed password, and would not send one too short")
    check(admin.get("accounts_banned") and admin.get("accounts_unbanned") and admin.get("accounts_listed_after"),
          "the tab banned it and lifted the ban; the list read again agrees")
    check(admin.get("account_password_in_tab_log") is False, "neither account password is in the tab's log")
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
    texts.append(json.dumps(report))
    all_secrets = [token, *secrets, mail_secret, account_secret]
    leaked = [s for s in all_secrets if s and any(s in t for t in texts)]
    check(not leaked, f"no token or password (the test mail and account passwords among them) in the editor output, the tab's log, "
                      f"the server console, the audit log or the report ({len([s for s in all_secrets if s])} secrets, {len(texts)} files)")

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
