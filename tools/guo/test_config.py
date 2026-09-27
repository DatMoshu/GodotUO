"""Hermetic configuration precedence and path-resolution regressions."""
import os
from pathlib import Path
import sys
import tempfile
import unittest
from unittest.mock import patch

sys.path.insert(0, str(Path(__file__).resolve().parents[1]))
from guo.config import find_repo_root, load_config, parse_config_bat


class ConfigTests(unittest.TestCase):
    def setUp(self):
        self.temp = tempfile.TemporaryDirectory(prefix="guo-config-test-")
        self.addCleanup(self.temp.cleanup)
        self.root = Path(self.temp.name)
        self.shared = self.root / "launchers" / "_shared"
        self.shared.mkdir(parents=True)
        self.defaults = self.shared / "config.bat"
        self.local = self.shared / "config.local.bat"
        self.defaults.write_text('if not defined UO_CLIENT_VERSION set "UO_CLIENT_VERSION=default"\n', encoding="utf-8")
        self.environment = patch.dict(os.environ, {}, clear=True)
        self.environment.start()
        self.addCleanup(self.environment.stop)
        self.home_lookup = patch("guo.config.Path.home", return_value=self.root / "fixture-home")
        self.home_lookup.start()
        self.addCleanup(self.home_lookup.stop)

    def test_environment_local_default_precedence(self):
        self.assertEqual(load_config(self.root).client_version, "default")
        self.local.write_text('set "UO_CLIENT_VERSION=local"', encoding="utf-8")
        self.assertEqual(load_config(self.root).client_version, "local")
        os.environ["UO_CLIENT_VERSION"] = "environment"
        self.assertEqual(load_config(self.root).client_version, "environment")
        os.environ["UO_CLIENT_VERSION"] = ""
        self.assertEqual(load_config(self.root).client_version, "local")

    def test_parser_bom_comments_spaces_and_no_execution(self):
        self.defaults.write_text('REM set "BAD=comment"\r\necho should-not-run\r\nset "LABEL=two words"\r\nif not defined NEXT set "NEXT=%LABEL%/child"\r\n', encoding="utf-8-sig")
        values = parse_config_bat(self.defaults)
        self.assertNotIn("BAD", values)
        self.assertEqual(values["LABEL"], "two words")
        self.assertEqual(values["NEXT"], "two words/child")

    def test_environment_expands_references(self):
        os.environ["FIXTURE_BASE"] = str(self.root / "space folder")
        self.local.write_text('set "UO_CLIENT_DATA=%FIXTURE_BASE%/client"', encoding="utf-8")
        self.assertEqual(load_config(self.root).client_data, self.root / "space folder" / "client")

    def test_local_values_expand_default_references(self):
        self.local.write_text('set "FIXTURE_BASE=%UO_ROOT%/local"', encoding="utf-8")
        self.defaults.write_text('if not defined UO_CACHE_DIR set "UO_CACHE_DIR=%FIXTURE_BASE%/cache"', encoding="utf-8")
        self.assertEqual(load_config(self.root).cache_dir, self.root / "local" / "cache")

    def test_guards_preserve_first_assignment(self):
        self.defaults.write_text('set "VALUE=first"\nif not defined VALUE set "VALUE=second"\n', encoding="utf-8")
        self.assertEqual(parse_config_bat(self.defaults)["VALUE"], "first")

    def test_batch_names_are_case_insensitive(self):
        self.local.write_text('set "uo_client_version=local"\nset "base=folder"\nset "UO_CACHE_DIR=%BASE%/cache"', encoding="utf-8")
        config = load_config(self.root)
        self.assertEqual(config.client_version, "local")
        self.assertEqual(config.cache_dir, Path("folder/cache"))

    def test_unknown_reference_stays_visible(self):
        self.defaults.write_text('set "VALUE=%NOT_CONFIGURED%/child"', encoding="utf-8")
        self.assertEqual(parse_config_bat(self.defaults)["VALUE"], "%NOT_CONFIGURED%/child")

    def test_resolution_does_not_mutate_environment(self):
        os.environ["FIXTURE_BASE"] = "external"
        before = dict(os.environ)
        load_config(self.root)
        self.assertEqual(dict(os.environ), before)

    def test_guard_checks_named_variable(self):
        self.defaults.write_text('set "PRESENT=yes"\nif not defined PRESENT set "ABSENT=no"', encoding="utf-8")
        self.assertNotIn("ABSENT", parse_config_bat(self.defaults))

    def test_root_and_derived_paths(self):
        self.defaults.write_text('set "UO_WORLD_PROJECT=%UO_ROOT%/build/world/example"', encoding="utf-8")
        config = load_config(self.root)
        self.assertEqual(config.world_project, self.root / "build/world/example")
        self.assertEqual(config.godot_project, self.root / "godot/GUO")
        self.assertEqual(config.upstream_build, self.root / "build/cuo")
        self.assertTrue(config.godot_console_exe.name.endswith("_console.exe"))

    def test_store_defaults_are_checkout_relative(self):
        config = load_config(self.root)
        self.assertEqual(config.store_dir, self.root / "build/store_cdn")
        self.assertEqual(config.store_url, "http://127.0.0.1:18865")

    def test_store_local_root_expansion_and_environment_override(self):
        self.local.write_text('set "UO_STORE_DIR=%UO_ROOT%/custom packs"\nset "UO_STORE_URL=http://127.0.0.1:18866"', encoding="utf-8")
        config = load_config(self.root)
        self.assertEqual(config.store_dir, self.root / "custom packs")
        self.assertEqual(config.store_url, "http://127.0.0.1:18866")
        os.environ["UO_STORE_DIR"] = "relative packs"
        os.environ["UO_STORE_URL"] = "http://127.0.0.1:18867"
        config = load_config(self.root)
        self.assertEqual(config.store_dir, self.root / "relative packs")
        self.assertEqual(config.store_url, "http://127.0.0.1:18867")

    def test_store_absolute_path_is_not_rebased(self):
        absolute = self.root / "outside checkout"
        os.environ["UO_STORE_DIR"] = str(absolute)
        self.assertEqual(load_config(self.root).store_dir, absolute)

    def test_invalid_numeric_fallback_and_account_list(self):
        self.local.write_text('set "UO_SHARD_PORT=bad"\nset "UO_WEB_PORT=bad"\nset "UO_SHARD_GM_ACCOUNTS= alpha, ,beta "', encoding="utf-8")
        config = load_config(self.root)
        self.assertEqual(config.shard_port, 2593)
        self.assertEqual(config.web_port, 8060)
        self.assertEqual(config.shard_gm_accounts, ("alpha", "beta"))

    def test_find_root_from_nested_file_and_missing_marker(self):
        nested = self.root / "nested" / "script.py"
        nested.parent.mkdir()
        nested.touch()
        self.assertEqual(find_repo_root(nested), self.root)
        self.defaults.unlink()
        with self.assertRaises(RuntimeError):
            find_repo_root(nested)


if __name__ == "__main__":
    unittest.main()
