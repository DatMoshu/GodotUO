"""Actual CMD forwarding regression; no engine, install, profile or shard."""
from __future__ import annotations

import importlib.util
import json
import os
from pathlib import Path
import shutil
import subprocess
import sys

import pytest

HERE = Path(__file__).resolve().parent
ROOT = HERE.parents[1]
spec = importlib.util.spec_from_file_location("play_launcher", HERE / "run.py")
play = importlib.util.module_from_spec(spec)
spec.loader.exec_module(play)


def test_frames_moves_to_engine_and_last_value_wins():
    assert play.command(["engine", "--path", "project", "--", "--frames", "2",
                         "--offline", "--FRAMES", "7", "--gpu-world=off"]) == [
        "engine", "--path", "project", "--quit-after", "7", "--",
        "--offline", "--gpu-world=off"]


def test_missing_frames_value_reports_error():
    assert play.main(["engine", "--", "--frames"]) == 1


def test_engine_quit_after_keeps_precedence_over_frames():
    assert play.command(["engine", "--path", "project", "--quit-after", "20",
                         "--", "--frames", "7"]) == [
        "engine", "--path", "project", "--quit-after", "7", "--quit-after", "20", "--"]


@pytest.fixture
def launcher(tmp_path):
    if os.name != "nt":
        pytest.skip("CMD end-to-end regression is Windows-only")
    root = tmp_path / "synthetic checkout"
    for folder in ("launchers/game", "launchers/_shared", "tools/play",
                   "tools/datasources", "build/datasources"):
        (root / folder).mkdir(parents=True)
    shutil.copyfile(ROOT / "launchers/game/play.bat", root / "launchers/game/play.bat")
    shutil.copyfile(HERE / "run.py", root / "tools/play/run.py")
    (root / "launchers/_shared/common.bat").write_bytes(b"@echo off\r\nexit /b 0\r\n")
    (root / "tools/datasources/run.py").write_text(
        "import os, pathlib, sys\n"
        "pathlib.Path(sys.argv[sys.argv.index('--bat')+1]).write_text('@echo off\\n')\n"
        "sys.exit(int(os.environ.get('TEST_DATA_RC', '0')))\n", encoding="utf-8")
    (root / "capture.py").write_text(
        "import json, os, pathlib, sys\n"
        "pathlib.Path(os.environ['TEST_CAPTURE']).write_text(json.dumps(sys.argv[1:]))\n"
        "sys.exit(int(os.environ.get('TEST_ENGINE_RC', '0')))\n", encoding="utf-8")
    (root / "engine.cmd").write_bytes(
        f'@echo off\r\n"{sys.executable}" "%~dp0capture.py" %*\r\nexit /b %ERRORLEVEL%\r\n'.encode())
    env = dict(os.environ)
    for key in ("GUO_ENGINE_ARGS", "UO_FILES_OVERRIDE", "TEST_DATA_RC", "TEST_ENGINE_RC"):
        env.pop(key, None)
    env.update(UO_PYTHON=sys.executable, UO_TOOLS=str(root / "tools"),
               UO_BUILD=str(root / "build"), GODOT_CONSOLE=str(root / "engine.cmd"),
               UO_GODOT_PROJECT=str(root / "project folder"),
               TEST_CAPTURE=str(root / "argv.json"))

    def invoke(raw_args, **overrides):
        result = subprocess.run(
            f'"{os.environ.get("COMSPEC", "cmd.exe")}" /d /s /c '
            f'""{root / "launchers/game/play.bat"}" {raw_args}"',
            env={**env, **overrides}, capture_output=True, text=True, timeout=20)
        capture = root / "argv.json"
        argv = json.loads(capture.read_text()) if capture.exists() else None
        return result, argv

    return root, invoke


@pytest.mark.parametrize("raw,expected", [
    ("", []),
    ("--gpu-world=off --multimesh-sprites=off --shared-cover",
     ["--gpu-world=off", "--multimesh-sprites=off", "--shared-cover"]),
    ('"--gpu-world=off" "--multimesh-sprites=off"',
     ["--gpu-world=off", "--multimesh-sprites=off"]),
    ("--gpu-world --multimesh-sprites=on --other off",
     ["--gpu-world", "--multimesh-sprites=on", "--other", "off"]),
    ('--label "a=b c!" --offline', ["--label", "a=b c!", "--offline"]),
])
def test_cmd_play_preserves_client_argv(launcher, raw, expected):
    root, invoke = launcher
    result, argv = invoke(raw)
    assert result.returncode == 0, result.stdout + result.stderr
    assert argv == ["--path", str(root / "project folder"), "--", *expected]


def test_cmd_play_frames_engine_flags_override_and_exit_code(launcher):
    root, invoke = launcher
    result, argv = invoke(
        "--frames 4 --gpu-world=off --FRAMES 9 --offline",
        GUO_ENGINE_ARGS='--resolution 640x480 --rendering-method gl_compatibility',
        UO_FILES_OVERRIDE=str(root / "data override"), TEST_ENGINE_RC="17", TEST_DATA_RC="3")
    assert result.returncode == 17, result.stdout + result.stderr
    assert argv == ["--path", str(root / "project folder"), "--quit-after", "9", "--resolution", "640x480",
                    "--rendering-method", "gl_compatibility", "--",
                    "--files-override", str(root / "data override"), "--gpu-world=off", "--offline"]


def test_cmd_play_missing_frames_value_never_starts_engine(launcher):
    _, invoke = launcher
    result, argv = invoke("--frames")
    assert result.returncode == 1
    assert "--frames requires a frame count" in result.stderr
    assert argv is None
