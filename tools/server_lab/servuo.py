"""The ServUO lab row: fetch at the pin, build, configure on loopback, write the lab admin before the first boot.

The lab's copy is its own, separate from the private ServUO shard of tools/servuo (tools/servuo/src): its home is the
lab profile's folder in the workspace, `servers/<id>/src`, with its own Config and Saves. It uses the same pin
(backends.json `lab`, which matches tools/servuo/run.py) and the same configuration lines (tools/servuo/config), with
the lab's port and shard name. ServUO is not patched.

The admin: ServUO asks on its console for an owner account when it boots with no accounts, and the server manager
gives a server no console input. So before the first boot the lab writes `Saves/Accounts/accounts.xml` with the lab
admin as Owner; ServUO reads a plain `password` element and hashes it at its first world save (upstream
Scripts/Accounting/Account.cs, `Account(XmlElement)` and `Save`).
"""

from __future__ import annotations

import importlib.util
import subprocess
from datetime import datetime, timezone
from pathlib import Path
from xml.sax.saxutils import escape

ROOT = Path(__file__).resolve().parents[2]


class SetupError(RuntimeError):
    pass


def _servuo_tool():
    spec = importlib.util.spec_from_file_location("guo_servuo_tool", ROOT / "tools" / "servuo" / "run.py")
    module = importlib.util.module_from_spec(spec)
    spec.loader.exec_module(module)
    return module


def _git(src: Path, *args: str, check: bool = True) -> subprocess.CompletedProcess:
    result = subprocess.run(["git", "-C", str(src), *args], capture_output=True, text=True)
    if check and result.returncode != 0:
        raise SetupError(f"git {' '.join(args[:2])} failed: {result.stderr.strip()[:300]}")
    return result


def dist(src: Path) -> Path:
    return src


def executable(src: Path) -> Path:
    return src / "ServUO.exe"


def fetch(src: Path, repo: str, commit: str, say=print) -> None:
    """A shallow fetch of the pinned commit, as tools/servuo does. A checkout at another commit is refused, never
    reset."""
    if (src / ".git").is_dir():
        head = _git(src, "rev-parse", "-q", "--verify", "HEAD", check=False).stdout.strip()
        if head == commit:
            return
        if head:
            raise SetupError(f"the lab checkout is at {head[:9]}, not the pin {commit[:9]}; remove its src folder to fetch again")
    else:
        src.mkdir(parents=True, exist_ok=True)
        _git(src, "init", "-q")
        _git(src, "remote", "add", "origin", repo)
    say(f"fetching {repo} at {commit[:9]} (shallow)")
    _git(src, "fetch", "-q", "--depth", "1", "origin", commit)
    _git(src, "checkout", "-q", "--detach", "FETCH_HEAD")
    say(f"at the pin {commit[:9]}")


def build(src: Path, say=print, log: Path | None = None) -> None:
    """dotnet build of ServUO.sln, Release: SDK-style net48 projects, ServUO.exe in the checkout's root."""
    if not (src / "ServUO.sln").is_file():
        raise SetupError("not fetched yet")
    say("building (dotnet build ServUO.sln -c Release)")
    out = log.open("w", encoding="utf-8", errors="replace") if log else subprocess.DEVNULL
    try:
        code = subprocess.run(["dotnet", "build", str(src / "ServUO.sln"), "-c", "Release", "-nologo"], cwd=str(src),
                              stdout=out, stderr=subprocess.STDOUT, stdin=subprocess.DEVNULL).returncode
    finally:
        if log:
            out.close()
    if code != 0 or not executable(src).is_file():
        raise SetupError(f"build failed (exit {code})" + (f"; see {log.name}" if log else ""))


def configure(src: Path, client_data: Path, shard_name: str, port: int) -> list[str]:
    """tools/servuo/config over src/Config with the lab's port and name: listener and advertised address 127.0.0.1,
    put back at every call. Returns the files it wrote."""
    if not client_data.is_dir():
        raise SetupError("UO_CLIENT_DATA does not exist")
    lines = _servuo_tool().write_config(src, {"CLIENT_DATA": str(client_data), "PORT": str(port), "NAME": shard_name})
    return [line.split(":", 1)[0] for line in lines]


def accounts_file(src: Path) -> Path:
    return src / "Saves" / "Accounts" / "accounts.xml"


def accounts_xml(account: str, password: str, now: datetime | None = None) -> str:
    when = (now or datetime.now(timezone.utc)).strftime("%Y-%m-%dT%H:%M:%S.0000000Z")
    return ('<?xml version="1.0" encoding="utf-8" standalone="yes"?>\n'
            '<accounts count="1">\n'
            '  <account>\n'
            f'    <username>{escape(account)}</username>\n'
            f'    <password>{escape(password)}</password>\n'
            '    <accessLevel>Owner</accessLevel>\n'
            f'    <created>{when}</created>\n'
            f'    <lastLogin>{when}</lastLogin>\n'
            '    <totalGameTime>PT0S</totalGameTime>\n'
            '    <chars />\n'
            '  </account>\n'
            '</accounts>\n')


def ensure_admin(src: Path, account: str, password: str) -> bool:
    """Write the lab admin as the only account when the world has no accounts yet; an existing accounts.xml is
    ServUO's and is left alone. Returns whether it wrote."""
    path = accounts_file(src)
    if path.exists():
        return False
    path.parent.mkdir(parents=True, exist_ok=True)
    path.write_text(accounts_xml(account, password), encoding="utf-8", newline="\n")
    return True


def server_env(base: dict[str, str], account: str, password: str) -> dict[str, str]:
    """ServUO reads no settings from the environment; the dev shard's accounts are kept out all the same."""
    return {k: v for k, v in base.items()
            if k not in ("UO_SHARD_GM_ACCOUNTS", "UO_SHARD_GM_PASSWORD", "UO_SHARD_OWNER", "UO_SHARD_OWNER_PASSWORD")}
