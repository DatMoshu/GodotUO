"""Orchestration parity of launchers/dev/smoke.sh with smoke.bat.

This does not run Godot, client data, or the .NET test projects. Stubs record
the commands smoke.sh would start. A real `dotnet run` is a separate check
and is not claimed here.
"""
import os
from pathlib import Path
import stat
import subprocess
import sys
import tempfile
import unittest


ROOT = Path(__file__).resolve().parents[2]
BAT = ROOT / "launchers" / "dev" / "smoke.bat"
SH = ROOT / "launchers" / "dev" / "smoke.sh"

# Probes that identify a step. Order is the Windows script's order.
MARKERS = (
    "art_capacity/run.py",
    "network_tests/NetworkTests.csproj",
    "render_sort_tests/RenderSortTests.csproj",
    "DiagnosticsFunctionalProbe.tscn",
    "GuoEffectPolicyProbe.tscn",
    "GuoOptionsFlowProbe.tscn",
    "--lighting-speed-probe",
    "SpeedModeWeatherProbe.tscn",
    "WaterProbe.tscn",
    "server_lab/test_server_lab.py",
    "perf_lab",
    "announce/Announce.csproj",
    "--login-preflight-probe",
    "probe_rules_tests/ProbeRulesTests.csproj",
    "--keyboard-probe",
    "--pad-regression-probe",
)


def _text(path: Path) -> str:
    return path.read_text(encoding="utf-8").replace("\r\n", "\n")


def _commands(path: Path):
    """(label, command) pairs. A label applies until the next echo."""
    label = ""
    pairs = []
    for raw in _text(path).splitlines():
        line = raw.strip()
        if not line or line.startswith("REM ") or line.startswith("rem ") or line.startswith("#"):
            continue
        if line.startswith("echo") and "[smoke]" in line:
            label = line
            continue
        if line.startswith(("dotnet ", '"%GODOT', '"$GODOT', '"%UO_PYTHON', '"$UO_PYTHON', "$UO_PYTHON", "%UO_PYTHON%")):
            pairs.append((label, line))
    return pairs


def _markers(path: Path):
    found = []
    for _, command in _commands(path):
        normal = command.replace("\\", "/")
        hit = [marker for marker in MARKERS if marker in normal]
        if hit:
            found.append(max(hit, key=len))
    return found


class SmokeParityTests(unittest.TestCase):
    def test_shared_steps_and_labels_match(self):
        bat = _commands(BAT)
        sh = _commands(SH)
        self.assertEqual(_markers(BAT), _markers(SH))
        for path in (BAT, SH):
            labels = {command: label for label, command in _commands(path)}
            preflight = next(command for _, command in _commands(path) if "--login-preflight-probe" in command)
            keyboard = next(command for _, command in _commands(path) if "--keyboard-probe" in command)
            self.assertIn("login preflight", labels[preflight].lower())
            self.assertNotIn("keyboard", labels[preflight].lower())
            self.assertIn("keyboard", labels[keyboard].lower())
            effect = next(command for _, command in _commands(path) if "GuoEffectPolicyProbe.tscn" in command)
            options = next(command for _, command in _commands(path) if "GuoOptionsFlowProbe.tscn" in command)
            self.assertIn("--depth-world", effect)
            self.assertIn("--depth-world", options)

    def test_step_failure_is_recorded_and_does_not_stop_the_script(self):
        sh = _text(SH)
        self.assertIn("set -euo pipefail", sh)
        self.assertIn('. "$(dirname "$0")/../_shared/common.sh" || exit 1', sh)
        for _, command in _commands(SH):
            self.assertTrue(command.endswith("|| FAIL=1"), command)
        code, log, output = _run(fail_substr="network_tests/NetworkTests.csproj")
        self.assertEqual(code, 1)
        self.assertIn("[smoke] FAILED", output)
        self.assertNotIn("[smoke] OK", output)
        joined = "\n".join(log)
        self.assertIn("network_tests/NetworkTests.csproj", joined)
        self.assertLess(joined.index("network_tests/NetworkTests.csproj"), joined.index("render_sort_tests/RenderSortTests.csproj"))
        self.assertIn("server_lab/test_server_lab.py", joined)
        self.assertIn("--login-preflight-probe", joined)
        self.assertIn("--keyboard-probe", joined)
        # The stubs exited. Nothing here executed the C# projects or Godot.
        self.assertTrue(all(line.startswith(("dotnet ", "godot ", "py ")) for line in log))

    def test_clean_stub_run_reaches_ok_and_skips_labs_unless_asked(self):
        code, log, output = _run()
        self.assertEqual(code, 0, output)
        self.assertIn("[smoke] OK", output)
        joined = "\n".join(log)
        self.assertNotIn("--batcher-probe", joined)
        code, log, _ = _run(labs=True)
        self.assertEqual(code, 0)
        self.assertIn("--batcher-probe", "\n".join(log))

    def test_common_failure_does_not_start_steps(self):
        code, log, output = _run(need_godot=True)
        self.assertEqual(code, 1)
        self.assertEqual(log, [])
        self.assertIn("Godot not found", output)


def _run(fail_substr="", labs=False, need_godot=False):
    real_python = sys.executable
    with tempfile.TemporaryDirectory(prefix="guo-smoke-orch-") as temp:
        root = Path(temp)
        log_path = root / "invocations.txt"
        bin_dir = root / "bin"
        bin_dir.mkdir()
        _write_stub(bin_dir / "dotnet", "#!/bin/sh\nprintf 'dotnet %s\\n' \"$*\" >> \"$SMOKE_LOG\"\n"
                    "case \"$SMOKE_FAIL_SUBSTR\" in '' ) ;; *) case \"$*\" in *\"$SMOKE_FAIL_SUBSTR\"*) exit 1 ;; esac ;; esac\nexit 0\n")
        _write_stub(bin_dir / "godot-console", "#!/bin/sh\nprintf 'godot %s\\n' \"$*\" >> \"$SMOKE_LOG\"\nexit 0\n")
        _write_stub(bin_dir / "python-stub", "#!/usr/bin/env python3\nimport os, sys\n"
                    "args=' '.join(sys.argv[1:])\n"
                    "if 'shellenv' in args:\n"
                    "    os.execv(os.environ['REAL_PYTHON'], [os.environ['REAL_PYTHON'], *sys.argv[1:]])\n"
                    "open(os.environ['SMOKE_LOG'],'a',encoding='utf-8').write('py '+args+'\\n')\n"
                    "needle=os.environ.get('SMOKE_FAIL_SUBSTR','')\n"
                    "raise SystemExit(1 if needle and needle in args else 0)\n")
        env = os.environ.copy()
        env.update({
            "PATH": str(bin_dir) + os.pathsep + env.get("PATH", ""),
            "SMOKE_LOG": str(log_path),
            "SMOKE_FAIL_SUBSTR": fail_substr,
            "REAL_PYTHON": real_python,
            "UO_PYTHON": str(bin_dir / "python-stub"),
            "UO_CLIENT_DATA": str(root / "no-client-data"),
            "UO_CACHE_DIR": str(root / "cache"),
            "UO_BUILD_DIR": str(root / "build"),
            "GUO_NEEDS_GODOT": "1" if need_godot else "0",
        })
        if need_godot:
            env.pop("GODOT_EXE", None)
            env.pop("GODOT_CONSOLE", None)
        else:
            env["GODOT_CONSOLE"] = str(bin_dir / "godot-console")
            env["GODOT_EXE"] = str(bin_dir / "godot-console")
        if labs:
            env["GuoLabs"] = "true"
        else:
            env.pop("GuoLabs", None)
        proc = subprocess.run(["bash", str(SH)], cwd=ROOT, env=env, capture_output=True, text=True, check=False)
        log = log_path.read_text(encoding="utf-8").splitlines() if log_path.exists() else []
        return proc.returncode, log, proc.stdout + proc.stderr


def _write_stub(path: Path, text: str) -> None:
    path.write_text(text, encoding="utf-8")
    path.chmod(path.stat().st_mode | stat.S_IEXEC)


if __name__ == "__main__":
    unittest.main()
