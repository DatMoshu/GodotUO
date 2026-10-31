"""Real cmd.exe launcher resolution with synthetic engine files; no engine runs."""
import json
import os
from pathlib import Path
import shutil
import subprocess
import sys

import pytest

ROOT = Path(__file__).resolve().parents[2]
pytestmark = pytest.mark.skipif(sys.platform != "win32", reason="Windows launcher")


@pytest.mark.parametrize("layout", ["absolute", "relative", "main", "own", "override"])
def test_launcher_engine_lookup(tmp_path, layout):
    main = tmp_path / "main checkout"
    tree = main if layout == "main" else tmp_path / "relocated tree"
    for root in {main, tree}:
        shared = root / "launchers/_shared"
        shared.mkdir(parents=True)
        shutil.copyfile(ROOT / "launchers/_shared/common.bat", shared / "common.bat")
        (shared / "config.bat").write_text('@echo off\nset GODOT_VERSION=test\nset GODOT_FLAVOR=mono_win64\n')
    metadata = main / ".git/worktrees/tree"
    metadata.mkdir(parents=True)
    (main / ".git/HEAD").write_text("ref: refs/heads/main\n")
    (metadata / "commondir").write_text("../..\n")
    if tree != main:
        pointer = os.path.relpath(metadata, tree) if layout == "relative" else str(metadata)
        (tree / ".git").write_text("gitdir: " + pointer.replace("\\", "/") + "\n")
    engine_root = tree if layout == "own" else main
    home = tmp_path / "explicit engine" if layout == "override" else engine_root / "tools/godot"
    release = home / "Godot_vtest_mono_win64"
    release.mkdir(parents=True)
    (release / "Godot_vtest_mono_win64.exe").touch()
    (release / "Godot_vtest_mono_win64_console.exe").touch()
    (main / "sources/ClassicUO").mkdir(parents=True)
    capture = tmp_path / "capture.py"
    result_file = tmp_path / "result.json"
    capture.write_text("import os,json,pathlib\npathlib.Path(" + repr(str(result_file)) + ").write_text(json.dumps({k:os.environ.get(k) for k in ['UO_MAIN_CHECKOUT','UO_GODOT_HOME','UO_UPSTREAM_DIR']}))\n")
    run = tmp_path / "run.cmd"
    run.write_text('@echo off\ncall "' + str(tree / 'launchers/_shared/common.bat') + '" --defer-shard-secrets\nif errorlevel 1 exit /b 1\n"' + sys.executable + '" "' + str(capture) + '"\n', newline="\r\n")
    env = {k:v for k,v in os.environ.items() if not k.upper().startswith(("UO_","GUO_","GODOT_"))}
    env.update(UO_CACHE_DIR=str(tmp_path / "cache"))
    if layout == "override":
        env["UO_GODOT_HOME"] = str(home)
    result = subprocess.run([os.environ["ComSpec"], "/d", "/c", str(run)], cwd=tmp_path, env=env, capture_output=True, text=True, timeout=15)
    assert result.returncode == 0, result.stdout + result.stderr
    values = json.loads(result_file.read_text())
    assert Path(values["UO_GODOT_HOME"]) == home
    assert Path(values["UO_UPSTREAM_DIR"]) == main / "sources"
    assert values["UO_MAIN_CHECKOUT"] is None if layout == "main" else Path(values["UO_MAIN_CHECKOUT"]) == main
