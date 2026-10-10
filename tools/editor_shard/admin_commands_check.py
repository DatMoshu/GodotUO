"""The Commands ops (Admin tab, AD3), checked live on the running private instance; part of admin-check.

admin_commands lists the server's commands with access level, usage,
description and the dangerous list's kind; admin_command runs one with nobody
logged in, as the bridge's hidden admin mobile, and returns its output (MUO
patch 0004). A dangerous command (shutdown, wipe, delete accounts, global
decorate, mass moves) is refused without its typed confirm word and with a
wrong one; [Wipe with its word asks for a target, which the hidden mobile
cannot give, so it is cancelled and nothing is wiped. "as" naming nobody
online is refused, and the old "command" op needs the token and "as". The
hidden mobile is never in the world (Find does not see it). A command line
holding a password reaches the audit with its name only.

Returns the made-up password it typed, for admin-check's leak grep.
"""

from __future__ import annotations

import uuid


def run(check, bridge_cls, port: int, token: str) -> list[str]:
    fake_password = "Pw" + uuid.uuid4().hex[:12]

    b = bridge_cls(port, timeout=30.0)
    b.ask({"op": "hello", "editor": "admin-check commands plain"})
    for op, msg in (("admin_commands", {}), ("admin_command", {"text": "[where"}), ("command", {"as": "Nobody", "text": "[where"})):
        r = b.ask({"op": op, "req": 1, **msg}) or {}
        check(r.get("ok") is False and "admin token" in r.get("error", ""), f"{op} without the token is refused")
    b.close()

    b = bridge_cls(port, timeout=30.0)
    hello = b.ask({"op": "hello", "editor": "admin-check commands", "admin_token": token}) or {}
    offered = hello.get("admin_ops") or []
    check(all(op in offered for op in ("admin_commands", "admin_command", "command")),
          "the admin hello offers admin_commands, admin_command and command")

    listed = b.ask({"op": "admin_commands", "req": 2}) or {}
    rows = {r["name"].lower(): r for r in listed.get("commands") or []}
    where = rows.get("where") or {}
    check(listed.get("ok") is True and len(rows) > 50 and where.get("access") == "Counselor" and where.get("usage") == "Where"
          and "coordinates" in (where.get("description") or ""),
          f"admin_commands lists {len(rows)} commands with access level, usage and description ([Where: {where.get('access')})")
    check(listed.get("output_available") is True, "the server has the output hook (MUO patch 0004)")
    check((rows.get("wipe") or {}).get("danger") == "wipe" and where.get("danger") is None
          and not any("<" in (r.get("description") or "") for r in rows.values()),
          "each command says whether it is on the dangerous list; descriptions are plain text")
    check(all(r.get("access") not in ("Developer", "Owner") for r in rows.values()),
          "only the commands this connection's level may run are listed")

    r = b.ask({"op": "admin_command", "text": "[where", "req": 3}) or {}
    out = r.get("output") or []
    check(r.get("ok") is True and r.get("req") == 3 and r.get("as") is None and r.get("known") is True
          and any(line.startswith("You are at ") and "Internal" in line for line in out),
          f"[where runs with nobody online, as the hidden admin mobile, and its output comes back: {out[:1]}")
    r = b.ask({"op": "admin_command", "text": "  where ", "req": 4}) or {}
    check(r.get("ok") is True and r.get("text") == "[where" and any(line.startswith("You are at ") for line in r.get("output") or []),
          "a command typed without the prefix runs the same")
    r = b.ask({"op": "admin_command", "text": "[nosuchcommand", "req": 5}) or {}
    check(r.get("ok") is True and r.get("known") is False and "That is not a valid command." in (r.get("output") or []),
          "an unknown command answers as in game: That is not a valid command.")
    r = b.ask({"op": "admin_command", "text": "[where\n[wipe", "req": 6}) or {}
    check(r.get("ok") is False and r.get("error") == "a command is one line", "a second line is refused")

    r = b.ask({"op": "admin_command", "text": "[wipe", "req": 7}) or {}
    danger = r.get("danger") or {}
    check(r.get("ok") is False and danger.get("kind") == "wipe" and danger.get("confirm") == "wipe" and danger.get("why"),
          f"[wipe without its confirm word is refused: {r.get('error')}")
    r = b.ask({"op": "admin_command", "text": "[wipe", "confirm": "yes", "req": 8}) or {}
    check(r.get("ok") is False and (r.get("danger") or {}).get("confirm") == "wipe", "and with a wrong word")
    for line, word in (("[restart", "restart"), ("[global delete where Item", "global delete"), ("[decorate", "decorate"),
                       ("[area set hue 5", "area set")):
        r = b.ask({"op": "admin_command", "text": line, "req": 9}) or {}
        check(r.get("ok") is False and (r.get("danger") or {}).get("confirm") == word,
              f"{line} is on the dangerous list, confirmed by '{word}'")
    r = b.ask({"op": "admin_command", "text": "[Wipe", "confirm": "WIPE", "req": 10}) or {}
    check(r.get("ok") is True and r.get("needs_target") is True and r.get("known") is True
          and any("target" in line for line in r.get("output") or []),
          "[Wipe with its word runs, asks for a target, and the hidden mobile's cursor is cancelled: nothing is wiped")

    r = b.ask({"op": "admin_command", "text": "[where", "as": "NoSuchStaffCharacter", "req": 11}) or {}
    check(r.get("ok") is False and "not online" in r.get("error", ""), "'run as' a character who is not online is refused")
    r = b.ask({"op": "command", "text": "[where", "req": 12}) or {}
    check(r.get("ok") is False and "'as'" in r.get("error", ""), "the old command op still runs only as a named character")
    r = b.ask({"op": "command", "as": "NoSuchStaffCharacter", "text": "[where", "req": 13}) or {}
    check(r.get("ok") is False and "not online" in r.get("error", ""), "and with the token it refuses one who is not online")

    r = b.ask({"op": "admin_command", "text": f"[password {fake_password} {fake_password}", "req": 14}) or {}
    check(r.get("ok") is True and r.get("text") == "[password ***", "a command line with a password is answered with its name only")
    found = b.ask({"op": "admin_godview_find", "text": "Admin tab", "req": 15}) or {}
    check(found.get("ok") is True and not found.get("matches"), "the hidden admin mobile is not in the world (Find does not see it)")

    entries = (b.ask({"op": "admin_audit", "count": 200, "req": 16}) or {}).get("entries") or []
    ran = [e for e in entries if e.get("op") == "admin_command"]
    check(any(e.get("ok") and (e.get("args") or {}).get("text") == "[where" for e in ran)
          and any(not e.get("ok") and (e.get("args") or {}).get("text") == "[wipe" for e in ran)
          and any((e.get("args") or {}).get("text") == "[password ***" for e in ran)
          and not any(fake_password in str(e) for e in entries),
          f"every command run or refused is audited ({len(ran)} entries), a password command by its name only")
    b.close()
    return [fake_password]
