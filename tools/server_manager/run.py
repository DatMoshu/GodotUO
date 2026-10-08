"""Create local backend starter profiles, list clients and report setup readiness.

Server and client profiles live in the per-user workspace (ADR-0032, docs/data_formats.md section 30):
UO_WORKSPACE_DIR, else %LOCALAPPDATA%\\GUO or $XDG_DATA_HOME/guo. An earlier build/editor_servers/profiles.json
is migrated once (its ClientProject/ClientData pairs become client profiles) and kept as .migrated.
"""
import argparse
import json
import os
from pathlib import Path
import sys
import uuid

ROOT = Path(__file__).resolve().parents[2]
sys.path.insert(0, str(ROOT / "tools"))
from guo import load_config

KINDS = ("guo-project", "guo-build", "external")


def write_json(path: Path, value) -> None:
    path.parent.mkdir(parents=True, exist_ok=True)
    temporary = path.with_suffix(path.suffix + "." + uuid.uuid4().hex + ".tmp")
    temporary.write_text(json.dumps(value, indent=2), encoding="utf-8")
    os.replace(temporary, path)


def read_json(path: Path, default):
    return json.loads(path.read_text(encoding="utf-8")) if path.exists() else default


class Workspace:
    def __init__(self, root: Path):
        self.root = root
        self.servers_file = root / "profiles" / "servers.json"
        self.clients_file = root / "profiles" / "clients.json"

    def server_home(self, server_id: str) -> Path:
        return self.root / "servers" / server_id

    def client_file(self, client_id: str) -> Path:
        return self.root / "clients" / client_id / "client.json"


def load_clients(ws: Workspace) -> dict:
    data = read_json(ws.clients_file, {"version": 1, "clients": []})
    for client in data["clients"]:
        meta = read_json(ws.client_file(client["id"]), {})
        client["_meta"] = {"kind": client["kind"], "version": "", "encryption": None, "base_fingerprint": "", "source": "manual", **meta}
    return data


def save_clients(ws: Workspace, data: dict) -> None:
    for client in data["clients"]:
        write_json(ws.client_file(client["id"]), client["_meta"])
    write_json(ws.clients_file, {"version": 1, "clients": [{k: v for k, v in c.items() if k != "_meta"} for c in data["clients"]]})


def add_client(data: dict, name: str, program: str, base_data: str, source: str) -> dict:
    for client in data["clients"]:
        if client["kind"] == "guo-project" and client["program"] == program and client["base_data"] == base_data and not client["overlay"]:
            return client
    client = {"id": uuid.uuid4().hex, "name": name[:100], "kind": "guo-project", "program": program, "arguments": [],
              "working_dir": "", "base_data": base_data, "overlay": "", "plugins": [],
              "_meta": {"kind": "guo-project", "version": "", "encryption": None, "base_fingerprint": "", "source": source}}
    data["clients"].append(client)
    return client


def absolute(value: str) -> str:
    return value if value and Path(value).is_absolute() else ""


def migrate(ws: Workspace, legacy: Path, clients: dict) -> dict:
    """Merge the earlier profiles file into the workspace once; returns the server list."""
    servers = read_json(ws.servers_file, {"Selected": None, "SelectedClient": None, "Servers": []})
    if not legacy.exists():
        return servers
    old = json.loads(legacy.read_text(encoding="utf-8"))
    known = {s["Id"] for s in servers["Servers"]}
    for profile in old.get("Servers", []):
        if profile["Id"] in known:
            continue
        project, data = absolute(profile.pop("ClientProject", "")), absolute(profile.pop("ClientData", ""))
        profile["DefaultClient"] = add_client(clients, profile["Name"], project, data, "migrated")["id"] if project or data else ""
        profile.setdefault("ExpectedClientVersion", "")
        servers["Servers"].append(profile)
    if not servers.get("Selected") and old.get("Selected"):
        servers["Selected"] = old["Selected"]
    save_clients(ws, clients)
    write_json(ws.servers_file, servers)
    backup = legacy.with_name(legacy.name + ".migrated")
    os.replace(legacy, backup)
    return servers


def main():
    parser = argparse.ArgumentParser(description=__doc__, formatter_class=argparse.RawDescriptionHelpFormatter)
    parser.add_argument("command", choices=["init", "clients", "doctor"])
    args = parser.parse_args()
    config = load_config()
    ws = Workspace(config.workspace_dir)
    backends = json.loads(Path(__file__).with_name("backends.json").read_text(encoding="utf-8"))
    clients = load_clients(ws)
    profiles = migrate(ws, ROOT / "build/editor_servers/profiles.json", clients)
    profiles.setdefault("SelectedClient", None)
    if args.command == "init":
        data = str(config.client_data.resolve()) if config.client_data.is_dir() and str(config.client_data) != "." else ""
        if not clients["clients"]:
            add_client(clients, "This project", str(ROOT / "godot/GUO"), data, "manual")
        for backend in backends:
            if any(s.get("Backend") == backend["id"] for s in profiles["Servers"]):
                continue
            server_id = uuid.uuid4().hex
            home = ws.server_home(server_id)
            home.mkdir(parents=True, exist_ok=True)
            profiles["Servers"].append({"Id": server_id, "Backend": backend["id"],
                "Name": backend["name"] + " test server", "Host": "127.0.0.1", "Port": backend["port"],
                "Executable": str(home / backend["executable"]), "ServerDirectory": str(home),
                "ServerProject": "", "Arguments": [], "DefaultClient": clients["clients"][0]["id"], "ExpectedClientVersion": "",
                "ContentLock": "", "ContentStore": ""})
        if not profiles.get("Selected") and profiles["Servers"]:
            profiles["Selected"] = profiles["Servers"][0]["Id"]
        save_clients(ws, clients)
        write_json(ws.servers_file, profiles)
        print("Saved", ws.servers_file)
    problems = 0
    if args.command in ("clients", "doctor"):
        for client in clients["clients"]:
            problem = client_problem(client)
            problems += bool(problem)
            print(f"{client['name']} [{client['kind']}]", problem or "files present", "| version", client["_meta"]["version"] or "unknown")
        if not clients["clients"]:
            print("No client profiles. Run init, or add one in the editor's Manage servers window.")
    if args.command == "clients":
        return 0
    missing = False
    ids = {c["id"] for c in clients["clients"]}
    for profile in profiles["Servers"]:
        ready = Path(profile.get("Executable", "")).is_file() and Path(profile.get("ServerDirectory", "")).is_dir()
        missing |= not ready
        print(profile["Name"], str(profile["Port"]), "files present; gameplay unverified" if ready else "setup required")
        default = profile.get("DefaultClient", "")
        if default and default not in ids:
            print("  Default client", default, "is not in clients.json")
            problems += 1
        backend = next((b for b in backends if b["id"] == profile.get("Backend")), None)
        if backend:
            print("  Adapter:", backend["adapter"])
            if not ready:
                print(" ", backend["setup"], backend["source"])
    if not profiles["Servers"]:
        print("Run init to create the six starter profiles.")
    return int(args.command == "doctor" and (missing or problems > 0 or not profiles["Servers"]))


def client_problem(client: dict) -> str:
    """What is missing for a client to start, or an empty string."""
    kind, program = client["kind"], client["program"]
    if kind not in KINDS:
        return "unknown kind"
    if kind == "guo-project":
        if program and not (Path(program) / "project.godot").is_file():
            return "project folder has no project.godot"
    elif not Path(program).is_file():
        return "program missing"
    if client["base_data"] and not Path(client["base_data"]).is_dir():
        return "UO data folder missing"
    if client["overlay"] and not (Path(client["overlay"]) / "guo_data.json").is_file():
        return "overlay has no guo_data.json"
    return ""


if __name__ == "__main__":
    raise SystemExit(main())
