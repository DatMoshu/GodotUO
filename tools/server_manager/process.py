"""Start, check and stop a managed server exactly as the editor's run bar does (ServerProfiles.cs, ManagedServer).

The server runs under the OS shell, which appends its stdout and stderr to `servers/<id>/server.console.log`; the
shell's identity (PID, start time in .NET UTC ticks, executable) goes to `servers/<id>/process.json`. Stop ends that
shell's process tree only when all three still match, so a recycled PID or a server somebody else started is never
touched. The editor and this module read and write the same record, so either can stop what the other started.

Windows only for now, like the lab: the start time is the kernel's creation time, which is what .NET's
Process.StartTime reads there.
"""

from __future__ import annotations

import json
import os
import subprocess
import sys
import time
import uuid
from pathlib import Path

# .NET DateTime ticks (100 ns since 0001-01-01) at the Windows FILETIME epoch (1601-01-01).
FILETIME_TO_TICKS = 504911232000000000
QUERY_LIMITED = 0x1000
CREATE_NO_WINDOW = 0x08000000


class ProcessError(RuntimeError):
    pass


def _kernel32():
    import ctypes
    from ctypes import wintypes
    k = ctypes.WinDLL("kernel32", use_last_error=True)
    k.OpenProcess.restype = wintypes.HANDLE
    k.OpenProcess.argtypes = (wintypes.DWORD, wintypes.BOOL, wintypes.DWORD)
    k.CloseHandle.argtypes = (wintypes.HANDLE,)
    k.GetProcessTimes.argtypes = (wintypes.HANDLE,) + (ctypes.POINTER(wintypes.FILETIME),) * 4
    k.QueryFullProcessImageNameW.argtypes = (wintypes.HANDLE, wintypes.DWORD, wintypes.LPWSTR, ctypes.POINTER(wintypes.DWORD))
    k.GetExitCodeProcess.argtypes = (wintypes.HANDLE, ctypes.POINTER(wintypes.DWORD))
    return k


def identity(pid: int) -> tuple[int, str] | None:
    """(start time in .NET UTC ticks, full executable path) of a live process, or None when it is gone or hidden."""
    if sys.platform != "win32":
        raise ProcessError("managed servers are started from Python on Windows only")
    import ctypes
    from ctypes import wintypes
    k = _kernel32()
    handle = k.OpenProcess(QUERY_LIMITED, False, pid)
    if not handle:
        return None
    try:
        code = wintypes.DWORD()
        if not k.GetExitCodeProcess(handle, ctypes.byref(code)) or code.value != 259:   # 259 = STILL_ACTIVE
            return None
        created, exited, kernel, user = (wintypes.FILETIME() for _ in range(4))
        if not k.GetProcessTimes(handle, ctypes.byref(created), ctypes.byref(exited), ctypes.byref(kernel), ctypes.byref(user)):
            return None
        filetime = (created.dwHighDateTime << 32) | created.dwLowDateTime
        size = wintypes.DWORD(32768)
        name = ctypes.create_unicode_buffer(size.value)
        if not k.QueryFullProcessImageNameW(handle, 0, name, ctypes.byref(size)):
            return None
        return filetime + FILETIME_TO_TICKS, os.path.abspath(name.value)
    finally:
        k.CloseHandle(handle)


def owned(state: Path) -> int | None:
    """The PID recorded in process.json when that exact process still runs, else None."""
    if not state.is_file():
        return None
    try:
        saved = json.loads(state.read_text(encoding="utf-8"))
        pid, started, exe = int(saved["Pid"]), int(saved["Started"]), str(saved["Executable"])
    except (OSError, ValueError, KeyError, TypeError):
        return None
    now = identity(pid)
    if now is None or now[0] != started or os.path.normcase(now[1]) != os.path.normcase(os.path.abspath(exe)):
        return None
    return pid


def _windows_argument(value: str) -> str:
    """CRT quoting, as ServerProfiles.WindowsArgument."""
    out, slashes = ['"'], 0
    for c in value:
        if c == "\\":
            slashes += 1
            continue
        out.append("\\" * (slashes * 2 + 1 if c == '"' else slashes))
        out.append(c)
        slashes = 0
    out.append("\\" * (slashes * 2))
    out.append('"')
    return "".join(out)


def start(executable: Path, workdir: Path, arguments: list[str], console: Path, state: Path,
          env: dict[str, str]) -> int:
    """Start the server under cmd.exe with its output appended to `console`; record the shell in `state`.
    Refused while the recorded process still runs. Returns the shell's PID."""
    if not executable.is_file() or not workdir.is_dir():
        raise ProcessError("choose an existing server executable and working directory")
    if owned(state) is not None:
        raise ProcessError("this server is already managed and running")
    console.parent.mkdir(parents=True, exist_ok=True)
    state.parent.mkdir(parents=True, exist_ok=True)
    child = {k: v for k, v in env.items() if not (k.startswith("UO_") and (k.endswith("_PROBE") or k == "UO_SERVER_CONTENT"))}
    key = "GUO_CONSOLE_" + uuid.uuid4().hex
    child[key + "_FILE"] = str(console)
    child[key + "_EXE"] = str(executable)
    child[key + "_ARGS"] = " ".join(_windows_argument(a) for a in arguments)
    comspec = os.path.join(os.environ.get("SystemRoot", r"C:\Windows"), "System32", "cmd.exe")
    line = f'"{comspec}" /d /v:on /s /c ""!{key}_EXE!" !{key}_ARGS! >> "!{key}_FILE!" 2>&1"'
    proc = subprocess.Popen(line, cwd=str(workdir), env=child, stdin=subprocess.DEVNULL,
                            stdout=subprocess.DEVNULL, stderr=subprocess.DEVNULL, creationflags=CREATE_NO_WINDOW)
    me = None
    for _ in range(50):
        me = identity(proc.pid)
        if me is not None:
            break
        time.sleep(0.05)
    if me is None:
        raise ProcessError("the server exited at once; see " + console.name)
    temporary = state.with_name(state.name + "." + uuid.uuid4().hex + ".tmp")
    temporary.write_text(json.dumps({"Pid": proc.pid, "Started": me[0], "Executable": me[1]}, indent=2), encoding="utf-8")
    os.replace(temporary, state)
    return proc.pid


def stop(state: Path, wait_s: float = 10.0) -> bool:
    """End the recorded process tree when it is still ours; remove the record. True when something was stopped."""
    pid = owned(state)
    if pid is not None:
        subprocess.run(["taskkill", "/PID", str(pid), "/T", "/F"], capture_output=True)
        end = time.monotonic() + wait_s
        while identity(pid) is not None:
            if time.monotonic() > end:
                raise ProcessError("the server has not stopped")
            time.sleep(0.2)
    state.unlink(missing_ok=True)
    return pid is not None
