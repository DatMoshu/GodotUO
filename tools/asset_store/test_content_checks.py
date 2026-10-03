"""Runs the headless content checks (StoreSmoke content-check) on the freshly built starter packs.

Covers the server export round trip for items, loot, creatures and spawners, and the refusals of
invalid definitions. Build first: dotnet build tools/asset_store/headless/StoreSmoke.csproj
"""
import json
from pathlib import Path
import subprocess
import sys
import tempfile
import unittest
import zipfile

ROOT = Path(__file__).resolve().parents[2]
sys.path.insert(0, str(ROOT / "tools"))
from asset_store.content_examples import build

DLL = ROOT / "tools/asset_store/headless/bin/Debug/net8.0/StoreSmoke.dll"


class ContentChecks(unittest.TestCase):
    def test_spawner_round_trip(self):
        with tempfile.TemporaryDirectory() as tmp:
            tmp = Path(tmp)
            archives = build(tmp / "examples")
            archive = next(a for a in archives if "sample-content-spawner" in a.name)
            spawner = json.loads(zipfile.ZipFile(archive).read("spawner.json"))
            self.assertEqual(spawner["count"], 2)
            run = subprocess.run(["dotnet", str(DLL), "content-check", str(tmp / "examples"), str(tmp / "store")],
                                 capture_output=True, text=True)
            self.assertEqual(run.returncode, 0, run.stdout + run.stderr)
            self.assertIn("spawner export, creature reference and six invalid spawner cases PASS", run.stdout)


if __name__ == "__main__":
    unittest.main()
