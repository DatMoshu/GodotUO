"""Create local backend starter profiles and report setup readiness."""
import argparse
import json
import os
from pathlib import Path
import sys
import uuid

ROOT = Path(__file__).resolve().parents[2]
sys.path.insert(0, str(ROOT / "tools"))
from guo import load_config


def main():
    parser = argparse.ArgumentParser(description=__doc__)
    parser.add_argument("command", choices=["init", "doctor"])
    args = parser.parse_args()
    path = ROOT / "build/editor_servers/profiles.json"
    backends = json.loads(Path(__file__).with_name("backends.json").read_text(encoding="utf-8"))
    profiles = json.loads(path.read_text(encoding="utf-8")) if path.exists() else {"Selected": None, "Servers": []}
    if args.command == "init":
        config = load_config()
        for backend in backends:
            if any(s.get("Backend") == backend["id"] for s in profiles["Servers"]):
                continue
            home = ROOT / "build/servers" / backend["id"]
            home.mkdir(parents=True, exist_ok=True)
            profiles["Servers"].append({"Id": uuid.uuid4().hex, "Backend": backend["id"],
                "Name": backend["name"] + " test server", "Host": "127.0.0.1", "Port": backend["port"],
                "Executable": str(home / backend["executable"]), "ServerDirectory": str(home),
                "ServerProject": "", "Arguments": [], "ClientProject": str(ROOT / "godot/GUO"),
                "ClientData": str(config.client_data.resolve()) if config.client_data.is_dir() and str(config.client_data) != "." else "", "ContentLock": "", "ContentStore": ""})
        if not profiles["Selected"] and profiles["Servers"]:
            profiles["Selected"] = profiles["Servers"][0]["Id"]
        path.parent.mkdir(parents=True, exist_ok=True)
        temporary = path.with_suffix("." + uuid.uuid4().hex + ".tmp")
        temporary.write_text(json.dumps(profiles, indent=2), encoding="utf-8")
        os.replace(temporary, path)
        print("Saved", path)
    missing = False
    for profile in profiles["Servers"]:
        ready = Path(profile.get("Executable", "")).is_file() and Path(profile.get("ServerDirectory", "")).is_dir()
        missing |= not ready
        print(profile["Name"], str(profile["Port"]), "files present; gameplay unverified" if ready else "setup required")
        backend = next((b for b in backends if b["id"] == profile.get("Backend")), None)
        if backend:
            print("  Adapter:", backend["adapter"])
            if not ready:
                print(" ", backend["setup"], backend["source"])
    if not profiles["Servers"]:
        print("Run init to create the six starter profiles.")
    return int(args.command == "doctor" and (missing or not profiles["Servers"]))


if __name__ == "__main__":
    raise SystemExit(main())
