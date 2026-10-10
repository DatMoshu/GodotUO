"""Hermetic checks of the server lab: case table, per-backend table, grid, triage, wiki page, card, ModernUO config,
and the server manager's process record (a real short-lived process; Windows only). No server is fetched or run."""

from __future__ import annotations

import json
import subprocess
import sys
import tempfile
import time
import unittest
from pathlib import Path

HERE = Path(__file__).resolve().parent
ROOT = HERE.parents[1]
sys.path.insert(0, str(HERE))
sys.path.insert(0, str(ROOT / "tools" / "server_manager"))

import grid  # noqa: E402
import modernuo  # noqa: E402
import process  # noqa: E402

BACKENDS = json.loads((ROOT / "tools" / "server_manager" / "backends.json").read_text(encoding="utf-8"))
CASES = json.loads((HERE / "cases.json").read_text(encoding="utf-8"))["cases"]
TABLE = json.loads((HERE / "table.json").read_text(encoding="utf-8"))["backends"]
MODERNUO = next(b for b in BACKENDS if b["id"] == "modernuo")


class TableTests(unittest.TestCase):
    def test_cases_are_the_25_plus_the_client_start(self):
        self.assertEqual([c["n"] for c in CASES], list(range(26)))
        self.assertTrue(all(c["title"] and c["group"] for c in CASES))

    def test_scenarios_exist(self):
        ids = set()
        for f in (ROOT / "tools" / "scenarios").rglob("*.scenario.json"):
            ids.add(json.loads(f.read_text(encoding="utf-8"))["id"])
        scripted = [c["scenario"] for c in CASES if c["scenario"]]
        self.assertEqual(len(scripted), 3)
        self.assertTrue(set(scripted) <= ids, set(scripted) - ids)

    def test_every_table_backend_is_a_lab_row(self):
        lab_rows = {b["id"] for b in BACKENDS if "lab" in b}
        self.assertTrue(set(TABLE) <= lab_rows)
        for entry in TABLE.values():
            self.assertTrue(entry["lab_name"] and entry["account"] and entry["character"])
            self.assertTrue(all(isinstance(v, str) for v in entry["vars"].values()))


class GridTests(unittest.TestCase):
    def setUp(self):
        self.grid = grid.empty()
        self.row = grid.ensure_row(self.grid, MODERNUO, "EJ")
        self.case1 = CASES[1]

    def test_cells_from_runner_exits(self):
        self.assertEqual(grid.cell_from_run(self.case1, 0, "r1", [])["result"], "PASS")
        self.assertEqual(grid.cell_from_run(self.case1, 1, "r1", ["log_in"])["result"], "FAIL (untriaged)")
        self.assertEqual(grid.cell_from_run(self.case1, 3, "r1", [])["result"], "FAIL (untriaged)")
        cell = grid.cell_from_run(self.case1, 2, None, [], "error: not set")
        self.assertEqual((cell["result"], cell["run_dir"], cell["note"]), ("not run", None, "error: not set"))
        self.assertEqual(grid.cell_from_run(self.case1, 0, "r9", [])["run_dir"], "build/runs/r9")

    def test_row_carries_pin_and_era(self):
        self.assertEqual(self.row["pins"][0]["commit"], MODERNUO["lab"]["repos"][0]["commit"][:9])
        self.assertEqual(self.row["era"], "EJ")

    def test_triage(self):
        self.row["cells"]["1"] = grid.cell_from_run(self.case1, 1, "r1", ["x"])
        self.row["cells"]["9"] = grid.cell_from_run(CASES[9], 0, "r2", [])
        grid.apply_triage(self.grid, {"modernuo": {"1": {"verdict": "server-gap", "why": "w"}, "9": {"verdict": "guo"},
                                                   "12": {"verdict": "n/a", "why": "era"}, "13": {"verdict": "bogus"}},
                                      "nobody": {"1": {"verdict": "guo"}}})
        self.assertEqual(self.row["cells"]["1"]["result"], "FAIL (server gap)")
        self.assertEqual(self.row["cells"]["9"]["result"], "PASS")          # a pass is never turned into a failure
        self.assertEqual(self.row["cells"]["12"]["result"], "n/a")
        self.assertNotIn("13", self.row["cells"])

    def test_wiki_page(self):
        self.row["cells"]["1"] = grid.cell_from_run(self.case1, 0, "r1", [])
        self.grid["client_version"] = "7.0.107.76"
        page = grid.wiki_page(self.grid, CASES, BACKENDS)
        self.assertIn("| ModernUO | ServUO | UOX3 | Sphere X |", page)
        self.assertIn("| 1 | " + self.case1["title"] + " | PASS | not run | not run | not run |", page)
        self.assertIn("7.0.107.76", page)
        self.assertNotIn(":\\", page)                          # no machine paths
        self.assertNotIn("r1", page.split("## Results")[1].split("## How")[0].replace("Results", ""))

    def test_card_is_plain(self):
        for n in (0, 1, 9):
            self.row["cells"][str(n)] = grid.cell_from_run(CASES[n], 0, f"run{n}", [])
        c = grid.card(self.grid, MODERNUO, CASES)
        self.assertTrue(c["ok"])
        self.assertEqual((c["passed"], c["scripted"]), (3, 3))
        self.assertNotIn("run0", c["body"])
        self.assertLessEqual(len(c["body"]), grid.CARD_BODY_LIMIT)
        self.row["cells"]["9"] = grid.cell_from_run(CASES[9], 1, "x", ["s"])
        self.assertFalse(grid.card(self.grid, MODERNUO, CASES)["ok"])

    def test_save_load_round_trip(self):
        with tempfile.TemporaryDirectory() as tmp:
            path = Path(tmp) / "g" / "grid.json"
            grid.save(path, self.grid)
            self.assertEqual(grid.load(path), self.grid)
            self.assertEqual(grid.load(Path(tmp) / "missing.json"), grid.empty())


class ModernUOConfigTests(unittest.TestCase):
    def test_configure_is_loopback_with_its_own_ping_port(self):
        with tempfile.TemporaryDirectory() as tmp:
            src, data = Path(tmp) / "src", Path(tmp) / "data"
            data.mkdir()
            wrote = modernuo.configure(src, data, "Lab", 2610)
            self.assertIn("modernuo.json", wrote)
            cfg_file = modernuo.dist(src) / "Configuration" / "modernuo.json"
            cfg = json.loads(cfg_file.read_text(encoding="utf-8"))
            self.assertEqual(cfg["listeners"], ["127.0.0.1:2610"])
            self.assertEqual(cfg["settings"]["pingServer.port"], "12610")
            cfg["listeners"] = ["0.0.0.0:2610"]                  # an edited file is closed again at the next call
            cfg_file.write_text(json.dumps(cfg), encoding="utf-8")
            modernuo.configure(src, data, "Lab", 2610)
            self.assertEqual(json.loads(cfg_file.read_text(encoding="utf-8"))["listeners"], ["127.0.0.1:2610"])
            self.assertEqual(modernuo.configure(src, data, "Lab", 2610), [])

    def test_server_env_has_the_lab_owner_and_no_dev_gms(self):
        env = modernuo.server_env({"UO_SHARD_GM_ACCOUNTS": "a,b", "UO_SHARD_GM_PASSWORD": "p", "UO_SHARD_OWNER": "dev", "X": "1"},
                                  "guolab", "pw")
        self.assertEqual((env["UO_SHARD_OWNER"], env["UO_SHARD_OWNER_PASSWORD"], env["X"]), ("guolab", "pw", "1"))
        self.assertNotIn("UO_SHARD_GM_ACCOUNTS", env)
        self.assertNotIn("UO_SHARD_GM_PASSWORD", env)


@unittest.skipUnless(sys.platform == "win32", "the process record is Windows only")
class ProcessRecordTests(unittest.TestCase):
    def setUp(self):
        self.tmp = tempfile.TemporaryDirectory(prefix="guo-lab-test-")
        self.addCleanup(self.tmp.cleanup)
        self.home = Path(self.tmp.name)
        self.state = self.home / "process.json"
        self.console = self.home / "server.console.log"
        self.exe = Path(process.os.environ.get("SystemRoot", r"C:\Windows")) / "System32" / "PING.EXE"

    def test_start_owned_stop(self):
        process.start(self.exe, self.home, ["-n", "30", "127.0.0.1"], self.console, self.state, dict(process.os.environ))
        self.addCleanup(lambda: process.stop(self.state))
        saved = json.loads(self.state.read_text(encoding="utf-8"))
        self.assertEqual(set(saved), {"Pid", "Started", "Executable"})
        self.assertTrue(saved["Executable"].lower().endswith("cmd.exe"))
        self.assertEqual(process.owned(self.state), saved["Pid"])
        with self.assertRaises(process.ProcessError):
            process.start(self.exe, self.home, [], self.console, self.state, dict(process.os.environ))
        time.sleep(1.5)
        self.assertIn("127.0.0.1", self.console.read_text(errors="replace"))
        self.assertTrue(process.stop(self.state))
        self.assertFalse(self.state.exists())
        self.assertIsNone(process.identity(saved["Pid"]))

    def test_a_different_start_time_is_not_ours(self):
        sleeper = subprocess.Popen([str(self.exe), "-n", "30", "127.0.0.1"], stdout=subprocess.DEVNULL)
        self.addCleanup(sleeper.kill)
        started, exe = process.identity(sleeper.pid)
        self.state.write_text(json.dumps({"Pid": sleeper.pid, "Started": started + 1, "Executable": exe}), encoding="utf-8")
        self.assertIsNone(process.owned(self.state))
        self.assertFalse(process.stop(self.state))                      # not ours: left running, record removed
        self.assertIsNone(sleeper.poll())
        self.state.write_text(json.dumps({"Pid": sleeper.pid, "Started": started, "Executable": exe}), encoding="utf-8")
        self.assertEqual(process.owned(self.state), sleeper.pid)

    def test_strips_probe_variables(self):
        script = self.home / "env.cmd"
        script.write_text("@set UO_\n", encoding="ascii")
        env = dict(process.os.environ, UO_SHARD_PROBE="1", UO_SERVER_CONTENT="x", UO_KEEP="y")
        process.start(Path(process.os.environ["ComSpec"]), self.home, ["/d", "/c", str(script)], self.console, self.state, env)
        self.addCleanup(lambda: process.stop(self.state))
        for _ in range(50):
            if self.console.is_file() and "UO_KEEP" in self.console.read_text(errors="replace"):
                break
            time.sleep(0.1)
        text = self.console.read_text(errors="replace")
        process.stop(self.state)
        self.assertIn("UO_KEEP=y", text)
        self.assertNotIn("UO_SHARD_PROBE", text)
        self.assertNotIn("UO_SERVER_CONTENT", text)


if __name__ == "__main__":
    unittest.main()
