"""The ModernUO lab row: fetch at the pin, apply GUO's patches, build, configure on loopback.

The lab's copy is its own, separate from the dev shard (tools/modernuo/src): its home is the lab profile's folder in
the workspace, `servers/<id>/src`, with its own Configuration and Saves. It uses the same pin (backends.json `lab`,
which SV0 matched to UO_SHARD_REF) and the same patches (tools/modernuo/patches); a change to either is a MUO patch
and goes in tools/modernuo/UPSTREAM.md.
"""

from __future__ import annotations

import importlib.util
import json
import subprocess
from pathlib import Path

ROOT = Path(__file__).resolve().parents[2]
PATCHES = ROOT / "tools" / "modernuo" / "patches"
TEMPLATES = ROOT / "tools" / "modernuo" / "config"


class SetupError(RuntimeError):
    pass


def _git(src: Path, *args: str, check: bool = True) -> subprocess.CompletedProcess:
    result = subprocess.run(["git", "-C", str(src), *args], capture_output=True, text=True)
    if check and result.returncode != 0:
        raise SetupError(f"git {' '.join(args[:2])} failed: {result.stderr.strip()[:300]}")
    return result


def dist(src: Path) -> Path:
    return src / "Distribution"


def executable(src: Path) -> Path:
    return dist(src) / "ModernUO.exe"


def fetch(src: Path, repo: str, commit: str, say=print) -> None:
    """Clone (full history: ModernUO versions itself with Nerdbank.GitVersioning), check out the pin, apply the
    patches. A checkout already at the pin keeps its patches; one with other changes is refused, never reset."""
    if not (src / ".git").is_dir():
        src.parent.mkdir(parents=True, exist_ok=True)
        say(f"cloning {repo}")
        result = subprocess.run(["git", "clone", "--no-checkout", repo, str(src)], capture_output=True, text=True)
        if result.returncode != 0:
            raise SetupError("clone failed: " + result.stderr.strip()[:300])
    if _git(src, "cat-file", "-e", commit + "^{commit}", check=False).returncode != 0:
        _git(src, "fetch", "origin")
    head = _git(src, "rev-parse", "-q", "--verify", "HEAD", check=False).stdout.strip()
    if head != commit:
        checked_out = bool(_git(src, "ls-files").stdout.strip())     # a --no-checkout clone has an empty index
        if checked_out and _git(src, "status", "--porcelain").stdout.strip():
            raise SetupError("the lab checkout has local changes and is not at the pin; remove its src folder to fetch again")
        _git(src, "checkout", "-q", "--detach", commit)
        say(f"at the pin {commit[:9]}")
    for patch in sorted(PATCHES.glob("*.patch")):
        if _git(src, "apply", "--check", str(patch), check=False).returncode == 0:
            _git(src, "apply", str(patch))
            say(f"applied {patch.name}")
        elif _git(src, "apply", "-R", "--check", str(patch), check=False).returncode == 0:
            say(f"{patch.name} already applied")
        else:
            raise SetupError(f"{patch.name} does not apply to the lab checkout at {commit[:9]} and is not already applied")


def build(src: Path, say=print, log: Path | None = None) -> None:
    """ModernUO's own publish script, release win x64, into src/Distribution."""
    if not (src / "publish.cmd").is_file():
        raise SetupError("not fetched yet")
    say("building (publish.cmd release win x64)")
    out = log.open("w", encoding="utf-8", errors="replace") if log else subprocess.DEVNULL
    try:
        code = subprocess.run(["cmd.exe", "/d", "/c", str(src / "publish.cmd"), "release", "win", "x64"],
                              cwd=str(src), stdout=out, stderr=subprocess.STDOUT, stdin=subprocess.DEVNULL).returncode
    finally:
        if log:
            out.close()
    if code != 0 or not executable(src).is_file():
        raise SetupError(f"build failed (exit {code})" + (f"; see {log.name}" if log else ""))


def _listener_helpers():
    spec = importlib.util.spec_from_file_location("guo_modernuo_configure", ROOT / "tools" / "modernuo" / "configure.py")
    module = importlib.util.module_from_spec(spec)
    spec.loader.exec_module(module)
    return module


def configure(src: Path, client_data: Path, shard_name: str, port: int) -> list[str]:
    """modernuo.json and expansion.json from tools/modernuo/config, listening on 127.0.0.1:port only. The listener is
    put back at every call, so the lab row can never be opened to the LAN by an edited file. Returns what it wrote."""
    if not client_data.is_dir():
        raise SetupError("UO_CLIENT_DATA does not exist")
    helpers = _listener_helpers()
    target = dist(src) / "Configuration"
    target.mkdir(parents=True, exist_ok=True)
    wanted = helpers.listener("127.0.0.1", port)
    wrote = []
    main_cfg = target / "modernuo.json"
    if not main_cfg.exists():
        text = (TEMPLATES / "modernuo.template.json").read_text(encoding="utf-8")
        text = text.replace("@UO_CLIENT_DATA@", json.dumps(str(client_data))[1:-1])
        text = text.replace("@UO_SHARD_NAME@", shard_name).replace("@UO_SHARD_LISTENER@", wanted)
        json.loads(text)
        main_cfg.write_text(text, encoding="utf-8", newline="\n")
        wrote.append(main_cfg.name)
    elif helpers.ensure_listener(main_cfg, wanted):
        wrote.append(main_cfg.name + " (listener)")
    if ensure_ping_port(main_cfg, port):
        wrote.append(main_cfg.name + " (ping port)")
    expansion = target / "expansion.json"
    if not expansion.exists():
        expansion.write_text((TEMPLATES / "expansion.json").read_text(encoding="utf-8"), encoding="utf-8", newline="\n")
        wrote.append(expansion.name)
    return wrote


def ping_port(port: int) -> int:
    """ModernUO's UDP ping server defaults to 12000 for every shard, so the lab row and the dev shard would collide;
    the lab's is its game port plus 10000 (2610 -> 12610)."""
    return port + 10000


def ensure_ping_port(main_cfg: Path, port: int) -> bool:
    data = json.loads(main_cfg.read_text(encoding="utf-8"))
    settings = data.setdefault("settings", {})
    if settings.get("pingServer.port") == str(ping_port(port)):
        return False
    settings["pingServer.port"] = str(ping_port(port))
    main_cfg.write_text(json.dumps(data, indent=2) + "\n", encoding="utf-8", newline="\n")
    return True


def server_env(base: dict[str, str], account: str, password: str) -> dict[str, str]:
    """The environment the lab server boots with: patch 0001 makes or raises the lab owner from these two, and no
    game master accounts (those belong to the dev shard)."""
    env = {k: v for k, v in base.items() if k not in ("UO_SHARD_GM_ACCOUNTS", "UO_SHARD_GM_PASSWORD")}
    env.update({"UO_SHARD_OWNER": account, "UO_SHARD_OWNER_PASSWORD": password})
    return env
