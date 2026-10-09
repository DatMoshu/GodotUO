"""tools/editor_shard: the bridge's admin token reaches the private shard (ADR-0035)."""

from __future__ import annotations

import importlib.util
import sys
import tempfile
import unittest
from pathlib import Path
from types import SimpleNamespace

sys.path.insert(0, str(Path(__file__).resolve().parents[1]))

from guo import shard_secrets  # noqa: E402

# By path, under its own name: other tools' tests import their own run.py as "run".
_spec = importlib.util.spec_from_file_location("editor_shard_run", Path(__file__).with_name("run.py"))
run = importlib.util.module_from_spec(_spec)
_spec.loader.exec_module(run)


class AdminTokenTests(unittest.TestCase):
    def setUp(self):
        self._tmp = tempfile.TemporaryDirectory()
        self.workspace = Path(self._tmp.name) / "workspace"

    def tearDown(self):
        self._tmp.cleanup()

    def test_admin_token_configured_value_wins(self):
        cfg = SimpleNamespace(bridge_admin_token="fromconfig", workspace_dir=self.workspace)
        self.assertEqual(run.admin_token(cfg), "fromconfig")
        self.assertFalse(shard_secrets.path_for(self.workspace).exists(), "a configured token must not write the file")

    def test_admin_token_generated_once_into_the_workspace(self):
        cfg = SimpleNamespace(bridge_admin_token="", workspace_dir=self.workspace)
        first = run.admin_token(cfg)
        self.assertRegex(first, rf"^[A-Za-z0-9]{{{shard_secrets.TOKEN_LENGTH}}}$")
        self.assertEqual(run.admin_token(cfg), first, "the token changed between starts")
        self.assertEqual(shard_secrets.read(shard_secrets.path_for(self.workspace))[shard_secrets.ADMIN_TOKEN_KEY], first)


if __name__ == "__main__":
    unittest.main()
