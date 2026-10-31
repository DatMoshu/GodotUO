"""Real cmd.exe verifier regressions; synthetic filenames, never game data."""
import json
import os
from pathlib import Path
import shutil
import subprocess
import sys

import pytest

sys.path.insert(0, str(Path(__file__).resolve().parents[1]))
from guo import FILE_REGISTRY

ROOT = Path(__file__).resolve().parents[2]
pytestmark = pytest.mark.skipif(sys.platform != "win32", reason="Windows batch quoting")


@pytest.mark.parametrize("case", ["plain", "backslash", "double-backslash", "forward-slash", "local-config", "missing", "override"])
def test_verify_launcher_client_path(tmp_path, case):
    root = tmp_path / "checkout with spaces"
    shared = root / "launchers/_shared"
    pipeline = root / "launchers/pipeline"
    shared.mkdir(parents=True)
    pipeline.mkdir()
    for name in ("common.bat", "config.bat"):
        shutil.copyfile(ROOT / "launchers/_shared" / name, shared / name)
    launcher = pipeline / "01_verify_client_data.bat"
    shutil.copyfile(ROOT / "launchers/pipeline/01_verify_client_data.bat", launcher)
    shutil.copytree(ROOT / "tools/guo", root / "tools/guo", ignore=shutil.ignore_patterns("__pycache__"))
    (root / "tools/uodata").mkdir()
    shutil.copyfile(ROOT / "tools/uodata/run.py", root / "tools/uodata/run.py")
    install = tmp_path / "Program Files (x86)" / "Ultima Online EchoesOfSosaria"
    install.mkdir(parents=True)
    for entry in FILE_REGISTRY:
        for filename in (*entry.mul, *entry.uop, *entry.indexed_by):
            target = install / filename
            target.parent.mkdir(parents=True, exist_ok=True)
            target.touch()
    fake_engine = tmp_path / "engine.exe"
    fake_engine.touch()  # common.bat checks existence only; never executed.
    env = {k: v for k, v in os.environ.items() if not k.upper().startswith(("UO_", "GUO_", "GODOT_"))}
    env.update(UO_PYTHON=sys.executable, GODOT_EXE=str(fake_engine),
               UO_CACHE_DIR=str(tmp_path / "cache"), UO_WORKSPACE_DIR=str(tmp_path / "workspace"),
               UO_BUILD_DIR=str(tmp_path / "output with spaces"), UO_CLIENT_VERSION="7.0.107.76")
    configured = str(install)
    if case in ("backslash", "local-config", "missing"):
        configured += "\\"
    elif case == "double-backslash":
        configured += "\\\\"
    elif case == "forward-slash":
        configured = configured.replace("\\", "/") + "/"
    if case == "missing":
        configured = str(tmp_path / "missing install") + "\\"
    if case == "local-config":
        (shared / "config.local.bat").write_text('if not defined UO_CLIENT_DATA set "UO_CLIENT_DATA=' + configured + '"\n', encoding="utf-8")
    else:
        env["UO_CLIENT_DATA"] = configured
    args = [os.environ["ComSpec"], "/d", "/c", r"launchers\pipeline\01_verify_client_data.bat", "--quiet"]
    if case == "override":
        env["UO_CLIENT_DATA"] = str(tmp_path / "wrong install") + "\\"
        args += ["--data-dir", str(install), "--client-version", "7.0.99.1"]
    result = subprocess.run(args, cwd=root, env=env, capture_output=True, text=True, timeout=30)
    manifest = tmp_path / "output with spaces/client_manifest.json"
    if case == "missing":
        assert result.returncode == 3, result.stdout + result.stderr
        assert "--client-version" not in result.stdout and "--out" not in result.stdout
        assert not manifest.exists()
        return
    assert result.returncode == 0, result.stdout + result.stderr
    data = json.loads(manifest.read_text(encoding="utf-8"))
    assert Path(data["data_dir"]) == install
    assert data["client_version"] == ("7.0.99.1" if case == "override" else "7.0.107.76")
    assert data["ok"] and data["counts"]["missing_required"] == 0
