"""Synthetic subprocess integration tests; no engine, shard or client data."""
import ctypes
import json
import os
from pathlib import Path
import shutil
import subprocess
import sys
import time
from unittest.mock import patch

import pytest

from guo import build_guard

GUARD = Path(build_guard.__file__).resolve()


def invoke(command, timeout="5", env=None):
    return subprocess.run([sys.executable, str(GUARD), "--timeout", timeout, "--", *command],
                          capture_output=True, text=True, timeout=15, env=env)


@pytest.mark.parametrize("code", [0, 7, 42])
def test_build_exit_status_is_preserved(code):
    result = invoke([sys.executable, "-c", f"import sys; print('child output'); sys.exit({code})"])
    assert result.returncode == code
    assert "child output" in result.stdout


def test_executable_and_arguments_with_spaces_are_preserved(tmp_path):
    folder = tmp_path / "python with spaces"
    folder.mkdir()
    executable = folder / Path(sys.executable).name
    shutil.copy2(sys.executable, executable)
    script = folder / "echo args.py"
    script.write_text("import json,sys; print(json.dumps(sys.argv[1:]))", encoding="utf-8")
    args = ["project with spaces", 'embedded "quotes"', "", "trailing slash\\", "a&b"]
    result = invoke([str(executable), str(script), *args])
    assert result.returncode == 0, result.stderr
    assert json.loads(result.stdout) == args


@pytest.mark.parametrize("value", ["", "0", "-1", "abc", "1.5", " 2", "86401", "2147483647",
                                  pytest.param("9" * 5000, id="overflow")])
def test_invalid_timeout_does_not_start_child(value, tmp_path):
    marker = tmp_path / "must not exist"
    result = invoke([sys.executable, "-c", f"open({str(marker)!r}, 'w').close()"], value)
    assert result.returncode == 2
    assert "UO_BUILD_TIMEOUT" in result.stderr
    assert not marker.exists()


@pytest.mark.parametrize("value", [None, "", "60"])
def test_default_timeout_matches_config_and_environment(value):
    env = {} if value is None else {"UO_BUILD_TIMEOUT": value}
    with patch.dict(os.environ, env, clear=True), patch.object(sys, "argv", ["guard", "--", "fake"]):
        with patch.object(build_guard, "run_bounded", return_value=0) as run:
            assert build_guard.main() == 0
            run.assert_called_once_with(["fake"], 60 if value == "60" else 180)


def is_running(pid):
    if sys.platform == "win32":
        from ctypes import wintypes as w
        api = ctypes.WinDLL("kernel32", use_last_error=True)
        api.OpenProcess.argtypes, api.OpenProcess.restype = [w.DWORD, w.BOOL, w.DWORD], w.HANDLE
        api.WaitForSingleObject.argtypes, api.WaitForSingleObject.restype = [w.HANDLE, w.DWORD], w.DWORD
        api.CloseHandle.argtypes = [w.HANDLE]
        handle = api.OpenProcess(0x100000, False, pid)
        if not handle:
            return False
        try:
            return api.WaitForSingleObject(handle, 0) == 258
        finally:
            api.CloseHandle(handle)
    # A killed orphan may remain a zombie until the host reaps it.
    stat = Path(f"/proc/{pid}/stat")
    if stat.exists() and stat.read_text().split(") ", 1)[1].startswith("Z"):
        return False
    try:
        os.kill(pid, 0)
        return True
    except ProcessLookupError:
        return False


@pytest.mark.parametrize("root_exits", [False, True])
def test_owned_descendants_cleaned_and_unrelated_child_survives(tmp_path, root_exits):
    pids = tmp_path / "descendant pids.json"
    script = tmp_path / "spawn tree.py"
    script.write_text(
        "import json,os,subprocess,sys,time\n"
        "if len(sys.argv) == 2:\n"
        " child = subprocess.Popen([sys.executable, __file__, sys.argv[1], 'leaf'])\n"
        " open(sys.argv[1], 'w').write(json.dumps([os.getpid(), child.pid]))\n"
        "else:\n"
        " time.sleep(60)\n", encoding="utf-8")
    # The build root spawns an intermediate child and exits early or hangs.
    root = ("import subprocess,sys,time; "
            "p=subprocess.Popen([sys.executable, sys.argv[1], sys.argv[2]]); "
            "time.sleep(0.5)" if root_exits else
            "import subprocess,sys,time; "
            "p=subprocess.Popen([sys.executable, sys.argv[1], sys.argv[2]]); time.sleep(60)")
    unrelated = subprocess.Popen([sys.executable, "-c", "import time; time.sleep(60)"])
    try:
        result = invoke([sys.executable, "-c", root, str(script), str(pids)], "2")
        assert result.returncode == (0 if root_exits else 124), result.stderr
        assert pids.exists(), result.stdout
        owned = json.loads(pids.read_text())
        deadline = time.monotonic() + 3
        while any(is_running(pid) for pid in owned) and time.monotonic() < deadline:
            time.sleep(0.05)
        assert not any(is_running(pid) for pid in owned)
        assert unrelated.poll() is None
        if not root_exits:
            assert "timed out after 2 s" in result.stdout
    finally:
        unrelated.terminate()
        unrelated.wait(timeout=5)


def test_build_child_disables_shared_compilation_only_on_windows():
    result = invoke([sys.executable, "-c",
                     "import os; print(os.environ.get('UseSharedCompilation'))"],
                    env={**os.environ, "UseSharedCompilation": "true"})
    assert result.returncode == 0
    assert result.stdout.strip() == ("false" if sys.platform == "win32" else "true")


@pytest.mark.parametrize("value", ["1", "180", "86400"])
def test_valid_timeout_boundaries(value):
    assert build_guard.parse_timeout(value) == int(value)


@pytest.mark.skipif(sys.platform != "win32", reason="Windows containment handshake")
def test_failed_job_assignment_does_not_start_build(tmp_path):
    marker = tmp_path / "must not start"
    with patch.object(build_guard.WindowsJob, "assign", side_effect=OSError("synthetic assignment failure")):
        with pytest.raises(OSError, match="synthetic assignment failure"):
            build_guard.run_bounded([sys.executable, "-c", f"open({str(marker)!r}, 'w').close()"], 2)
    assert not marker.exists()


def test_missing_executable_reports_launch_failure(tmp_path):
    result = invoke([str(tmp_path / "missing engine")])
    assert result.returncode == 2
    assert "[build]" in result.stderr
