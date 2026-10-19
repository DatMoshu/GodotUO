"""Exercise the real Windows launcher prefix with a temporary shard and synthetic secrets.

Stops before the server executable: no live shard, credentials or sockets are used.
"""
import json
import importlib.util
import os
from pathlib import Path
import shutil
import subprocess
import sys
import tempfile
import unittest
from unittest.mock import patch

ROOT = Path(__file__).resolve().parents[2]
sys.path.insert(0, str(ROOT / "tools"))
from guo import shard_secrets
from guo import load_config


@unittest.skipUnless(sys.platform == "win32", "executes the Windows batch launcher")
class LauncherSecretsTests(unittest.TestCase):
    def setUp(self):
        temp = tempfile.TemporaryDirectory(prefix="guo-secret-launch-")
        self.addCleanup(temp.cleanup)
        self.root = Path(temp.name)
        for folder in ("launchers/_shared", "launchers/shard", "tools/modernuo", "server/Distribution", "uo", "workspace"):
            (self.root / folder).mkdir(parents=True)
        for name in ("common.bat", "config.bat"):
            shutil.copyfile(ROOT / "launchers/_shared" / name, self.root / "launchers/_shared" / name)
        shutil.copytree(ROOT / "tools/guo", self.root / "tools/guo", ignore=shutil.ignore_patterns("__pycache__"))
        shutil.copyfile(ROOT / "tools/modernuo/configure.py", self.root / "tools/modernuo/configure.py")
        shutil.copytree(ROOT / "tools/modernuo/config", self.root / "tools/modernuo/config")
        (self.root / "server/Distribution/ModernUO.exe").touch()  # existence check only; never executed
        self.output = self.root / "observed.json"
        # Capture only into the temporary fixture, never print even synthetic values.
        (self.root / "capture.py").write_text(
            "import json,os,pathlib\n"
            "keys=('UO_SHARD_OWNER_PASSWORD','UO_SHARD_GM_PASSWORD','UO_BRIDGE_ADMIN_TOKEN')\n"
            "pathlib.Path('observed.json').write_text(json.dumps({k:os.environ.get(k) for k in keys}))\n",
            encoding="utf-8")
        prefix = (ROOT / "launchers/shard/run.bat").read_text(encoding="utf-8").split("echo [shard] %UO_SHARD_NAME%")[0]
        (self.root / "launchers/shard/check.bat").write_text(
            prefix + '\n"%UO_PYTHON%" "%UO_ROOT%\\capture.py"\nexit /b %ERRORLEVEL%\n',
            encoding="utf-8", newline="\r\n")
        self.env = {k: v for k, v in os.environ.items() if not k.upper().startswith(("UO_", "GUO_", "GODOT_"))}
        self.env.update(UO_WORKSPACE_DIR=str(self.root / "workspace"), UO_SHARD_DIST=str(self.root / "server/Distribution"),
                        UO_SHARD_SRC=str(self.root / "server"),
                        UO_CLIENT_DATA=str(self.root / "uo"), UO_CACHE_DIR=str(self.root / "cache"),
                        UO_BUILD_DIR=str(self.root / "build"), UO_PYTHON=sys.executable,
                        GODOT_EXE=str(self.root / "server/Distribution/ModernUO.exe"))

    def write_old(self):
        path = shard_secrets.path_for(self.root / "workspace")
        path.parent.mkdir(parents=True, exist_ok=True)
        path.write_text(''.join(f'if not defined {k} set "{k}={"A" * 20 if k in shard_secrets.PASSWORD_KEYS else "T" * 32}"\n'
                                for k in shard_secrets.KEYS), encoding="utf-8")

    def launch(self):
        result = subprocess.run([os.environ["ComSpec"], "/d", "/c", str(self.root / "launchers/shard/check.bat")],
                                cwd=self.root, env=self.env, capture_output=True, text=True, timeout=30)
        self.assertEqual(result.returncode, 0, "launcher fixture failed (output withheld)")
        return json.loads(self.output.read_text()), result.stdout + result.stderr

    def test_rotated_passwords_reach_the_server_environment(self):
        self.write_old()
        observed, output = self.launch()
        current = shard_secrets.read(shard_secrets.path_for(self.root / "workspace"))
        self.assertTrue(observed == current, "launcher retained pre-configure secrets")
        for key in shard_secrets.PASSWORD_KEYS:
            self.assertEqual(len(observed[key]), 16)
        self.assertTrue(all(value not in output for value in observed.values()), "secret leaked to output")

    def test_first_run_loads_generated_secrets(self):
        observed, _ = self.launch()
        self.assertTrue(observed == shard_secrets.read(shard_secrets.path_for(self.root / "workspace")))

    def test_explicit_environment_overrides_survive_rotation(self):
        self.write_old()
        explicit = {key: f"Explicit{index}" for index, key in enumerate(shard_secrets.KEYS)}
        self.env.update(explicit)
        observed, _ = self.launch()
        self.assertTrue(observed == explicit, "explicit environment was overwritten")

    def test_local_configuration_overrides_survive_rotation(self):
        self.write_old()
        explicit = {key: f"Local{index}" for index, key in enumerate(shard_secrets.KEYS)}
        (self.root / "launchers/_shared/config.local.bat").write_text(
            ''.join(f'if not defined {k} set "{k}={v}"\n' for k, v in explicit.items()), encoding="utf-8")
        observed, _ = self.launch()
        self.assertTrue(observed == explicit, "local configuration was overwritten")


class ShellExportsTests(unittest.TestCase):
    def test_deferred_shell_exports_omit_generated_values_then_reload_rotated_values(self):
        spec = importlib.util.spec_from_file_location("guo_shellenv_secrets_test", ROOT / "tools/shellenv/run.py")
        module = importlib.util.module_from_spec(spec)
        spec.loader.exec_module(module)
        with tempfile.TemporaryDirectory(prefix="guo-shell-secrets-") as temp:
            root = Path(temp)
            (root / "launchers/_shared").mkdir(parents=True)
            for name in ("config.bat", "config.sh"):
                shutil.copyfile(ROOT / "launchers/_shared" / name, root / "launchers/_shared" / name)
            workspace = root / "workspace"
            secret_file = shard_secrets.path_for(workspace)
            secret_file.parent.mkdir(parents=True)
            secret_file.write_text(''.join(f'if not defined {k} set "{k}={"A" * 20}"\n'
                                           for k in shard_secrets.KEYS), encoding="utf-8")
            env = {k: v for k, v in os.environ.items() if not k.upper().startswith(("UO_", "GUO_", "GODOT_"))}
            env["UO_WORKSPACE_DIR"] = str(workspace)
            with patch.dict(os.environ, env, clear=True), patch.object(module, "load_config", lambda: load_config(root)):
                deferred = module.exports(include_shard_secrets=False)
                self.assertTrue(all(k not in deferred for k in shard_secrets.KEYS))
                shard_secrets.ensure(workspace)
                current = shard_secrets.read(secret_file)
                resolved = module.exports()
                self.assertTrue(all(resolved[k] == current[k] for k in shard_secrets.KEYS))
                explicit = {key: f"Explicit{index}" for index, key in enumerate(shard_secrets.KEYS)}
                with patch.dict(os.environ, explicit):
                    self.assertTrue(all(module.exports()[k] == explicit[k] for k in explicit))


if __name__ == "__main__":
    unittest.main()
