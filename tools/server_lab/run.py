#!/usr/bin/env python3
"""The server compatibility lab: run GUO's scripted cases against other UO servers and keep the grid.

    python tools/server_lab/run.py doctor [BACKEND]
    python tools/server_lab/run.py setup BACKEND          fetch at the pin, patch, build, configure, admin, profile
    python tools/server_lab/run.py start BACKEND | stop BACKEND | status BACKEND
    python tools/server_lab/run.py seed BACKEND           give the lab account its character (once per world)
    python tools/server_lab/run.py cases BACKEND [--case 0,1,9] [--no-record]
    python tools/server_lab/run.py row BACKEND [--no-record]     all of the above, then stop, wiki, card
    python tools/server_lab/run.py wiki                   rewrite docs/wiki/Server-Compatibility.md from the grid
    python tools/server_lab/run.py card BACKEND           build/server_lab/card_<backend>.json; sends nothing

Every lab server listens on 127.0.0.1 only and has its own admin account, generated into the per-user workspace
(`server_lab/<backend>/secrets.json`), never the dev shard's. The server is a profile in the workspace's
servers.json, started and stopped through tools/server_manager (the run bar's own process record), and the cases are
scenarios run by tools/scenario_run with `--server`. The grid is build/server_lab/grid.json (data_formats section 36).

Backends: modernuo (SV1). ServUO, UOX3 and Sphere X join with SV4, SV5 and SV6.
Take the machine leases first (switchboard): build:D for setup, shard:<port> and godot:runtime for start/seed/cases.
"""

from __future__ import annotations

import argparse
import json
import os
import re
import secrets
import shutil
import socket
import string
import subprocess
import sys
import time
from pathlib import Path

HERE = Path(__file__).resolve().parent
ROOT = HERE.parents[1]
sys.path.insert(0, str(ROOT / "tools"))
sys.path.insert(0, str(HERE))

import grid as grid_mod  # noqa: E402
import modernuo  # noqa: E402
from guo import load_config  # noqa: E402


def _server_manager():
    import importlib.util
    spec = importlib.util.spec_from_file_location("guo_server_manager", ROOT / "tools" / "server_manager" / "run.py")
    module = importlib.util.module_from_spec(spec)
    spec.loader.exec_module(module)
    return module


sm = _server_manager()
SUPPORTED = {"modernuo": modernuo}
READY_S = 240
SEED_S = 480
_ALPHABET = string.ascii_letters + string.digits
# The classic login gump's password box takes 16 characters (upstream LoginGump.cs, PasswordStbTextBox max 16):
# a longer password is cut short as it is typed and the login is refused. 62^16 is still about 95 bits.
PASSWORD_LENGTH = 16


def load_json(path: Path):
    return json.loads(path.read_text(encoding="utf-8"))


def backends() -> list[dict]:
    return load_json(ROOT / "tools" / "server_manager" / "backends.json")


def cases() -> list[dict]:
    return load_json(HERE / "cases.json")["cases"]


def table() -> dict:
    return load_json(HERE / "table.json")["backends"]


def build_dir() -> Path:
    return ROOT / "build" / "server_lab"


class Lab:
    """One backend's lab state: its table entry, its workspace folder, its secrets and its server profile."""

    def __init__(self, backend_id: str, cfg):
        known = {b["id"]: b for b in backends() if "lab" in b}
        if backend_id not in known:
            raise SystemExit(f"{backend_id}: not a lab backend ({', '.join(sorted(known))})")
        if backend_id not in SUPPORTED:
            raise SystemExit(f"{backend_id}: its lab row arrives with a later story (SV4 ServUO, SV5 UOX3, SV6 Sphere X)")
        self.id = backend_id
        self.cfg = cfg
        self.backend = known[backend_id]
        self.module = SUPPORTED[backend_id]
        self.table = table()[backend_id]
        self.ws = sm.Workspace(cfg.workspace_dir)
        self.state_dir = Path(cfg.workspace_dir) / "server_lab" / backend_id
        self.state_file = self.state_dir / "lab.json"
        self.port = int(self.backend["port"])

    # --- state, secrets, profile -------------------------------------------------------------------------------
    def state(self) -> dict:
        return load_json(self.state_file) if self.state_file.is_file() else {}

    def save_state(self, **values) -> None:
        state = {**self.state(), **values}
        self.state_dir.mkdir(parents=True, exist_ok=True)
        tmp = self.state_file.with_name("lab.json.tmp")
        tmp.write_text(json.dumps(state, indent=2), encoding="utf-8")
        tmp.replace(self.state_file)

    def credentials(self, create: bool = False) -> tuple[str, str]:
        """The lab admin (account, password) from the workspace secrets file; generated on first setup."""
        path = self.state_dir / "secrets.json"
        if path.is_file():
            data = load_json(path)
            return data["account"], data["password"]
        if not create:
            raise SystemExit(f"{self.id}: no lab account yet; run setup first")
        account = self.table["account"]
        password = "".join(secrets.choice(_ALPHABET) for _ in range(PASSWORD_LENGTH))
        self.state_dir.mkdir(parents=True, exist_ok=True)
        tmp = path.with_name("secrets.json.tmp")
        tmp.write_text(json.dumps({"account": account, "password": password}, indent=2), encoding="utf-8")
        tmp.replace(path)
        print(f"[lab] generated the {self.backend['name']} lab admin into the workspace (server_lab/{self.id}/secrets.json)")
        return account, password

    def profile(self) -> dict | None:
        pid = self.state().get("profile_id")
        servers = sm.load_servers(self.ws)
        return next((s for s in servers["Servers"] if s["Id"] == pid), None) if pid else None

    def home(self) -> Path:
        pid = self.state().get("profile_id")
        if not pid:
            import uuid
            pid = uuid.uuid4().hex
            self.save_state(profile_id=pid)
        return self.ws.server_home(pid)

    def src(self) -> Path:
        return self.home() / "src"

    def register(self) -> dict:
        clients = sm.load_clients(self.ws)["clients"]
        src = self.src()
        profile = {"Id": self.state()["profile_id"], "Backend": self.id, "Name": self.table["lab_name"],
                   "Host": "127.0.0.1", "Port": self.port, "Executable": str(self.module.executable(src)),
                   "ServerDirectory": str(self.module.dist(src)), "ServerProject": "", "Arguments": [],
                   "DefaultClient": clients[0]["id"] if clients else "", "ExpectedClientVersion": self.cfg.client_version,
                   "ContentLock": "", "ContentStore": ""}
        return sm.upsert_server(self.ws, profile)

    def need_profile(self) -> dict:
        profile = self.profile()
        if profile is None or not Path(profile["Executable"]).is_file():
            raise SystemExit(f"{self.id}: not set up; run setup first")
        return profile

    # --- the steps ---------------------------------------------------------------------------------------------
    def setup(self, skip_build: bool = False) -> None:
        core = next(r for r in self.backend["lab"]["repos"] if r["role"] == "core")
        src = self.src()
        say = lambda line: print(f"[lab] {self.id}: {line}")  # noqa: E731
        self.module.fetch(src, core["repo"], core["commit"], say)
        if not skip_build or not self.module.executable(src).is_file():
            log = build_dir() / self.id / "build.log"
            log.parent.mkdir(parents=True, exist_ok=True)
            self.module.build(src, say, log)
        wrote = self.module.configure(src, self.cfg.client_data, self.table["shard_name"], self.port)
        say("configured on 127.0.0.1:%d%s" % (self.port, (" (" + ", ".join(wrote) + ")") if wrote else ""))
        self.credentials(create=True)
        profile = self.register()
        say(f"profile {profile['Name']!r} saved in the workspace's servers.json")

    def listening(self) -> bool:
        with socket.socket() as s:
            s.settimeout(0.5)
            return s.connect_ex(("127.0.0.1", self.port)) == 0

    def start(self) -> None:
        profile = self.need_profile()
        if sm.server_running(self.ws, profile):
            print(f"[lab] {profile['Name']} is already running")
            return
        if self.listening():
            raise SystemExit(f"[lab] port {self.port} is taken by another program; stop it first")
        self.module.configure(self.src(), self.cfg.client_data, self.table["shard_name"], self.port)
        account, password = self.credentials()
        pid = sm.start_server(self.ws, profile, self.module.server_env(dict(os.environ), account, password))
        print(f"[lab] {profile['Name']} starting (PID {pid}); waiting for 127.0.0.1:{self.port}")
        end = time.monotonic() + READY_S
        while time.monotonic() < end:
            if self.listening():
                print(f"[lab] {profile['Name']} is listening")
                return
            if not sm.server_running(self.ws, profile):
                break
            time.sleep(1)
        tail = self.console_tail()
        sm.stop_server(self.ws, profile)
        raise SystemExit(f"[lab] {profile['Name']} did not start listening; last console lines:\n{tail}")

    def console_tail(self, lines: int = 15) -> str:
        profile = self.profile()
        path = self.ws.console_file(profile["Id"]) if profile else None
        if not path or not path.is_file():
            return "(no console output)"
        account, password = self.credentials()
        text = "\n".join(path.read_text(encoding="utf-8", errors="replace").splitlines()[-lines:])
        return text.replace(password, "***")

    def stop(self) -> None:
        profile = self.profile()
        if profile is None:
            print(f"[lab] {self.id}: no lab profile")
            return
        print(f"[lab] {profile['Name']} " + ("stopped" if sm.stop_server(self.ws, profile) else "was not running"))

    def client_env(self) -> dict[str, str]:
        account, password = self.credentials()
        env = dict(os.environ)
        env.update({"UO_SHARD_HOST": "127.0.0.1", "UO_SHARD_PORT": str(self.port), "UO_SHARD_OWNER": account,
                    "UO_SHARD_OWNER_PASSWORD": password, "GUO_SCENARIO_ACCOUNT": account, "GUO_SCENARIO_PASSWORD": password})
        env.pop("UO_SHARD_GM_ACCOUNTS", None)
        return env

    def seed(self) -> None:
        """Log the lab admin in once with the client's own probe login, which makes its character, then save the
        world so the character survives the lab's forced stop."""
        self.need_profile()
        if not self.listening():
            raise SystemExit(f"[lab] {self.id} is not running; start it first")
        account, _ = self.credentials()
        log = build_dir() / self.id / "seed.log"
        log.parent.mkdir(parents=True, exist_ok=True)
        save = self.table["vars"]["staff_save"]
        cmd = [str(ROOT / "launchers" / "game" / "play.bat"), "--play", "--scratch-profile", "--no-focus", "--silent", "--account", account,
               "--character", self.table["character"], "--shard-command", save]
        print(f"[lab] seeding: logging {account} in to make its character, then {save}")
        with log.open("w", encoding="utf-8", errors="replace") as out:
            proc = subprocess.Popen(cmd, cwd=str(ROOT), env=self.client_env(), stdout=out, stderr=subprocess.STDOUT,
                                    stdin=subprocess.DEVNULL)
            try:
                code = proc.wait(timeout=SEED_S)
            except subprocess.TimeoutExpired:
                subprocess.run(["taskkill", "/PID", str(proc.pid), "/T", "/F"], capture_output=True)
                raise SystemExit(f"[lab] seeding did not finish in {SEED_S} s; see {log.relative_to(ROOT)}")
        if code != 0:
            raise SystemExit(f"[lab] seeding exited {code}; see {log.relative_to(ROOT)}")
        self.save_state(seeded=grid_mod.now())
        print("[lab] seeded")

    def run_case(self, case: dict, record: bool) -> dict:
        cmd = [sys.executable, str(ROOT / "tools" / "scenario_run" / "run.py"), case["scenario"]]
        if case["needs_server"]:
            cmd += ["--server", self.need_profile()["Id"]]
        if not record:
            cmd.append("--no-record")
        print(f"[lab] case {case['n']}: {case['title']} ({case['scenario']})")
        proc = subprocess.Popen(cmd, cwd=str(ROOT), env=self.client_env(), stdout=subprocess.PIPE,
                                stderr=subprocess.STDOUT, text=True, encoding="utf-8", errors="replace")
        lines = []
        for line in proc.stdout:
            print("    " + line.rstrip())
            lines.append(line)
        code = proc.wait()
        run_id = None
        for line in reversed(lines):
            match = re.match(r"^(PASS|FAIL) (\S+)", line)
            if match:
                run_id = match.group(2)
                break
        failed = []
        if run_id and (ROOT / "build" / "runs" / run_id / "run.json").is_file():
            manifest = load_json(ROOT / "build" / "runs" / run_id / "run.json")
            failed = [s["id"] for s in manifest.get("steps", []) if s.get("ok") is False]
        note = "" if run_id else (lines[-1].strip() if lines else f"exit {code}")
        return grid_mod.cell_from_run(case, code, run_id, failed, note)

    def run_cases(self, numbers: list[int] | None, record: bool) -> dict:
        path = build_dir() / "grid.json"
        grid = grid_mod.load(path)
        grid_mod.ensure_row(grid, self.backend, self.backend["lab"]["era"]["lab"])
        chosen = [c for c in cases() if c["scenario"] and (numbers is None or c["n"] in numbers)]
        for case in chosen:
            cell = self.run_case(case, record)
            grid = grid_mod.load(path)            # re-read: a long run may overlap a wiki rebuild
            row = grid_mod.ensure_row(grid, self.backend, self.backend["lab"]["era"]["lab"])
            row["cells"][str(case["n"])] = cell
            grid.update({"updated": grid_mod.now(), "client_version": self.cfg.client_version, "commit": git_commit()})
            grid_mod.save(path, grid)
            print(f"[lab] case {case['n']}: {cell['result']}")
        return grid


def git_commit() -> str | None:
    result = subprocess.run(["git", "-C", str(ROOT), "rev-parse", "--short", "HEAD"], capture_output=True, text=True)
    return result.stdout.strip() or None


def current_grid() -> dict:
    grid = grid_mod.load(build_dir() / "grid.json")
    triage = HERE / "triage.json"
    if triage.is_file():
        grid_mod.apply_triage(grid, load_json(triage))
    return grid


def write_wiki() -> Path:
    page = ROOT / "docs" / "wiki" / "Server-Compatibility.md"
    page.write_text(grid_mod.wiki_page(current_grid(), cases(), backends()), encoding="utf-8", newline="\n")
    print(f"[lab] wrote {page.relative_to(ROOT)}")
    return page


def write_card(lab: Lab) -> Path:
    card = grid_mod.card(current_grid(), lab.backend, cases())
    path = build_dir() / f"card_{lab.id}.json"
    path.parent.mkdir(parents=True, exist_ok=True)
    path.write_text(json.dumps(card, indent=2, ensure_ascii=False) + "\n", encoding="utf-8")
    print(json.dumps(card, indent=2, ensure_ascii=False))
    return path


def doctor(cfg, backend_id: str | None) -> int:
    problems = 0
    for tool in ("git", "dotnet"):
        found = shutil.which(tool)
        print(f"{tool:8} {'found' if found else 'MISSING'}")
        problems += not found
    if shutil.which("dotnet"):
        sdks = subprocess.run(["dotnet", "--list-sdks"], capture_output=True, text=True).stdout
        has10 = any(line.startswith("10.") for line in sdks.splitlines())
        print(f"{'.NET 10':8} {'found' if has10 else 'MISSING (ModernUO needs the .NET 10 SDK)'}")
        problems += not has10
    free = shutil.disk_usage(ROOT).free / 2**30
    print(f"{'disk':8} {free:.0f} GB free" + ("" if free >= 10 else " (the heavy-job floor is 10 GB)"))
    problems += free < 10
    print(f"{'UO data':8} {'found' if cfg.client_data.is_dir() else 'MISSING (UO_CLIENT_DATA)'}")
    problems += not cfg.client_data.is_dir()
    for b in sorted((b for b in backends() if "lab" in b), key=lambda b: b["lab"]["row"]):
        if backend_id and b["id"] != backend_id:
            continue
        if b["id"] not in SUPPORTED:
            print(f"{b['name']:10} later story")
            continue
        lab = Lab(b["id"], cfg)
        profile = lab.profile()
        ready = profile is not None and Path(profile["Executable"]).is_file()
        running = ready and sm.server_running(lab.ws, profile)
        print(f"{b['name']:10} " + ("set up" if ready else "not set up") + (", running" if running else "")
              + (", seeded" if lab.state().get("seeded") else ""))
    return 1 if problems else 0


def main(argv: list[str] | None = None) -> int:
    ap = argparse.ArgumentParser(description=__doc__, formatter_class=argparse.RawDescriptionHelpFormatter)
    ap.add_argument("command", choices=("doctor", "setup", "start", "stop", "status", "seed", "cases", "row", "wiki", "card"))
    ap.add_argument("backend", nargs="?")
    ap.add_argument("--case", default=None, help="cases: comma-separated case numbers (default: every case with a scenario)")
    ap.add_argument("--no-record", action="store_true", help="cases, row: stills and events only, no video")
    ap.add_argument("--skip-build", action="store_true", help="setup: keep an existing build")
    ap.add_argument("--reseed", action="store_true", help="row: seed even when the lab account was seeded before")
    args = ap.parse_args(argv)
    cfg = load_config()
    if args.command == "doctor":
        return doctor(cfg, args.backend)
    if args.command == "wiki":
        write_wiki()
        return 0
    if not args.backend:
        ap.error(f"{args.command} needs a backend")
    lab = Lab(args.backend, cfg)
    numbers = [int(n) for n in args.case.split(",")] if args.case else None
    if args.command == "setup":
        lab.setup(args.skip_build)
    elif args.command == "start":
        lab.start()
    elif args.command == "stop":
        lab.stop()
    elif args.command == "status":
        profile = lab.profile()
        running = profile is not None and sm.server_running(lab.ws, profile)
        print(f"[lab] {args.backend} " + ("running" if running else "not running"))
        return 0 if running else 1
    elif args.command == "seed":
        lab.seed()
    elif args.command == "cases":
        lab.run_cases(numbers, not args.no_record)
    elif args.command == "card":
        write_card(lab)
    elif args.command == "row":
        profile = lab.profile()
        if profile is None or not Path(profile["Executable"]).is_file():
            lab.setup()
        started_here = not sm.server_running(lab.ws, lab.need_profile())
        lab.start()
        try:
            if args.reseed or not lab.state().get("seeded"):
                lab.seed()
            lab.run_cases(numbers, not args.no_record)
        finally:
            if started_here:
                lab.stop()
        write_wiki()
        write_card(lab)
    return 0


if __name__ == "__main__":
    sys.exit(main())
