"""The Accounts ops (Admin tab, AD5), checked live on the running private instance; part of admin-check.

admin_accounts lists every account; admin_account makes one, sets its access
level, gives it a new password, bans and unbans it. Each is proved on the
server too: the shard's own login server (tools/muo_shard/login_probe.py, one
0x80 packet) takes the made account with its generated 16-character password,
refuses the old password after a reset, refuses the account while it is
banned, and takes it again after. Refusals are checked in plain words: a name
or password the login screen cannot hold, a level at or above the tab's own,
an account at or above it (the shard's owner), an unknown action or account.
The audit log has every one, the password masked.

The account made here is named ad5c<6 digits> (no person's name reaches the
evidence) and is left on the private shard: the Admin tab cannot delete
accounts (deleting is on AD3's dangerous list).
"""

from __future__ import annotations

import json
import secrets as secret_source
import string
import sys
from pathlib import Path

sys.path.insert(0, str(Path(__file__).resolve().parents[1] / "muo_shard"))
from login_probe import login  # noqa: E402

LETTERS = string.ascii_letters + string.digits


def password(length: int = 16) -> str:
    return "".join(secret_source.choice(LETTERS) for _ in range(length))


def run(check, cfg, bridge_cls, port: int, token: str, shard_port: int) -> list[str]:
    """Runs the account checks; returns the passwords it used, for the caller's leak grep."""
    used: list[str] = []

    b = bridge_cls(port)
    b.ask({"op": "hello", "editor": "admin-check plain"})
    r = b.ask({"op": "admin_accounts", "req": 40})
    check(r is not None and r.get("ok") is False and "admin token" in r.get("error", ""), "admin_accounts without the token is refused")
    r = b.ask({"op": "admin_account", "action": "ban", "account": "nobody", "req": 41})
    check(r is not None and r.get("ok") is False and "admin token" in r.get("error", ""), "admin_account without the token is refused")
    b.close()

    b = bridge_cls(port, timeout=30.0)
    hello = b.ask({"op": "hello", "editor": "admin-check", "admin_token": token})
    held = (hello or {}).get("admin")
    listed = b.ask({"op": "admin_accounts", "req": 42})
    rows = (listed or {}).get("accounts") or []
    fields = {"name", "access", "created", "last_login", "characters", "online", "banned", "protected"}
    owner = next((a for a in rows if a.get("name", "").lower() == (cfg.shard_owner or "").lower()), None)
    check(listed is not None and listed.get("ok") is True and rows and listed.get("count") == len(rows)
          and all(fields <= set(a) for a in rows) and all(str(a.get("last_login") or "Z").endswith("Z") for a in rows)
          and [a["name"].lower() for a in rows] == sorted(a["name"].lower() for a in rows),
          f"admin_accounts lists {len(rows)} accounts by name, each with access, created, last login (UTC), characters, "
          f"online, banned and protected")

    name = "ad5c" + "".join(secret_source.choice(string.digits) for _ in range(6))
    first = password()
    used.append(first)
    made = b.ask({"op": "admin_account", "action": "create", "account": name, "password": first, "access": "Player", "req": 43})
    row = (made or {}).get("row") or {}
    check(made is not None and made.get("ok") is True and row.get("name") == name and row.get("access") == "Player"
          and row.get("banned") is False and "password" not in made and first not in json.dumps(made),
          f"admin_account create makes '{name}' (Player) with a generated 16-character password; the reply holds no password")
    again = b.ask({"op": "admin_accounts", "req": 44})
    check(any(a.get("name") == name for a in (again or {}).get("accounts") or []) and again.get("count") == len(rows) + 1,
          "the list has it next time")
    check(login("127.0.0.1", shard_port, name, first) == ("ok", 0),
          "the shard's login server takes the new account with its generated password (16 characters, the login box's size)")

    for what, msg, want in (
        ("the same name again", {"action": "create", "account": name, "password": password()}, "already an account"),
        ("a name with a forbidden character", {"action": "create", "account": "ad5<x>", "password": password()}, "may not hold"),
        ("a name longer than the login box", {"action": "create", "account": "a" * 17, "password": password()}, "at most 16"),
        ("a password longer than the login box", {"action": "create", "account": name + "x", "password": password(17)}, "8 to 16"),
        ("the account's name as its password", {"action": "password", "account": name, "password": name}, "published"),
        ("a level at the tab's own", {"action": "access", "account": name, "access": held or "Administrator"}, "below its own"),
        ("the Owner level", {"action": "access", "account": name, "access": "Owner"}, "below its own"),
        ("a level that does not exist", {"action": "access", "account": name, "access": "King"}, "not an access level"),
        ("an account that does not exist", {"action": "ban", "account": "ad5-nobody"}, "no account"),
        ("an unknown action", {"action": "delete", "account": name}, "needs an action"),
    ):
        r = b.ask({"op": "admin_account", **msg, "req": 45})
        check(r is not None and r.get("ok") is False and want in (r.get("error") or ""),
              f"{what} is refused: \"{r and r.get('error')}\"")
    if owner is not None and held != "Owner":
        r = b.ask({"op": "admin_account", "action": "password", "account": owner["name"], "password": password(), "req": 46})
        check(r is not None and r.get("ok") is False and "below its own level" in (r.get("error") or ""),
              f"the shard's owner account ({owner.get('access')}) is out of this tab's reach: \"{r and r.get('error')}\"")
    else:
        check(False, f"the shard's owner account is in the list (UO_SHARD_OWNER), and the tab is not Owner ({held})")

    r = b.ask({"op": "admin_account", "action": "access", "account": name, "access": "Counselor", "req": 47})
    check(r is not None and r.get("ok") is True and (r.get("row") or {}).get("access") == "Counselor",
          f"admin_account access makes it a Counselor ({r and r.get('characters_changed')} characters changed with it)")
    second = password(12)
    used.append(second)
    r = b.ask({"op": "admin_account", "action": "password", "account": name, "password": second, "req": 48})
    check(r is not None and r.get("ok") is True and "password" not in r and second not in json.dumps(r),
          "admin_account password gives it a typed password; the reply holds no password")
    check(login("127.0.0.1", shard_port, name, first) == ("denied", 3) and login("127.0.0.1", shard_port, name, second) == ("ok", 0),
          "the login server refuses the old password now and takes the new one")
    r = b.ask({"op": "admin_account", "action": "ban", "account": name, "req": 49})
    check(r is not None and r.get("ok") is True and (r.get("row") or {}).get("banned") is True, "admin_account ban bans it")
    check(login("127.0.0.1", shard_port, name, second) == ("denied", 2), "the login server refuses it while it is banned (blocked)")
    r = b.ask({"op": "admin_account", "action": "unban", "account": name, "req": 50})
    check(r is not None and r.get("ok") is True and (r.get("row") or {}).get("banned") is False, "admin_account unban lifts it")
    check(login("127.0.0.1", shard_port, name, second) == ("ok", 0), "and the login server takes it again")

    audit = b.ask({"op": "admin_audit", "count": 40, "req": 51})
    entries = [e for e in (audit or {}).get("entries") or [] if e.get("op") == "admin_account" and e["args"].get("account") == name]
    with_password = [e for e in entries if "password" in e.get("args", {})]
    check(len(entries) >= 8 and any(e.get("ok") is False for e in entries)
          and with_password and all(e["args"]["password"] == "***" for e in with_password)
          and not any(p in json.dumps(audit) for p in used),
          f"every account change is audited ({len(entries)} for '{name}', refusals too), each password as ***")
    b.close()
    return used
