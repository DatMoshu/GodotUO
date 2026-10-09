#!/usr/bin/env python3
"""Say whether a shard accepts a login: the login server's answer to one 0x80 packet, nothing more.

    login_probe.py --host H --port P --account A     (the password is read from the env var named by --password-env)

Exit 0 when the shard answers with its server list (0xA8), 1 when it refuses (0x82, with the reason), 2 when it
cannot be reached or answers with anything else. The password is never an argument and is never printed. Meant for
the rehearsal of a shard plan: "an unknown account is refused", "the owner account logs in".
"""

from __future__ import annotations

import argparse
import os
import socket
import struct
import sys

DENIED = {0: "no account with that name (or auto creation is off)", 1: "account in use", 2: "account blocked",
          3: "bad password", 4: "idle", 5: "bad communication"}


def login(host: str, port: int, account: str, password: str, version=(7, 0, 61, 0), timeout=10.0) -> tuple[str, int]:
    """("ok", 0) on a server list, ("denied", reason) on 0x82; raises OSError if nothing sensible comes back."""
    with socket.create_connection((host, port), timeout=timeout) as s:
        s.settimeout(timeout)
        s.sendall(b"\xef" + struct.pack(">I", 0x7F000001) + struct.pack(">4I", *version))
        s.sendall(b"\x80" + account.encode("ascii")[:30].ljust(30, b"\0") + password.encode("ascii")[:30].ljust(30, b"\0") + b"\x5d")
        first = s.recv(1)
        if first == b"\xa8":
            return "ok", 0
        if first == b"\x82":
            return "denied", s.recv(1)[0]
    raise OSError(f"unexpected answer {first!r} from the login server")


def main(argv: list[str] | None = None) -> int:
    ap = argparse.ArgumentParser(description=__doc__.splitlines()[0])
    ap.add_argument("--host", default="127.0.0.1")
    ap.add_argument("--port", type=int, default=2593)
    ap.add_argument("--account", required=True)
    ap.add_argument("--password-env", default="MUO_PROBE_PASSWORD", help="env var holding the password (default %(default)s)")
    a = ap.parse_args(argv)
    password = os.environ.get(a.password_env, "")
    try:
        kind, reason = login(a.host, a.port, a.account, password)
    except OSError as e:
        print(f"login_probe: {e}", file=sys.stderr)
        return 2
    if kind == "ok":
        print(f"login_probe: {a.account}: accepted")
        return 0
    print(f"login_probe: {a.account}: refused ({DENIED.get(reason, reason)})")
    return 1


if __name__ == "__main__":
    sys.exit(main())
