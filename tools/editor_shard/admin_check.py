"""The bridge's admin channel, checked live on the running private instance (ADR-0035).

    python tools/editor_shard/run.py admin-check

Talks to the bridge as an editor would (JSON lines on 127.0.0.1:<bridge port>)
and checks, in order:

1. a plain hello (no token) is answered with admin null, and map editing
   still works without the token (a `mobiles` query);
2. an admin op without the token is refused;
3. a wrong token is refused (admin_error), and the admin op after it too;
4. the right token is granted, `admin_whoami` names the level and the ops, and
   `admin_audit` returns entries with the token masked;
5. a connection is closed after three refused tokens;
6. neither the token nor a password reaches the shard log or the audit log.

Prints one line per check and exits 0 when all pass. The token is read from
the configuration and never printed.
"""

from __future__ import annotations

import json
import socket
from pathlib import Path


class Bridge:
    def __init__(self, port: int, timeout: float = 10.0):
        self.sock = socket.create_connection(("127.0.0.1", port), timeout=timeout)
        self.reader = self.sock.makefile("r", encoding="utf-8", newline="\n")

    def send(self, msg: dict) -> None:
        self.sock.sendall((json.dumps(msg) + "\n").encode("utf-8"))

    def reply(self, op: str) -> dict | None:
        """The next message with this op (relays from other editors are skipped); None if the bridge closed."""
        while True:
            try:
                line = self.reader.readline()
            except OSError:
                return None
            if not line:
                return None
            msg = json.loads(line)
            if msg.get("op") == op:
                return msg

    def ask(self, msg: dict) -> dict | None:
        self.send(msg)
        return self.reply(msg["op"])

    def close(self) -> None:
        try:
            self.sock.close()
        except OSError:
            pass


def run(port: int, token: str, shard_home: Path, secrets: list[str]) -> int:
    results: list[tuple[bool, str]] = []

    def check(ok: bool, what: str) -> None:
        results.append((bool(ok), what))
        print(f"[admin-check] {'PASS' if ok else 'FAIL'}  {what}")

    if not token:
        print("[admin-check] no admin token configured (UO_BRIDGE_ADMIN_TOKEN); run the private shard's start first")
        return 2

    b = Bridge(port)
    hello = b.ask({"op": "hello", "editor": "admin-check plain"})
    check(hello is not None and hello.get("admin") is None and "admin_error" not in hello,
          "a plain hello is answered, with no admin level and no refusal")
    mobiles = b.ask({"op": "mobiles", "facet": 0, "x0": 1400, "y0": 1550, "x1": 1500, "y1": 1650, "req": 1})
    check(mobiles is not None and mobiles.get("ok") is True, "map editing needs no token (mobiles answered ok)")
    who = b.ask({"op": "admin_whoami", "req": 2})
    check(who is not None and who.get("ok") is False and "admin token" in who.get("error", ""),
          f"an admin op without the token is refused ({who and who.get('error')})")
    b.close()

    b = Bridge(port)
    bad = b.ask({"op": "hello", "editor": "admin-check wrong", "admin_token": "x" * len(token)})
    check(bad is not None and bad.get("admin") is None and bad.get("admin_error") == "admin token refused",
          "a wrong token is refused in hello")
    who = b.ask({"op": "admin_whoami", "req": 3})
    check(who is not None and who.get("ok") is False, "and the admin op after it is refused")
    b.close()

    b = Bridge(port)
    good = b.ask({"op": "hello", "editor": "admin-check", "admin_token": token})
    check(good is not None and good.get("admin") and "admin_whoami" in (good.get("admin_ops") or []),
          f"the right token is granted ({good and good.get('admin')}, ops {good and good.get('admin_ops')})")
    who = b.ask({"op": "admin_whoami", "req": 4})
    check(who is not None and who.get("ok") is True and who.get("req") == 4 and who.get("editor") == "admin-check"
          and who.get("level") == good.get("admin"), "admin_whoami names the editor and its level")
    audit = b.ask({"op": "admin_audit", "count": 20, "req": 5})
    entries = (audit or {}).get("entries") or []
    hellos = [e for e in entries if e.get("op") == "hello"]
    check(audit is not None and audit.get("ok") is True and entries
          and all({"at", "editor", "op", "level", "ok"} <= set(e) for e in entries),
          f"admin_audit returns {len(entries)} entries with who, what, when, level and outcome")
    check(any(e.get("ok") is False and e.get("error") == "admin token refused" for e in hellos)
          and all(e["args"].get("admin_token") == "***" for e in hellos if "admin_token" in e.get("args", {})),
          "the refused hello is audited, its token masked")
    check(any(e.get("op") == "admin_whoami" and e.get("ok") is False for e in entries),
          "the refused admin ops are audited")
    b.close()

    b = Bridge(port, timeout=20.0)
    closed = False
    for i in range(4):
        r = b.ask({"op": "hello", "editor": "admin-check guesser", "admin_token": f"guess{i}"})
        if r is None:
            closed = True
            break
    if not closed:
        closed = b.reply("hello") is None
    check(closed and i == 3, f"a connection is closed after three refused tokens (closed on try {i + 1})")
    b.close()

    texts = []
    for f in (shard_home / "shard.log", shard_home / "Logs" / "GUO" / "admin_audit.jsonl"):
        texts.append(f.read_text(encoding="utf-8", errors="replace") if f.is_file() else "")
    check(bool(texts[1]), "the audit log file exists on the server")
    leaked = [s for s in [token, *secrets] if s and any(s in t for t in texts)]
    check(not leaked, f"no token or password in the shard log or the audit log ({len([token, *secrets])} secrets checked)")

    failed = [w for ok, w in results if not ok]
    print(f"[admin-check] {len(results) - len(failed)}/{len(results)} passed")
    return 0 if not failed else 1
