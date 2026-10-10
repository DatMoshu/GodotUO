"""The backup op (Admin tab, AD6), checked live on the running private instance; part of admin-check.

admin_backup "list" answers every GUO snapshot under the server's Backups/GUO,
newest first; "now" saves the world, copies the save folder into a new
snapshot (a manifest beside it, naming no folder) and removes the oldest beyond
"keep"; "restore" refuses a name that is not a snapshot's (or would leave the
folder) and, for a real one, saves and keeps a before-restore snapshot first.
The restore itself (the save folder swapped with the server stopped) is the
editor's, proved by admin-tab. Health's admin_status names the last backup.
None of it runs without the token, and the audit log has every one.

Snapshots it makes stay on the private shard, the last keep-1 run leaving one.
"""

from __future__ import annotations

import json
import re
from pathlib import Path

NAME = re.compile(r"^\d{4}-\d\d-\d\d_\d{6}Z(_before-restore)?(_\d{1,3})?$")


def run(check, bridge_cls, port: int, token: str, shard_home: Path) -> None:
    b = bridge_cls(port, timeout=120.0)
    b.ask({"op": "hello", "editor": "admin-check backups plain"})
    r = b.ask({"op": "admin_backup", "action": "list", "req": 1})
    check(r is not None and r.get("ok") is False and "admin token" in r.get("error", ""), "admin_backup without the token is refused")
    b.close()

    b = bridge_cls(port, timeout=120.0)
    hello = b.ask({"op": "hello", "editor": "admin-check backups", "admin_token": token})
    check(hello is not None and "admin_backup" in (hello.get("admin_ops") or []), "the admin hello offers admin_backup")

    listed = b.ask({"op": "admin_backup", "action": "list", "req": 2}) or {}
    check(listed.get("ok") is True and isinstance(listed.get("snapshots"), list) and listed.get("saves_path") and listed.get("backup_path"),
          f"admin_backup list answers {len(listed.get('snapshots') or [])} snapshots and where they and the save are")

    now = b.ask({"op": "admin_backup", "action": "now", "keep": 50, "req": 3}) or {}
    snap = now.get("snapshot") or {}
    folder = shard_home / "Backups" / "GUO" / str(snap.get("name"))
    manifest = folder / "guo_backup.json"
    manifest_text = manifest.read_text(encoding="utf-8") if manifest.is_file() else ""
    check(now.get("ok") is True and now.get("req") == 3 and NAME.match(snap.get("name") or "") and (snap.get("files") or 0) > 0
          and snap.get("reason") == "manual" and folder.is_dir() and any(folder.rglob("*.bin")),
          f"admin_backup now saved ({now.get('save_ms')} ms) and kept {snap.get('name')}: {snap.get('files')} files, "
          f"{snap.get('bytes')} bytes, copied in {now.get('copy_ms')} ms")
    check(manifest_text and str(shard_home) not in manifest_text and str(shard_home).replace("\\", "\\\\") not in manifest_text
          and json.loads(manifest_text).get("name") == snap.get("name"), "the snapshot's manifest names it and no folder")
    rows = now.get("snapshots") or []
    check(rows and rows[0].get("name") == snap.get("name"), "the reply's list has it first")
    st = b.ask({"op": "admin_status", "req": 4}) or {}
    check(st.get("last_backup_name") == snap.get("name") and (st.get("last_backup_s") or 0) < 120,
          f"admin_status names the last backup ({st.get('last_backup')}, {st.get('last_backup_s')} s ago)")

    bad = b.ask({"op": "admin_backup", "action": "delete", "req": 5}) or {}
    check(bad.get("ok") is False and "list, now or restore" in bad.get("error", ""), "an unknown admin_backup action is refused in plain words")
    bad = b.ask({"op": "admin_backup", "action": "restore", "name": "../Saves", "req": 6}) or {}
    check(bad.get("ok") is False and "not a backup's name" in bad.get("error", ""), "a restore of a name that would leave the folder is refused")
    bad = b.ask({"op": "admin_backup", "action": "restore", "name": "2001-01-01_000000Z", "req": 7}) or {}
    check(bad.get("ok") is False and "no backup called" in bad.get("error", ""), "a restore of a backup that is not there is refused")

    restore = b.ask({"op": "admin_backup", "action": "restore", "name": snap.get("name"), "keep": 50, "req": 8}) or {}
    before = restore.get("snapshot") or {}
    check(restore.get("ok") is True and restore.get("target") == snap.get("name") and before.get("reason") == "before-restore"
          and (before.get("name") or "").endswith("_before-restore"),
          f"admin_backup restore keeps {before.get('name')} of the world as it is first (the editor swaps the save, server stopped)")

    kept = b.ask({"op": "admin_backup", "action": "now", "keep": 1, "req": 9}) or {}
    names = [s.get("name") for s in kept.get("snapshots") or []]
    pruned = kept.get("pruned") or []
    check(kept.get("ok") is True and len(names) == 1 and names[0] == (kept.get("snapshot") or {}).get("name")
          and snap.get("name") in pruned and before.get("name") in pruned
          and not (shard_home / "Backups" / "GUO" / str(snap.get("name"))).exists(),
          f"keep 1 removed the older snapshots ({len(pruned)}) from the list and the disk")

    audit = b.ask({"op": "admin_audit", "count": 30, "req": 10}) or {}
    entries = [e for e in audit.get("entries") or [] if e.get("op") == "admin_backup"]
    check(any(e.get("ok") is True and e.get("args", {}).get("action") == "now" for e in entries)
          and any(e.get("ok") is False and e.get("args", {}).get("action") == "restore" for e in entries),
          f"the audit log has the backups and the refused restores ({len(entries)} entries)")
    b.close()
