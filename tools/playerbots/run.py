"""Optional, isolated ModernUO + UO Offline PlayerBots development shard."""
from __future__ import annotations

import argparse
import json
import os
from pathlib import Path
import shutil
import socket
import subprocess
import sys
import time

HERE = Path(__file__).resolve().parent
ROOT = HERE.parents[1]
sys.path.insert(0, str(ROOT / "tools"))
from guo.config import load_config


def run(*args, cwd=None, env=None, capture=False):
    return subprocess.run([str(a) for a in args], cwd=cwd, env=env, check=True,
                          text=True, stdout=subprocess.PIPE if capture else None).stdout


def checkout(path: Path, repository: str, commit: str):
    if not path.exists():
        path.parent.mkdir(parents=True, exist_ok=True)
        # Full history is required by ModernUO's version generator.
        run("git", "clone", repository, path)
    if not (path / ".git").is_dir():
        raise RuntimeError(f"Not a managed git checkout: {path}")
    origin = run("git", "-C", path, "remote", "get-url", "origin", capture=True).strip()
    if origin != repository:
        raise RuntimeError(f"Unexpected origin in {path}: {origin}")
    head = run("git", "-C", path, "rev-parse", "HEAD", capture=True).strip()
    if head == commit:
        return
    dirty = run("git", "-C", path, "status", "--porcelain", capture=True).strip()
    if dirty:
        raise RuntimeError(f"Refusing to change the pin of a modified checkout: {path}. "
                           "Use a fresh UO_PLAYERBOTS_DIR for upgrades.")
    if subprocess.run(["git", "-C", str(path), "cat-file", "-e", commit],
                      stdout=subprocess.DEVNULL, stderr=subprocess.DEVNULL).returncode:
        run("git", "-C", path, "fetch", "origin", commit)
    run("git", "-C", path, "checkout", "--detach", commit)


def pending_patches(server: Path, patches: list[Path]) -> list[Path]:
    pending = []
    for patch in patches:
        reverse = subprocess.run(["git", "-C", str(server), "apply", "--reverse",
                                  "--check", str(patch)], capture_output=True)
        if reverse.returncode == 0:
            continue
        run("git", "-C", server, "apply", "--check", patch)
        pending.append(patch)
    if pending:
        # Check the entire set before applying anything; incompatibility is fatal.
        run("git", "-C", server, "apply", "--check", *pending)
    return pending


def copy_plan(source: Path, target: Path) -> list[tuple[Path, Path]]:
    if not source.is_dir():
        raise RuntimeError(f"Missing upstream directory: {source}")
    result = []
    for src in sorted(source.rglob("*")):
        if src.is_symlink():
            raise RuntimeError(f"Unexpected symlink: {src}")
        if not src.is_file():
            continue
        dst = target / src.relative_to(source)
        if dst.exists() and dst.read_bytes() != src.read_bytes():
            raise RuntimeError(f"Refusing to overwrite modified file: {dst}")
        result.append((src, dst))
    return result


def configure(cfg, dist: Path):
    if not cfg.client_data.is_dir():
        raise RuntimeError("Set UO_CLIENT_DATA to your existing UO installation first.")
    folder = dist / "Configuration"
    folder.mkdir(parents=True, exist_ok=True)
    destination = folder / "modernuo.json"
    if not destination.exists():
        template = (ROOT / "tools/modernuo/config/modernuo.template.json").read_text()
        template = template.replace("@UO_CLIENT_DATA@", json.dumps(str(cfg.client_data))[1:-1])
        template = template.replace("@UO_SHARD_NAME@", "GUO PlayerBots")
        template = template.replace("@UO_SHARD_PORT@", str(cfg.playerbots_port))
        data = json.loads(template)
        data["listeners"] = [f"127.0.0.1:{cfg.playerbots_port}"]
        data["settings"]["crashGuard.restartServer"] = "False"
        data["settings"]["pingServer.enabled"] = "False"
        data["settings"]["guo.playerbots.population"] = "50"
        destination.write_text(json.dumps(data, indent=2) + "\n", encoding="utf-8")
    expansion = folder / "expansion.json"
    if not expansion.exists():
        shutil.copy2(ROOT / "tools/modernuo/config/expansion.json", expansion)


def smoke(dist: Path, folder: Path, env: dict[str, str], port: int):
    # Do not mistake an already-running shard for the process under test.
    with socket.socket() as probe:
        if probe.connect_ex(("127.0.0.1", port)) == 0:
            raise RuntimeError(f"Port {port} is occupied; stop that shard before smoke.")
    log = folder / "smoke.log"
    with log.open("w", encoding="utf-8") as output:
        process = subprocess.Popen(["dotnet", str(dist / "ModernUO.dll")], cwd=dist,
                                   env=env, stdin=subprocess.DEVNULL,
                                   stdout=output, stderr=subprocess.STDOUT)
        try:
            deadline = time.monotonic() + 90
            while time.monotonic() < deadline:
                if process.poll() is not None:
                    raise RuntimeError(f"Server exited during smoke; see {log}")
                text = log.read_text(encoding="utf-8", errors="replace")
                if "[BotStartupManager] Built" in text and "Listening: 127.0.0.1:" + str(port) in text:
                    with socket.create_connection(("127.0.0.1", port), timeout=2):
                        pass
                    print(f"[playerbots] PASS: bot initialization and TCP listener. Evidence: {log}")
                    return
                time.sleep(0.25)
            raise RuntimeError(f"Timed out waiting for bot initialization/listener; see {log}")
        finally:
            # Only the temporary process we just created; no world reset or save deletion.
            if process.poll() is None:
                process.terminate()
                try:
                    process.wait(timeout=10)
                except subprocess.TimeoutExpired:
                    process.kill()
                    process.wait()


def main():
    parser = argparse.ArgumentParser(description=__doc__)
    parser.add_argument("command", choices=("setup", "build", "run", "play", "populate", "smoke", "status"))
    args = parser.parse_args()
    cfg = load_config(ROOT)
    folder = (cfg.playerbots_dir or cfg.build / "playerbots").resolve()
    server = folder / "server"
    upstream = folder / "upstream"
    dist = server / "Distribution"
    pin = json.loads((HERE / "upstream.json").read_text())
    if not 1 <= cfg.playerbots_port <= 65535:
        raise RuntimeError("UO_PLAYERBOTS_PORT must be between 1 and 65535")
    if cfg.playerbots_port in (2593, cfg.shard_port):
        raise RuntimeError("UO_PLAYERBOTS_PORT must not be the dev shard port; bots run on their own shard")
    if server.resolve() == cfg.shard_src.resolve():
        raise RuntimeError("PlayerBots must use a separate checkout from UO_SHARD_SRC")
    if args.command == "status":
        print(f"Profile: {folder}\nEndpoint: 127.0.0.1:{cfg.playerbots_port}")
        print(f"UO Offline pin: {pin['commit']}\nModernUO pin: {pin['server_commit']}")
        print(f"Setup complete: {(folder / 'installed.json').is_file()}")
        print(f"Build recorded: {(folder / 'built.json').is_file()}")
        return
    if args.command == "setup":
        checkout(upstream, pin["repository"], pin["commit"])
        if run("git", "-C", upstream, "status", "--porcelain", capture=True).strip():
            raise RuntimeError(f"Upstream checkout was modified: {upstream}")
        checkout(server, pin["server_repository"], pin["server_commit"])
        # Preserve the existing GUO dev-shard baseline and add only bot support.
        patches = sorted((ROOT / "tools/modernuo/patches").glob("*.patch"))
        patches += [upstream / "patches" / name for name in pin["patches"]]
        pending = pending_patches(server, patches)
        copies = copy_plan(upstream / "playerbots/source/CustomBots",
                           server / "Projects/UOContent/CustomBots")
        copies += copy_plan(HERE / "overlay", server / "Projects/UOContent/CustomBots/Guo")
        for name in pin["data"]:
            copies += copy_plan(upstream / "playerbots/data" / name, dist / "Data" / name)
        if pending:
            run("git", "-C", server, "apply", *pending)
        for src, dst in copies:
            dst.parent.mkdir(parents=True, exist_ok=True)
            if not dst.exists():
                shutil.copy2(src, dst)
        (dist / "Data/Navigation").mkdir(parents=True, exist_ok=True)
        scenario = dist / "Data/PlayerBotSpawners.json"
        if not scenario.exists():
            # Upstream's world-wide fallback adds fixed crowds outside its target.
            # Use five explicit spawners so a fresh dev world stays small.
            shutil.copy2(HERE / "scenarios/PlayerBotSpawners.json", scenario)
        notices = dist / "Licenses/UO-Offline"
        notices.mkdir(parents=True, exist_ok=True)
        shutil.copy2(upstream / "LICENSE", notices / "LICENSE")
        shutil.copy2(HERE / "upstream.json", notices / "upstream.json")
        configure(cfg, dist)
        (folder / "installed.json").write_text(json.dumps(pin, indent=2) + "\n")
        print("[playerbots] Setup complete. Next: playerbots.bat build")
        return
    marker = folder / "installed.json"
    if not marker.is_file() or json.loads(marker.read_text()) != pin:
        raise RuntimeError("Run playerbots.bat setup for the current pins first.")
    env = os.environ.copy()
    env.update(UO_SHARD_SRC=str(server), UO_SHARD_DIST=str(dist),
               UO_SHARD_HOST="127.0.0.1", UO_SHARD_PORT=str(cfg.playerbots_port),
               UO_SHARD_NAME="GUO PlayerBots", UO_SHARD_OWNER=cfg.shard_owner,
               UO_SHARD_OWNER_PASSWORD=cfg.shard_owner_password,
               UO_SHARD_GM_ACCOUNTS=",".join(cfg.shard_gm_accounts),
               UO_SHARD_GM_PASSWORD=cfg.shard_gm_password)
    if args.command == "build":
        (folder / "built.json").unlink(missing_ok=True)
        # Compile the pinned build tool, avoiding its floating binary download.
        run("dotnet", "run", "--project", server / "Projects/BuildTool", "--",
            "release", "win", "x64", cwd=server, env=env)
        (folder / "built.json").write_text(json.dumps(pin, indent=2) + "\n")
    else:
        built = folder / "built.json"
        if not built.is_file() or json.loads(built.read_text()) != pin:
            raise RuntimeError("Run playerbots.bat build successfully before starting this profile.")
        if args.command in ("run", "smoke"):
            configure(cfg, dist)
            if args.command == "smoke":
                smoke(dist, folder, env, cfg.playerbots_port)
            else:
                run("dotnet", dist / "ModernUO.dll", cwd=dist, env=env)
        else:
            launcher = "game/play.bat" if args.command == "play" else "shard/populate.bat"
            run("cmd.exe", "/d", "/c", ROOT / "launchers" / launcher, cwd=ROOT, env=env)


if __name__ == "__main__":
    try:
        main()
    except (RuntimeError, OSError, ValueError, subprocess.CalledProcessError) as error:
        print(f"[playerbots] {error}", file=sys.stderr)
        sys.exit(1)
