"""The dev shard is closed by default and has no default password (SF1).

Hermetic: a fake built shard and workspace in a temporary folder, a stand-in
configuration object, no client data, no network, nothing from config.local.bat.

    python tools/modernuo/test_configure.py
"""

from __future__ import annotations

import contextlib
import io
import json
import re
import subprocess
import sys
import tempfile
import unittest
from pathlib import Path
from types import SimpleNamespace

HERE = Path(__file__).resolve().parent
ROOT = HERE.parents[1]
sys.path.insert(0, str(HERE))
sys.path.insert(0, str(ROOT / "tools"))

import configure  # noqa: E402
from guo import shard_secrets  # noqa: E402


class ConfigureTests(unittest.TestCase):
    def setUp(self):
        temp = tempfile.TemporaryDirectory(prefix="guo-sf1-")
        self.addCleanup(temp.cleanup)
        self.tmp = Path(temp.name)

    def cfg(self, name="a", bind="127.0.0.1", port=2593, owner_pw="", gm_pw=""):
        dist = self.tmp / name / "Distribution"
        data = self.tmp / name / "uo"
        dist.mkdir(parents=True, exist_ok=True)
        data.mkdir(parents=True, exist_ok=True)
        return SimpleNamespace(shard_dist=dist, client_data=data, shard_name="GUO Dev", shard_port=port,
                               shard_bind=bind, workspace_dir=self.tmp / name / "workspace",
                               shard_owner_password=owner_pw, shard_gm_password=gm_pw, bridge_admin_token="")

    def run_configure(self, cfg) -> str:
        out = io.StringIO()
        with contextlib.redirect_stdout(out):
            self.assertEqual(configure.configure(cfg), 0)
        return out.getvalue()

    def listeners(self, cfg):
        return json.loads((cfg.shard_dist / "Configuration" / "modernuo.json").read_text(encoding="utf-8"))["listeners"]

    def test_fresh_configure_binds_loopback(self):
        cfg = self.cfg()
        self.run_configure(cfg)
        self.assertEqual(self.listeners(cfg), ["127.0.0.1:2593"])

    def test_fresh_configure_generates_passwords_in_the_workspace(self):
        cfg = self.cfg()
        out = self.run_configure(cfg)
        path = shard_secrets.path_for(cfg.workspace_dir)
        values = shard_secrets.read(path)
        self.assertEqual(set(values), {shard_secrets.OWNER_KEY, shard_secrets.GM_KEY, shard_secrets.ADMIN_TOKEN_KEY})
        for key, value in values.items():
            length = shard_secrets.TOKEN_LENGTH if key == shard_secrets.ADMIN_TOKEN_KEY else shard_secrets.LENGTH
            self.assertRegex(value, rf"^[A-Za-z0-9]{{{length}}}$")
            self.assertNotIn(value, out, "a password or the admin token was printed")
        self.assertEqual(len(set(values.values())), 3)
        # A guarded .bat, as common.bat calls it: the environment still wins.
        for line in path.read_text(encoding="utf-8").splitlines():
            if " set " in line:
                self.assertTrue(line.startswith(("if not defined UO_SHARD_", "if not defined UO_BRIDGE_ADMIN_TOKEN ")), line)

    def test_an_earlier_secrets_file_gains_the_admin_token_and_keeps_its_passwords(self):
        # A file written before ADR-0035 holds only the two passwords.
        cfg = self.cfg()
        path = shard_secrets.path_for(cfg.workspace_dir)
        path.parent.mkdir(parents=True)
        path.write_text('if not defined UO_SHARD_OWNER_PASSWORD set "UO_SHARD_OWNER_PASSWORD=Owner1"\n'
                        'if not defined UO_SHARD_GM_PASSWORD set "UO_SHARD_GM_PASSWORD=Gm1"\n', encoding="utf-8")
        out = self.run_configure(cfg)
        values = shard_secrets.read(path)
        self.assertEqual((values[shard_secrets.OWNER_KEY], values[shard_secrets.GM_KEY]), ("Owner1", "Gm1"))
        self.assertRegex(values[shard_secrets.ADMIN_TOKEN_KEY], rf"^[A-Za-z0-9]{{{shard_secrets.TOKEN_LENGTH}}}$")
        self.assertIn(shard_secrets.ADMIN_TOKEN_KEY, out)
        self.assertNotIn(values[shard_secrets.ADMIN_TOKEN_KEY], out)

    def test_a_staff_password_too_long_for_the_login_box_is_replaced(self):
        # AD5: passwords made before it had 20 characters, but the login gump's box holds 16, so a typed login failed.
        cfg = self.cfg()
        path = shard_secrets.path_for(cfg.workspace_dir)
        path.parent.mkdir(parents=True)
        old_owner, old_gm, token = "A" * 20, "Gm1short", "T" * shard_secrets.TOKEN_LENGTH
        path.write_text(f'if not defined UO_SHARD_OWNER_PASSWORD set "UO_SHARD_OWNER_PASSWORD={old_owner}"\n'
                        f'if not defined UO_SHARD_GM_PASSWORD set "UO_SHARD_GM_PASSWORD={old_gm}"\n'
                        f'if not defined UO_BRIDGE_ADMIN_TOKEN set "UO_BRIDGE_ADMIN_TOKEN={token}"\n', encoding="utf-8")
        out = self.run_configure(cfg)
        values = shard_secrets.read(path)
        self.assertEqual(shard_secrets.LENGTH, 16)
        self.assertRegex(values[shard_secrets.OWNER_KEY], r"^[A-Za-z0-9]{16}$")
        self.assertNotIn(values[shard_secrets.OWNER_KEY], out)
        # A password that fits is kept, and so is the token: nobody types it.
        self.assertEqual((values[shard_secrets.GM_KEY], values[shard_secrets.ADMIN_TOKEN_KEY]), (old_gm, token))
        self.assertIn(shard_secrets.OWNER_KEY, out)

    def test_a_settings_secret_in_the_file_survives_a_rewrite(self):
        # The Admin tab's Settings form (AD4) keeps a mail password here; adding a missing key must not drop it.
        cfg = self.cfg()
        path = shard_secrets.path_for(cfg.workspace_dir)
        path.parent.mkdir(parents=True)
        path.write_text('if not defined UO_SHARD_OWNER_PASSWORD set "UO_SHARD_OWNER_PASSWORD=Owner1"\n'
                        'if not defined UO_SHARD_EMAIL_PASSWORD set "UO_SHARD_EMAIL_PASSWORD=Mail9"\n', encoding="utf-8")
        self.run_configure(cfg)
        text = path.read_text(encoding="utf-8")
        self.assertIn('if not defined UO_SHARD_EMAIL_PASSWORD set "UO_SHARD_EMAIL_PASSWORD=Mail9"', text)
        self.assertEqual(text.count("UO_SHARD_EMAIL_PASSWORD="), 1)
        self.assertEqual(shard_secrets.read(path)[shard_secrets.OWNER_KEY], "Owner1")

    def test_passwords_differ_per_fresh_run_and_stay_put_after(self):
        a, b = self.cfg("a"), self.cfg("b")
        self.run_configure(a)
        self.run_configure(b)
        first = shard_secrets.read(shard_secrets.path_for(a.workspace_dir))
        other = shard_secrets.read(shard_secrets.path_for(b.workspace_dir))
        for key in shard_secrets.KEYS:
            self.assertNotEqual(first[key], other[key])
        self.run_configure(a)
        self.assertEqual(shard_secrets.read(shard_secrets.path_for(a.workspace_dir)), first)

    def test_an_open_listener_in_an_existing_file_is_closed(self):
        cfg = self.cfg()
        conf = cfg.shard_dist / "Configuration" / "modernuo.json"
        conf.parent.mkdir(parents=True)
        conf.write_text(json.dumps({"listeners": ["0.0.0.0:2593"], "settings": {"keep": "me"}}), encoding="utf-8")
        out = self.run_configure(cfg)
        self.assertEqual(self.listeners(cfg), ["127.0.0.1:2593"])
        self.assertEqual(json.loads(conf.read_text(encoding="utf-8"))["settings"], {"keep": "me"})
        self.assertIn("UO_SHARD_BIND", out)

    def test_opening_the_shard_is_a_setting(self):
        cfg = self.cfg(bind="0.0.0.0", port=2600)
        out = self.run_configure(cfg)
        self.assertEqual(self.listeners(cfg), ["0.0.0.0:2600"])
        self.assertIn("reachable from other machines", out)
        self.assertEqual(configure.listener("::1", 2593), "[::1]:2593")
        with self.assertRaises(SystemExit):
            configure.listener("localhost", 2593)

    def test_an_explicit_password_is_not_reported_as_generated(self):
        cfg = self.cfg(owner_pw="fromlocal", gm_pw="fromlocal2")
        out = self.run_configure(cfg)
        # Only the admin token (ADR-0035), which nothing configured, is generated.
        self.assertIn(f"Generated {shard_secrets.ADMIN_TOKEN_KEY} into", out)
        self.assertNotIn(shard_secrets.OWNER_KEY, out)
        self.assertNotIn(shard_secrets.GM_KEY, out)
        self.assertNotIn("fromlocal", out)


class NoDefaultsLeftTests(unittest.TestCase):
    def test_template_listens_on_the_setting_only(self):
        text = (HERE / "config" / "modernuo.template.json").read_text(encoding="utf-8")
        self.assertNotIn("0.0.0.0", text)
        self.assertIn('"@UO_SHARD_LISTENER@"', text)

    def test_config_bat_ships_no_password_and_binds_loopback(self):
        text = (ROOT / "launchers" / "_shared" / "config.bat").read_text(encoding="utf-8")
        self.assertIsNone(re.search(r'set\s+"UO_SHARD_(OWNER|GM)_PASSWORD=', text, re.I))
        self.assertIn('set "UO_SHARD_BIND=127.0.0.1"', text)

    def test_patch_takes_no_password_from_the_account_name(self):
        text = (HERE / "patches" / "0001-headless-owner-account.patch").read_text(encoding="utf-8")
        self.assertNotIn("new Account(username, username)", text)
        self.assertIn('GetEnvironmentVariable("UO_SHARD_GM_PASSWORD")', text)

    def test_the_secrets_file_is_ignored_by_git(self):
        for rel in ("secrets.bat", "build/workspace/shard/secrets.bat", "workspace/shard/secrets.bat.tmp"):
            r = subprocess.run(["git", "-C", str(ROOT), "check-ignore", "-q", "--no-index", rel])
            self.assertEqual(r.returncode, 0, f"{rel} is not ignored")


if __name__ == "__main__":
    unittest.main()
