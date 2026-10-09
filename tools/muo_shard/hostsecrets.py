"""`run.py secrets`: write a host's /etc/muo/<id>.env over ssh, never through argv or a local file.

The values are asked for at the terminal and travel on ssh's stdin. The remote command is fixed
text (a small python merge program and the env file's path); it holds no value. The merge keeps
keys the operator did not give, so a second run can change one value.
"""

from __future__ import annotations

import getpass
import re
import shlex
import subprocess
import sys

from shardprofile import Profile

HOST_RE = re.compile(r"^[A-Za-z0-9_][A-Za-z0-9_.@:\[\]-]{0,252}$")   # no leading '-': never an ssh option

# Reads KEY=VALUE lines from stdin, merges them into the file (mode 0600), replaces it atomically.
_MERGE = """\
import os, sys
path = sys.argv[1]
new = dict(l.rstrip("\\n").split("=", 1) for l in sys.stdin if "=" in l)
old, order = {}, []
if os.path.exists(path):
    for l in open(path, encoding="utf-8"):
        k, sep, v = l.rstrip("\\n").partition("=")
        if sep and k not in old:
            order.append(k)
        if sep:
            old[k] = v
for k, v in new.items():
    if k not in old:
        order.append(k)
    old[k] = "'" + v + "'"
tmp = path + ".new"
fd = os.open(tmp, os.O_WRONLY | os.O_CREAT | os.O_TRUNC, 0o600)
with os.fdopen(fd, "w", encoding="utf-8", newline="\\n") as f:
    for k in order:
        f.write(k + "=" + old[k] + "\\n")
os.replace(tmp, path)
os.chmod(path, 0o600)
print("muo_shard: wrote " + ", ".join(sorted(new)) + " to " + path, file=sys.stderr)
"""


# Passwords anyone can read in GUO's history; patch 0001 makes and raises no staff account with one.
PUBLISHED_PASSWORDS = ("guoprobe",)


class SecretsError(Exception):
    pass


def check_host(host: str) -> str:
    if not HOST_RE.fullmatch(host or ""):
        raise SecretsError(f"--host: {host!r} is not a host name or user@host")
    return host


def check_value(key: str, value: str) -> str:
    # The file is read by systemd and by a shell: single-quoted, so a quote or a line break cannot be carried.
    if any(ch in value for ch in "'\n\r\0"):
        raise SecretsError(f"{key}: a value may not hold a single quote, a line break or a NUL")
    return value


def remote_command(env_file: str) -> str:
    """What ssh runs on the host: no value in it, only the merge program and the file."""
    return f"sudo python3 -c {shlex.quote(_MERGE)} {shlex.quote(env_file)}"


def ask(prompt_fn=input, secret_fn=getpass.getpass, keys: dict[str, str] | None = None) -> dict[str, str]:
    """Prompt for the three host values; a blank answer leaves that key as it is."""
    keys = keys or {}
    values: dict[str, str] = {}
    data = prompt_fn(f"{keys['client_data']} (UO install folder on the host, blank = keep): ").strip()
    user = prompt_fn(f"{keys['admin_user']} (owner account name, blank = keep): ").strip()
    if data:
        values[keys["client_data"]] = check_value(keys["client_data"], data)
    if user:
        values[keys["admin_user"]] = check_value(keys["admin_user"], user)
    pw = secret_fn(f"{keys['admin_password']} (hidden, blank = keep): ")
    if pw:
        if secret_fn("again: ") != pw:
            raise SecretsError("the two passwords differ; nothing was sent")
        if pw.lower() in PUBLISHED_PASSWORDS or (user and pw.lower() == user.lower()):
            raise SecretsError(f"{keys['admin_password']} is a published default or the account name; "
                               "the shard would make no owner with it. Nothing was sent")
        values[keys["admin_password"]] = check_value(keys["admin_password"], pw)
    return values


def send(p: Profile, host: str, values: dict[str, str], ssh: str = "ssh") -> int:
    from plans import Ctx

    c = Ctx(p)
    check_host(host)
    if not values:
        raise SecretsError("nothing to send")
    payload = "".join(f"{k}={v}\n" for k, v in values.items())
    argv = [ssh, "--", host, remote_command(c.env_file)]
    r = subprocess.run(argv, input=payload, text=True)
    return r.returncode


def run(p: Profile, host: str, ssh: str = "ssh", prompt_fn=input, secret_fn=getpass.getpass) -> int:
    from plans import Ctx

    check_host(host)
    if not sys.stdin.isatty() and prompt_fn is input:
        raise SecretsError("secrets prompts at a terminal; it takes no values from a pipe or the command line")
    values = ask(prompt_fn, secret_fn, Ctx(p).keys)
    return send(p, host, values, ssh)
