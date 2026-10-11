"""tools/editor_shard: the Admin tab's Audit list (AD8) on a fixture audit log.

The reading, filter and CSV live in godot/GUO/src/Workspace/AdminAuditView.cs (plain .NET, no Godot), which the
bridge's test project compiles and checks on bridge/tests/fixtures/admin_audit_fixture.jsonl. This runs that project
and checks the fixture itself holds its secrets masked, as the server writes them.
"""

from __future__ import annotations

import json
import shutil
import subprocess
import unittest
from pathlib import Path

HERE = Path(__file__).resolve().parent
TESTS = HERE / "bridge" / "tests"
FIXTURE = TESTS / "fixtures" / "admin_audit_fixture.jsonl"
SECRET_WORDS = ("password", "token", "secret", "passphrase", "webhook")


def _entries():
    out = []
    for line in FIXTURE.read_text(encoding="utf-8").splitlines():
        try:
            out.append(json.loads(line))
        except json.JSONDecodeError:
            continue  # the fixture's cut line, which the list skips
    return out


def _secret_values(node, key=""):
    """Every value under a secret-named field, at any depth."""
    if isinstance(node, dict):
        secret_key = any(w in str(node.get("key", "")).lower() for w in SECRET_WORDS)
        for k, v in node.items():
            if any(w in k.lower() for w in SECRET_WORDS) or (secret_key and k in ("from", "to", "value")):
                yield v
            else:
                yield from _secret_values(v, k)
    elif isinstance(node, list):
        for v in node:
            yield from _secret_values(v, key)


class AuditViewTests(unittest.TestCase):
    def test_audit_fixture_secrets_are_masked(self):
        entries = _entries()
        self.assertEqual(len(entries), 7)
        self.assertIn({"action": "password", "account": "guoprobe", "password": "***", "req": 3}, [e["args"] for e in entries])
        secrets = [v for e in entries for v in _secret_values(e["args"])]
        self.assertTrue(secrets)
        self.assertTrue(all(v == "***" for v in secrets), secrets)

    @unittest.skipUnless(shutil.which("dotnet"), "dotnet is not on PATH")
    def test_audit_view_on_fixture_passes(self):
        proc = subprocess.run(
            ["dotnet", "run", "--project", str(TESTS / "AdminChannel.Tests.csproj")],
            capture_output=True, text=True, encoding="utf-8", errors="replace", timeout=600,
        )
        out = proc.stdout + proc.stderr
        self.assertEqual(proc.returncode, 0, out[-4000:])
        self.assertIn("PASS: the Audit list reads the log", out)
        self.assertIn("ALL PASS", out)


if __name__ == "__main__":
    unittest.main()
