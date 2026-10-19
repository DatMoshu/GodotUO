import tempfile
import unittest
from pathlib import Path
from types import SimpleNamespace
from unittest.mock import patch

from run import PRESET_NAME, device_args, export_command, release_env, release_signing_problems


class ReleaseSigningTests(unittest.TestCase):
    def setUp(self):
        temp = tempfile.TemporaryDirectory()
        self.addCleanup(temp.cleanup)
        self.root = Path(temp.name) / "checkout"
        self.root.mkdir()
        self.keystore = Path(temp.name) / "keys" / "release.keystore"
        self.keystore.parent.mkdir()
        self.keystore.write_bytes(b"ks")

    def paths(self, keystore=..., user="guo", password="secret1"):
        cfg = SimpleNamespace(root=self.root, android_release_keystore_user=user,
                              android_release_keystore_password=password)
        return SimpleNamespace(cfg=cfg, release_keystore=self.keystore if keystore is ... else keystore,
                               java=lambda tool: None)

    def problems(self, p):
        return [what for what, _, _ in release_signing_problems(p, verify=False)]

    def test_a_complete_setup_has_no_problems(self):
        self.assertEqual(self.problems(self.paths()), [])

    def test_nothing_set_names_all_three_settings(self):
        rows = release_signing_problems(self.paths(keystore=None, user="", password=""), verify=False)
        details = " ".join(detail for _, detail, _ in rows)
        for setting in ("UO_ANDROID_RELEASE_KEYSTORE ", "UO_ANDROID_RELEASE_KEYSTORE_USER",
                        "UO_ANDROID_RELEASE_KEYSTORE_PASSWORD"):
            self.assertIn(setting, details + " ")
        # Every row says how to fix it, and the doctor launcher is named.
        self.assertTrue(all(fix for _, _, fix in rows))
        self.assertTrue(any("doctor.bat --release" in fix for _, _, fix in rows))

    def test_a_keystore_inside_the_checkout_is_refused(self):
        inside = self.root / "build" / "release.keystore"
        inside.parent.mkdir()
        inside.write_bytes(b"ks")
        rows = release_signing_problems(self.paths(keystore=inside), verify=False)
        self.assertEqual(len(rows), 1)
        self.assertIn("inside the checkout", rows[0][1])

    def test_a_missing_file_is_named(self):
        rows = release_signing_problems(self.paths(keystore=self.keystore.with_name("gone.keystore")),
                                        verify=False)
        self.assertIn("no file at", rows[0][1])

    def test_verify_without_keytool_is_a_problem(self):
        self.assertEqual(self.problems(self.paths()), [])
        rows = release_signing_problems(self.paths(), verify=True)
        self.assertIn("keytool not found", rows[0][1])

    def test_verify_sends_the_password_on_stdin_not_the_command_line(self):
        p = self.paths()
        p.java = lambda tool: Path("keytool")
        with patch("run.subprocess.run") as run:
            run.return_value = SimpleNamespace(returncode=0)
            self.assertEqual(release_signing_problems(p), [])
        args, kwargs = run.call_args
        self.assertNotIn("secret1", " ".join(args[0]))
        self.assertEqual(kwargs["input"], "secret1\n")

    def test_a_wrong_password_is_a_problem(self):
        p = self.paths()
        p.java = lambda tool: Path("keytool")
        with patch("run.subprocess.run") as run:
            run.return_value = SimpleNamespace(returncode=1)
            rows = release_signing_problems(p)
        self.assertIn("cannot open alias", rows[0][1])


class ReleaseExportTests(unittest.TestCase):
    def test_the_release_key_reaches_godot_through_its_environment(self):
        cfg = SimpleNamespace(android_release_keystore_user="guo", android_release_keystore_password="secret1")
        env = release_env(SimpleNamespace(cfg=cfg, release_keystore=Path("K:/keys/release.keystore")))
        self.assertEqual(env["GODOT_ANDROID_KEYSTORE_RELEASE_PATH"], str(Path("K:/keys/release.keystore")))
        self.assertEqual(env["GODOT_ANDROID_KEYSTORE_RELEASE_USER"], "guo")
        self.assertEqual(env["GODOT_ANDROID_KEYSTORE_RELEASE_PASSWORD"], "secret1")

    def test_release_uses_export_release_and_never_puts_the_key_on_the_command_line(self):
        cmd = export_command(Path("godot"), Path("proj"), Path("out.apk"), release=True)
        self.assertIn("--export-release", cmd)
        self.assertNotIn("--export-debug", cmd)
        self.assertEqual(cmd[-2:], [PRESET_NAME, Path("out.apk")])

    def test_debug_is_unchanged(self):
        self.assertIn("--export-debug", export_command(Path("godot"), Path("proj"), Path("out.apk"), release=False))


class ReleaseArgsTests(unittest.TestCase):
    def test_a_release_build_carries_no_dev_account(self):
        cfg = SimpleNamespace(android_client_data="/sdcard/uo", android_account="devaccount")
        p = SimpleNamespace(cfg=cfg)
        self.assertIn("--account devaccount", device_args(p, ""))
        self.assertEqual(device_args(p, "", sound=True, client_data=False, account=False), "-- --play")


class PresetTemplateTests(unittest.TestCase):
    def test_the_template_never_carries_a_release_key(self):
        text = (Path(__file__).resolve().parent / "export_presets.template.cfg").read_text(encoding="utf-8")
        for key in ("keystore/release", "keystore/release_user", "keystore/release_password"):
            self.assertIn(f'{key}=""', text)


if __name__ == "__main__":
    unittest.main()
