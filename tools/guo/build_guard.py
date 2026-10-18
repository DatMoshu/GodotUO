"""Bound the build launcher and own only its child process tree (stdlib only)."""
from __future__ import annotations

import argparse
import json
import os
from pathlib import Path
import re
import signal
import subprocess
import sys

sys.path.insert(0, str(Path(__file__).resolve().parents[1]))
from guo.process import build_child_env, no_activate

DEFAULT_TIMEOUT = 180
MAX_TIMEOUT = 86400


def parse_timeout(value: str) -> int:
    if not re.fullmatch(r"[0-9]{1,5}", value) or not 1 <= int(value) <= MAX_TIMEOUT:
        raise ValueError("UO_BUILD_TIMEOUT must be an integer from 1 to 86400 seconds")
    return int(value)


class WindowsJob:
    """Enroll a waiting helper before it can spawn the build (no assignment race)."""

    def __init__(self):
        import ctypes as c
        from ctypes import wintypes as w

        class Basic(c.Structure):
            _fields_ = [("PerProcessUserTimeLimit", c.c_int64),
                        ("PerJobUserTimeLimit", c.c_int64), ("LimitFlags", w.DWORD),
                        ("MinimumWorkingSetSize", c.c_size_t),
                        ("MaximumWorkingSetSize", c.c_size_t),
                        ("ActiveProcessLimit", w.DWORD), ("Affinity", c.c_size_t),
                        ("PriorityClass", w.DWORD), ("SchedulingClass", w.DWORD)]

        class IO(c.Structure):
            _fields_ = [(name, c.c_uint64) for name in
                        ("ReadOperationCount", "WriteOperationCount", "OtherOperationCount",
                         "ReadTransferCount", "WriteTransferCount", "OtherTransferCount")]

        class Extended(c.Structure):
            _fields_ = [("BasicLimitInformation", Basic), ("IoInfo", IO),
                        ("ProcessMemoryLimit", c.c_size_t), ("JobMemoryLimit", c.c_size_t),
                        ("PeakProcessMemoryUsed", c.c_size_t), ("PeakJobMemoryUsed", c.c_size_t)]

        self.api = c.WinDLL("kernel32", use_last_error=True)
        for name, args, result in (
            ("CreateJobObjectW", [w.LPVOID, w.LPCWSTR], w.HANDLE),
            ("SetInformationJobObject", [w.HANDLE, c.c_int, w.LPVOID, w.DWORD], w.BOOL),
            ("AssignProcessToJobObject", [w.HANDLE, w.HANDLE], w.BOOL),
            ("TerminateJobObject", [w.HANDLE, w.UINT], w.BOOL),
            ("CloseHandle", [w.HANDLE], w.BOOL),
        ):
            fn = getattr(self.api, name)
            fn.argtypes, fn.restype = args, result
        self.handle = self.api.CreateJobObjectW(None, None)
        if not self.handle:
            raise c.WinError(c.get_last_error())
        info = Extended()
        info.BasicLimitInformation.LimitFlags = 0x2000  # KILL_ON_JOB_CLOSE
        if not self.api.SetInformationJobObject(self.handle, 9, c.byref(info), c.sizeof(info)):
            error = c.WinError(c.get_last_error())
            self.close()
            raise error

    def assign(self, proc):
        import ctypes as c
        if not self.api.AssignProcessToJobObject(self.handle, int(proc._handle)):
            raise c.WinError(c.get_last_error())

    def terminate(self):
        import ctypes as c
        if not self.api.TerminateJobObject(self.handle, 124):
            raise c.WinError(c.get_last_error())

    def close(self):
        if self.handle:
            self.api.CloseHandle(self.handle)
            self.handle = None


def run_bounded(command: list[str], timeout: int) -> int:
    job = WindowsJob() if sys.platform == "win32" else None
    proc = None
    try:
        if job:
            proc = subprocess.Popen([sys.executable, str(Path(__file__).resolve()), "--child"],
                                    stdin=subprocess.PIPE, **no_activate())
            job.assign(proc)
            # JSON retains spaces, quotes and empty arguments without a shell.
            proc.stdin.write(json.dumps(command).encode("utf-8"))
            proc.stdin.close()
        else:
            proc = subprocess.Popen(command, start_new_session=True, env=build_child_env())
        try:
            return proc.wait(timeout=timeout)
        except subprocess.TimeoutExpired:
            print(f"[build] timed out after {timeout} s; stopping the build tree", flush=True)
            if job:
                job.terminate()
            else:
                try:
                    os.killpg(proc.pid, signal.SIGKILL)
                except ProcessLookupError:
                    pass  # Root/group exited at the timeout boundary.
            proc.wait(timeout=10)
            return 124
    finally:
        if job:
            job.close()  # Remove descendants even after a normal root exit.
        elif proc:
            try:
                os.killpg(proc.pid, signal.SIGKILL)
            except ProcessLookupError:
                pass
        if proc:
            if proc.stdin and not proc.stdin.closed:
                proc.stdin.close()
            if proc.poll() is None:
                proc.kill()
            proc.wait(timeout=10)


def main() -> int:
    if sys.argv[1:] == ["--child"]:
        # Parent assigns this helper to its job before releasing stdin.
        try:
            command = json.loads(sys.stdin.buffer.read())
            return subprocess.call(command, env=build_child_env(), **no_activate())
        except (ValueError, OSError) as exc:
            print(f"[build] {exc}", file=sys.stderr)
            return 2
    parser = argparse.ArgumentParser(description=__doc__)
    parser.add_argument("--timeout", default=os.environ.get("UO_BUILD_TIMEOUT") or str(DEFAULT_TIMEOUT))
    parser.add_argument("command", nargs=argparse.REMAINDER)
    args = parser.parse_args()
    try:
        timeout = parse_timeout(args.timeout)
        command = args.command[1:] if args.command[:1] == ["--"] else args.command
        if not command:
            raise ValueError("a build command is required after --")
        return run_bounded(command, timeout)
    except (ValueError, OSError, subprocess.SubprocessError) as exc:
        print(f"[build] {exc}", file=sys.stderr)
        return 2


if __name__ == "__main__":
    sys.exit(main())
