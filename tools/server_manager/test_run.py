"""Workspace migration and client checks of tools/server_manager/run.py (hermetic: temporary folders only)."""
import json
from pathlib import Path
import sys
import tempfile
import unittest

sys.path.insert(0, str(Path(__file__).resolve().parent))
import run


def old_profile(name: str, ident: str, project: str, data: str) -> dict:
    return {"Id": ident, "Backend": "custom", "Name": name, "Host": "127.0.0.1", "Port": 2610, "Executable": "", "ServerDirectory": "",
            "ServerProject": "", "ClientProject": project, "ClientData": data, "ContentLock": "", "ContentStore": "", "Arguments": []}


class WorkspaceTests(unittest.TestCase):
    def setUp(self):
        self.temp = tempfile.TemporaryDirectory(prefix="guo-ws-test-")
        self.addCleanup(self.temp.cleanup)
        self.root = Path(self.temp.name).resolve()
        self.ws = run.Workspace(self.root / "workspace")

    def test_migration_dedupes_clients_and_keeps_backup(self):
        project, data = str(self.root / "proj"), str(self.root / "data")
        legacy = self.root / "profiles.json"
        legacy.write_text(json.dumps({"Selected": "a" * 32, "Servers": [
            old_profile("One", "a" * 32, project, data), old_profile("Two", "b" * 32, project, data),
            old_profile("Three", "c" * 32, project, "")]}), encoding="utf-8")
        clients = run.load_clients(self.ws)
        servers = run.migrate(self.ws, legacy, clients)
        self.assertEqual(len(clients["clients"]), 2)
        self.assertEqual(servers["Servers"][0]["DefaultClient"], servers["Servers"][1]["DefaultClient"])
        self.assertNotEqual(servers["Servers"][0]["DefaultClient"], servers["Servers"][2]["DefaultClient"])
        self.assertTrue(all("ClientProject" not in s and "ClientData" not in s for s in servers["Servers"]))
        self.assertFalse(legacy.exists())
        self.assertTrue(legacy.with_name("profiles.json.migrated").exists())
        self.assertEqual(servers["Selected"], "a" * 32)
        saved = json.loads(self.ws.clients_file.read_text(encoding="utf-8"))
        self.assertEqual(saved["version"], 1)
        self.assertTrue(self.ws.client_file(saved["clients"][0]["id"]).exists())
        self.assertNotIn("_meta", saved["clients"][0])

    def test_client_problems(self):
        self.assertEqual(run.client_problem({"kind": "external", "program": str(self.root / "no.exe"), "base_data": "", "overlay": ""}), "program missing")
        self.assertEqual(run.client_problem({"kind": "guo-project", "program": "", "base_data": "", "overlay": ""}), "")
        self.assertEqual(run.client_problem({"kind": "guo-project", "program": "", "base_data": str(self.root / "gone"), "overlay": ""}), "UO data folder missing")


class LabPinTests(unittest.TestCase):
    """The server lab rows of backends.json (SV0): docs/data_formats.md section 30."""

    def test_lab_rows_are_pinned_and_complete(self):
        backends = json.loads(Path(run.__file__).with_name("backends.json").read_text(encoding="utf-8"))
        lab = {b["id"]: b["lab"] for b in backends if "lab" in b}
        self.assertEqual(sorted(lab), ["modernuo", "servuo", "sphere", "uox3"])
        self.assertEqual(sorted(r["row"] for r in lab.values()), [1, 2, 3, 4])
        for ident, row in lab.items():
            self.assertTrue({"repos", "pin_source", "licence", "toolchain", "client", "era", "admin", "bind"} <= set(row), ident)
            for repo in row["repos"]:
                self.assertRegex(repo["commit"], r"^[0-9a-f]{40}$", ident)
                self.assertRegex(repo["date"], r"^\d{4}-\d{2}-\d{2}$", ident)
                self.assertTrue(repo["repo"].startswith("https://github.com/") and repo["ref"], ident)
            self.assertIn(row["admin"]["route"], ("patch-env", "console-prompt", "account-file"), ident)
            self.assertIsInstance(row["bind"]["loopback"], bool, ident)
            self.assertTrue(row["era"]["lab"] and row["client"]["accepts"], ident)


if __name__ == "__main__":
    unittest.main()
