"""Hermetic configuration precedence and path-resolution regressions."""
import json
import os
from pathlib import Path
import sys
import tempfile
import unittest
from unittest.mock import patch

sys.path.insert(0, str(Path(__file__).resolve().parents[1]))
from guo.config import find_repo_root, load_config, parse_config_bat
from guo import datasources
from guo.formats import required_files


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
        # The platform defaults read this machine's registry and folders; the
        # tests name their own.
        self.defaults_lookup = patch("guo.datasources.platform_defaults", return_value=[])
        self.defaults_lookup.start()
        self.addCleanup(self.defaults_lookup.stop)

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
        if sys.platform == "win32":
            self.assertTrue(config.godot_console_exe.name.endswith("_console.exe"))
        else:
            self.assertEqual(config.godot_console_exe, config.godot_exe)

    def test_linux_godot_build_names(self):
        with patch("guo.config.sys.platform", "linux"), patch("guo.config.platform.machine", return_value="x86_64"):
            self.defaults.write_text('set "GODOT_VERSION=4.7.2-stable"\nset "GODOT_FLAVOR=mono_win64"', encoding="utf-8")
            config = load_config(self.root)
        # config.bat's Windows pin means "this OS's build" anywhere else.
        self.assertEqual(config.godot_flavor, "mono_linux_x86_64")
        folder = self.root / "tools/godot/Godot_v4.7.2-stable_mono_linux_x86_64"
        self.assertEqual(config.godot_exe, folder / "Godot_v4.7.2-stable_mono_linux.x86_64")
        self.assertEqual(config.godot_console_exe, config.godot_exe)

    def test_backslash_paths_are_native(self):
        self.defaults.write_text('set "UO_WORLD_PROJECT=%UO_ROOT%\\build\\world\\default"', encoding="utf-8")
        self.assertEqual(load_config(self.root).world_project, self.root / "build" / "world" / "default")

    def test_unexpanded_cache_dir_falls_back_to_user_data(self):
        self.defaults.write_text('if not defined UO_CACHE_DIR set "UO_CACHE_DIR=%LOCALAPPDATA%\\GUO\\cache"', encoding="utf-8")
        self.assertEqual(load_config(self.root).cache_dir, self.root / "fixture-home" / ".local" / "share" / "GUO" / "cache")

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


def make_install(folder: Path, skip: str = "") -> Path:
    """A fake data set: one empty file per form of every required entry, but `skip`."""
    folder.mkdir(parents=True, exist_ok=True)
    for f in required_files():
        if f.key == skip:
            continue
        for name in (f.uop[:1] or f.mul[:1] + f.indexed_by):
            (folder / name).write_bytes(b"")
    return folder


def make_custom(folder: Path, mode: str, files: dict | None = None, ea: bool = False) -> Path:
    folder.mkdir(parents=True, exist_ok=True)
    for name in files or {}:
        (folder / name).write_bytes(b"x")
    (folder / "guo_data.json").write_text(json.dumps({
        "format": "guo/data-folder@1", "name": "fixture", "mode": mode,
        "files": files or {}, "contains_ea_data": ea}), encoding="utf-8")
    return folder


class DataSourceTests(unittest.TestCase):
    """ADR-0021: custom folder, then the install (environment, setting, default), then the wizard."""

    def setUp(self):
        self.temp = tempfile.TemporaryDirectory(prefix="guo-datasources-test-")
        self.addCleanup(self.temp.cleanup)
        self.dir = Path(self.temp.name)

    def test_validity_is_the_required_set(self):
        good = make_install(self.dir / "good")
        self.assertTrue(datasources.validate_install(good).ok)
        key = required_files()[0].key
        bad = datasources.validate_install(make_install(self.dir / "bad", skip=key))
        self.assertFalse(bad.ok)
        self.assertEqual(bad.missing, [key])
        self.assertFalse(datasources.validate_install(None).ok)
        self.assertFalse(datasources.validate_install(Path("")).ok)

    def test_a_mul_without_its_index_is_not_valid(self):
        entry = next(f for f in required_files() if f.indexed_by and not f.uop)
        folder = make_install(self.dir / "noindex")
        for name in entry.indexed_by:
            (folder / name).unlink()
        self.assertIn(entry.key, datasources.validate_install(folder).missing)

    def test_order_environment_then_setting_then_default(self):
        env, setting, default = (make_install(self.dir / n) for n in ("env", "setting", "default"))
        r = datasources.resolve("", str(env), str(setting), [default])
        self.assertEqual((r.source, r.client_data, r.origin), ("install", env, "environment"))
        r = datasources.resolve("", "", str(setting), [default])
        self.assertEqual((r.client_data, r.origin), (setting, "setting"))
        r = datasources.resolve("", "", "", [self.dir / "missing", default])
        self.assertEqual((r.client_data, r.origin), (default, "default"))

    def test_a_broken_setting_is_reported_not_replaced(self):
        default = make_install(self.dir / "default")
        r = datasources.resolve("", "", str(self.dir / "typo"), [default])
        self.assertEqual(r.source, "wizard")
        self.assertIsNone(r.client_data)
        self.assertTrue(any("typo" in n for n in r.notes))
        self.assertIn("first-run wizard", r.message())

    def test_nothing_anywhere_is_the_wizard(self):
        r = datasources.resolve("", "", "", [])
        self.assertFalse(r.ok)
        self.assertEqual(r.source, "wizard")

    def test_complete_custom_folder_comes_first(self):
        install = make_install(self.dir / "install")
        custom = make_custom(make_install(self.dir / "custom"), "complete")
        r = datasources.resolve(str(custom), str(install), "", [])
        self.assertEqual((r.source, r.client_data, r.custom), ("custom", custom, custom))

    def test_incomplete_complete_folder_falls_back_to_the_install(self):
        install = make_install(self.dir / "install")
        custom = make_custom(self.dir / "custom", "complete")
        r = datasources.resolve(str(custom), str(install), "", [])
        self.assertEqual((r.source, r.client_data), ("install", install))
        self.assertTrue(any("custom" in n for n in r.notes))

    def test_layered_folder_over_the_install(self):
        install = make_install(self.dir / "install")
        custom = make_custom(self.dir / "custom", "layered", {"tiledata.mul": {"sha1": "x"}, "artLegacyMUL.uop": {}})
        r = datasources.resolve(str(custom), "", str(install), [])
        self.assertEqual((r.source, r.client_data, r.origin), ("install+custom", install, "setting"))
        self.assertEqual(r.overrides, {"tiledata.mul": custom / "tiledata.mul", "artlegacymul.uop": custom / "artLegacyMUL.uop"})
        out = datasources.write_override(r, self.dir / "build" / "override.txt")
        self.assertEqual(out.read_text(encoding="utf-8").splitlines()[1], f"tiledata.mul={custom / 'tiledata.mul'}")

    def test_layered_folder_alone_is_the_wizard(self):
        custom = make_custom(self.dir / "custom", "layered", {"tiledata.mul": {}})
        r = datasources.resolve(str(custom), "", "", [])
        self.assertEqual(r.source, "wizard")
        self.assertTrue(any("needs a valid UO install" in n for n in r.notes))

    def test_manifest_is_checked(self):
        install = make_install(self.dir / "install")
        (self.dir / "plain").mkdir()
        ea = make_custom(self.dir / "ea", "layered", {"a.mul": {}}, ea=True)
        gone = make_custom(self.dir / "gone", "layered", {"a.mul": {}})
        (gone / "a.mul").unlink()
        undeclared = make_custom(self.dir / "undeclared", "layered", {"a.mul": {}})
        m = json.loads((undeclared / "guo_data.json").read_text(encoding="utf-8"))
        del m["contains_ea_data"]
        (undeclared / "guo_data.json").write_text(json.dumps(m), encoding="utf-8")
        esc = make_custom(self.dir / "esc", "layered", {})
        m = json.loads((esc / "guo_data.json").read_text(encoding="utf-8"))
        m["files"] = {"../install/tiledata.mul": {}}
        (esc / "guo_data.json").write_text(json.dumps(m), encoding="utf-8")
        cases = {
            "no manifest": (self.dir / "plain", "no guo_data.json"),
            "undeclared": (undeclared, "contains_ea_data"),
            "missing file": (gone, "not a file"),
            "path escape": (esc, "not a file"),
        }
        for label, (folder, needle) in cases.items():
            r = datasources.resolve(str(folder), str(install), "", [])
            self.assertEqual(r.source, "install", label)
            self.assertTrue(any(needle in n for n in r.notes), label)
        # EA-derived data is allowed in a local folder, and marked as such.
        r = datasources.resolve(str(ea), str(install), "", [])
        self.assertEqual((r.source, r.local_only), ("install+custom", True))
        self.assertIn("never shipped", r.message())

    def test_runtime_table_matches_formats(self):
        """The client's DataRequirements.g.cs is generated from FILE_REGISTRY and must be current."""
        import importlib.util

        repo = Path(__file__).resolve().parents[2]
        spec = importlib.util.spec_from_file_location("datasources_run", repo / "tools" / "datasources" / "run.py")
        run = importlib.util.module_from_spec(spec)
        spec.loader.exec_module(run)
        have = (repo / run.GENERATED).read_text(encoding="utf-8").replace("\r\n", "\n")
        self.assertEqual(have, run.generated_cs(), "run: python tools/datasources/run.py gen-cs")
        for f in required_files():
            self.assertIn(f'new("{f.key}", ', have)

    def test_config_uses_the_same_order(self):
        root = self.dir / "repo"
        shared = root / "launchers" / "_shared"
        shared.mkdir(parents=True)
        (shared / "config.bat").write_text("", encoding="utf-8")
        setting = make_install(self.dir / "setting")
        (shared / "config.local.bat").write_text(f'set "UO_CLIENT_DATA={setting}"', encoding="utf-8")
        with patch.dict(os.environ, {"UO_CACHE_DIR": str(self.dir / "cache")}, clear=True), patch("guo.datasources.platform_defaults", return_value=[]):
            cfg = load_config(root)
            self.assertEqual((cfg.client_data, cfg.client_data_setting, cfg.client_data_env), (setting, str(setting), ""))
            self.assertEqual(datasources.resolve_config(cfg).origin, "setting")
            os.environ["UO_CLIENT_DATA"] = str(self.dir / "broken")
            cfg = load_config(root)
            self.assertEqual(cfg.client_data, self.dir / "broken")
            self.assertEqual(datasources.resolve_config(cfg).source, "wizard")
        default = make_install(self.dir / "default")
        (shared / "config.local.bat").write_text("", encoding="utf-8")
        with patch.dict(os.environ, {"UO_CACHE_DIR": str(self.dir / "cache")}, clear=True), patch("guo.datasources.platform_defaults", return_value=[default]):
            self.assertEqual(load_config(root).client_data, default)

if __name__ == "__main__":
    unittest.main()
