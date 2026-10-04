"""Source configuration and scoped compiler environment regressions."""
from __future__ import annotations

import contextlib
import io
import os
from pathlib import Path
import sys
import tempfile
from types import SimpleNamespace
import unittest
from unittest.mock import patch

sys.path.insert(0, str(Path(__file__).resolve().parents[1]))
from guo.config import load_config
from guo.process import build_child_env
from layout_import import run


class SourceConfigTests(unittest.TestCase):
    def test_source_command_selects_matching_installation(self):
        cfg = SimpleNamespace(layout_cdda_dir=Path("cdda"), layout_zomboid_dir=Path("zomboid"))
        for command in ("scan", "engine-bake", "zomboid-scan", "zomboid-resolve", "hybrid"):
            with self.subTest(command=command):
                expected = cfg.layout_cdda_dir if command in ("scan", "engine-bake") else cfg.layout_zomboid_dir
                self.assertEqual(run.source_path(cfg, command, None), expected)
                self.assertEqual(run.source_path(cfg, command, Path("override")), Path("override"))

    def test_missing_source_reports_relevant_key(self):
        cfg = SimpleNamespace(layout_cdda_dir=None, layout_zomboid_dir=None)
        for command, key in (("scan", "UO_LAYOUT_CDDA_DIR"), ("hybrid", "UO_LAYOUT_ZOMBOID_DIR")):
            with self.assertRaisesRegex(ValueError, key):
                run.source_path(cfg, command, None)

    def test_config_environment_local_default_precedence(self):
        with tempfile.TemporaryDirectory() as temporary, patch.dict(os.environ, {}, clear=True), \
             patch("pathlib.Path.home", return_value=Path(temporary)):
            root = Path(temporary)
            shared = root / "launchers" / "_shared"
            shared.mkdir(parents=True)
            defaults = shared / "config.bat"
            local = shared / "config.local.bat"
            keys = ("UO_LAYOUT_CDDA_DIR", "UO_LAYOUT_ZOMBOID_DIR")
            defaults.write_text('\n'.join(f'if not defined {key} set "{key}="' for key in keys))
            cfg = load_config(root)
            self.assertIsNone(cfg.layout_cdda_dir)
            self.assertIsNone(cfg.layout_zomboid_dir)
            defaults.write_text('\n'.join(f'if not defined {key} set "{key}=default"' for key in keys))
            self.assertEqual(load_config(root).layout_cdda_dir, Path("default"))
            local.write_text('\n'.join(f'if not defined {key} set "{key}=local"' for key in keys))
            cfg = load_config(root)
            self.assertEqual((cfg.layout_cdda_dir, cfg.layout_zomboid_dir), (Path("local"), Path("local")))
            for key in keys:
                os.environ[key] = "environment"
            cfg = load_config(root)
            self.assertEqual((cfg.layout_cdda_dir, cfg.layout_zomboid_dir), (Path("environment"), Path("environment")))

    def test_cli_configured_source_keeps_installation_output_guard(self):
        with tempfile.TemporaryDirectory() as temporary:
            source = Path(temporary) / "source"
            cfg = SimpleNamespace(layout_cdda_dir=source, layout_zomboid_dir=None,
                                  client_data=Path(temporary) / "retail")
            with patch("guo.load_config", return_value=cfg), contextlib.redirect_stderr(io.StringIO()) as output:
                code = run.main(["scan", "--db", str(source / "catalogue.sqlite")])
            self.assertEqual(code, 2)
            self.assertIn("outside the source installation", output.getvalue())
            self.assertFalse(source.exists())

    def test_cli_missing_source_fails_before_creating_database(self):
        with tempfile.TemporaryDirectory() as temporary:
            database = Path(temporary) / "catalogue.sqlite"
            cfg = SimpleNamespace(layout_cdda_dir=None, layout_zomboid_dir=None,
                                  client_data=Path(temporary) / "retail")
            with patch("guo.load_config", return_value=cfg), contextlib.redirect_stderr(io.StringIO()) as output:
                self.assertEqual(run.main(["scan", "--db", str(database)]), 2)
            self.assertIn("--source or set UO_LAYOUT_CDDA_DIR", output.getvalue())
            self.assertFalse(database.exists())

    def test_cli_explicit_source_overrides_configured_source(self):
        with tempfile.TemporaryDirectory() as temporary:
            root = Path(temporary)
            cfg = SimpleNamespace(layout_cdda_dir=root / "configured", layout_zomboid_dir=None,
                                  client_data=root / "retail")
            explicit = root / "explicit"
            with patch("guo.load_config", return_value=cfg), \
                 patch.object(run.cdda, "scan", return_value="synthetic") as scan, \
                 patch.object(run.cdda, "make_profile", return_value="profile"), \
                 patch.object(run.cdda, "coverage", return_value={"profile_status": "resolved"}), \
                 contextlib.redirect_stdout(io.StringIO()):
                self.assertEqual(run.main(["scan", "--source", str(explicit),
                                           "--db", str(root / "catalogue.sqlite")]), 0)
            self.assertEqual(scan.call_args.args[1], explicit)

    def test_windows_compiler_setting_is_child_only(self):
        with patch.dict(os.environ, {"UseSharedCompilation": "true"}), patch("guo.process.sys.platform", "win32"):
            env = build_child_env()
            self.assertEqual(env["UseSharedCompilation"], "false")
            self.assertEqual(sum(key.upper() == "USESHAREDCOMPILATION" for key in env), 1)
            self.assertEqual(os.environ["UseSharedCompilation"], "true")
            self.assertIsNot(env, os.environ)

    def test_other_platform_preserves_compiler_setting(self):
        with patch.dict(os.environ, {"UseSharedCompilation": "true"}), patch("guo.process.sys.platform", "linux"):
            env = {key.upper(): value for key, value in build_child_env().items()}
            self.assertEqual(env["USESHAREDCOMPILATION"], "true")


if __name__ == "__main__":
    unittest.main()
