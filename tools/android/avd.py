"""GUO's own Android emulators (AVDs): create, start, stop, by name and serial.

Every AVD the project uses is in device_profiles.json, with the console port
it runs on; the port fixes its adb serial (emulator-<port>). Everything here
addresses the emulator by that serial and checks `emu avd name` before acting,
so a run meant for guo_phone never installs onto, taps or stops another
session's emulator or a physical device that happens to be attached.

    python tools\android\run.py avd list
    python tools\android\run.py avd create guo_phone
    python tools\android\run.py avd start guo_phone      boots it, headless
    python tools\android\run.py avd stop guo_phone
    python tools\android\run.py smoke --avd guo_phone   boot, smoke, uninstall, stop

An emulator is started only with enough free memory (MIN_FREE_RAM_GB), and
stopped only if this run started it.
"""

from __future__ import annotations

import ctypes
import json
import os
import subprocess
import sys
import time
from dataclasses import dataclass
from pathlib import Path

HERE = Path(__file__).resolve().parent
CATALOG_FILE = HERE / "device_profiles.json"

MIN_FREE_RAM_GB = 10.0
BOOT_TIMEOUT_S = 300
STOP_TIMEOUT_S = 60

# What the README found works for Godot on the emulator: Vulkan off, so Godot
# falls back to GLES on the host GPU. -no-window keeps the run off the owner's
# screen; -no-snapshot boots cold and saves nothing, so a run leaves the AVD
# as it found it.
EMULATOR_FLAGS = ["-no-window", "-no-snapshot", "-no-audio", "-no-boot-anim",
                  "-gpu", "host", "-feature", "-Vulkan"]


@dataclass(frozen=True)
class Avd:
    name: str
    port: int
    sdk_device: str
    system_image: str

    @property
    def serial(self) -> str:
        return f"emulator-{self.port}"


def catalog(path: Path = CATALOG_FILE) -> dict:
    return json.loads(path.read_text(encoding="utf-8"))


def avds(data: dict | None = None) -> dict[str, Avd]:
    """Every AVD in the catalog, once each (several profiles share guo_phone)."""
    data = data or catalog()
    found: dict[str, Avd] = {}
    for prof in data["profiles"]:
        avd = Avd(prof["avd"], int(prof["port"]), prof["sdk_device"], data["system_image"])
        if avd.name in found and found[avd.name].port != avd.port:
            raise ValueError(f"{avd.name} is on two ports in {CATALOG_FILE.name}")
        found.setdefault(avd.name, avd)
    return found


def find(name: str, data: dict | None = None) -> Avd:
    known = avds(data)
    if name not in known:
        sys.exit(f"[android] {name} is not one of GUO's AVDs ({', '.join(sorted(known))}); "
                 f"see {CATALOG_FILE.name}")
    return known[name]


def free_ram_gb() -> float:
    """Physical memory free right now, in GiB."""
    if os.name == "nt":
        class MemoryStatus(ctypes.Structure):
            _fields_ = [("dwLength", ctypes.c_ulong), ("dwMemoryLoad", ctypes.c_ulong),
                        ("ullTotalPhys", ctypes.c_ulonglong), ("ullAvailPhys", ctypes.c_ulonglong),
                        ("ullTotalPageFile", ctypes.c_ulonglong), ("ullAvailPageFile", ctypes.c_ulonglong),
                        ("ullTotalVirtual", ctypes.c_ulonglong), ("ullAvailVirtual", ctypes.c_ulonglong),
                        ("ullAvailExtendedVirtual", ctypes.c_ulonglong)]
        status = MemoryStatus()
        status.dwLength = ctypes.sizeof(MemoryStatus)
        ctypes.windll.kernel32.GlobalMemoryStatusEx(ctypes.byref(status))
        return status.ullAvailPhys / 2**30
    for line in Path("/proc/meminfo").read_text().splitlines():
        if line.startswith("MemAvailable:"):
            return int(line.split()[1]) / 2**20
    return 0.0


def emulator_exe(sdk: Path) -> Path:
    for name in ("emulator.exe", "emulator"):
        exe = sdk / "emulator" / name
        if exe.exists():
            return exe
    sys.exit(f"[android] no emulator under {sdk / 'emulator'}; install the SDK's Emulator package")


def avdmanager_exe(sdk: Path) -> Path:
    for name in ("avdmanager.bat", "avdmanager"):
        exe = sdk / "cmdline-tools" / "latest" / "bin" / name
        if exe.exists():
            return exe
    sys.exit("[android] no avdmanager; install the SDK's Command-line Tools (latest)")


def existing(sdk: Path) -> list[str]:
    out = subprocess.run([str(emulator_exe(sdk)), "-list-avds"], capture_output=True, text=True).stdout
    return [line.strip() for line in out.splitlines() if line.strip() and not line.startswith(("INFO", "WARNING"))]


def create(sdk: Path, avd: Avd) -> int:
    if avd.name in existing(sdk):
        print(f"[android] {avd.name} exists")
        return 0
    image = sdk / Path(avd.system_image.replace(";", "/"))
    if not image.is_dir():
        sys.exit(f"[android] missing system image {avd.system_image}: "
                 f'sdkmanager "{avd.system_image}"')
    cmd = [str(avdmanager_exe(sdk)), "create", "avd", "-n", avd.name, "-k", avd.system_image, "-d", avd.sdk_device]
    print(f"[android] creating {avd.name} ({avd.sdk_device}, {avd.system_image})", flush=True)
    # avdmanager asks whether to make a custom hardware profile; answer no.
    return subprocess.run(cmd, input="no\n", text=True).returncode


def avd_name_of(adb: Path, serial: str) -> str | None:
    """The AVD behind an emulator serial, from its console; None if nothing answers."""
    try:
        out = subprocess.run([str(adb), "-s", serial, "emu", "avd", "name"], capture_output=True,
                             text=True, timeout=10).stdout
    except subprocess.TimeoutExpired:
        return None
    first = out.strip().splitlines()[0].strip() if out.strip() else ""
    return first or None


def adb_state(adb: Path, serial: str) -> str | None:
    """'device', 'offline', ... for the serial, or None when adb does not list it."""
    out = subprocess.run([str(adb), "devices"], capture_output=True, text=True).stdout
    return parse_state(out, serial)


def parse_state(devices_output: str, serial: str) -> str | None:
    for line in devices_output.splitlines()[1:]:
        fields = line.split()
        if len(fields) >= 2 and fields[0] == serial:
            return fields[1]
    return None


def booted(adb: Path, serial: str) -> bool:
    try:
        out = subprocess.run([str(adb), "-s", serial, "shell", "getprop", "sys.boot_completed"],
                             capture_output=True, text=True, timeout=10).stdout
    except subprocess.TimeoutExpired:
        return False
    return out.strip() == "1"


def start(sdk: Path, adb: Path, avd: Avd, log: Path, min_free_gb: float = MIN_FREE_RAM_GB
          ) -> subprocess.Popen | None:
    """Boot the AVD headless on its own port and wait for it.

    Returns the emulator process this call started, or None when the AVD was
    already up (then it is someone else's and the caller must leave it running).
    Exits if the port is taken by another AVD, memory is short, or boot times out.
    """
    state = adb_state(adb, avd.serial)
    if state is not None:
        running = avd_name_of(adb, avd.serial)
        if running != avd.name:
            sys.exit(f"[android] {avd.serial} is {running or 'something else'}, not {avd.name}; leaving it alone")
        print(f"[android] {avd.name} is already running on {avd.serial}; using it and leaving it up", flush=True)
        wait_booted(adb, avd, None)
        return None
    if avd.name not in existing(sdk):
        sys.exit(f"[android] no AVD {avd.name}; run: python tools\\android\\run.py avd create {avd.name}")
    free = free_ram_gb()
    if free < min_free_gb:
        sys.exit(f"[android] only {free:.1f} GB of RAM free, {min_free_gb:.0f} GB needed to start "
                 f"{avd.name}; not starting it. Try again when memory is free.")
    print(f"[android] {free:.1f} GB free; booting {avd.name} headless on {avd.serial}", flush=True)
    log.parent.mkdir(parents=True, exist_ok=True)
    out = open(log, "w", encoding="utf-8", errors="replace")
    proc = subprocess.Popen([str(emulator_exe(sdk)), "-avd", avd.name, "-port", str(avd.port), *EMULATOR_FLAGS],
                            stdout=out, stderr=subprocess.STDOUT)
    print(f"[android] emulator pid {proc.pid}; its log: {log}", flush=True)
    try:
        wait_booted(adb, avd, proc)
    except SystemExit:
        stop(adb, avd, proc)
        raise
    return proc


def wait_booted(adb: Path, avd: Avd, proc: subprocess.Popen | None, timeout: int = BOOT_TIMEOUT_S) -> None:
    deadline = time.time() + timeout
    while time.time() < deadline:
        if proc is not None and proc.poll() is not None:
            sys.exit(f"[android] the emulator exited ({proc.returncode}) before {avd.name} booted")
        if adb_state(adb, avd.serial) == "device" and booted(adb, avd.serial):
            running = avd_name_of(adb, avd.serial)
            if running != avd.name:
                sys.exit(f"[android] {avd.serial} came up as {running}, not {avd.name}")
            print(f"[android] {avd.name} booted on {avd.serial}", flush=True)
            return
        time.sleep(3)
    sys.exit(f"[android] {avd.name} did not boot within {timeout}s")


def stop(adb: Path, avd: Avd, proc: subprocess.Popen | None = None) -> bool:
    """Shut the AVD down through its own console, after checking it is the AVD.

    With proc (the emulator this run started), waits for that process to exit
    and, if the console did not stop it, ends that one process. Nothing is
    ever killed by name.
    """
    if adb_state(adb, avd.serial) is not None:
        running = avd_name_of(adb, avd.serial)
        if running == avd.name:
            subprocess.run([str(adb), "-s", avd.serial, "emu", "kill"], capture_output=True, timeout=30)
        elif running is not None:
            print(f"[android] {avd.serial} is {running}, not {avd.name}; not stopping it", flush=True)
            return False
    if proc is None:
        deadline = time.time() + STOP_TIMEOUT_S
        while time.time() < deadline and adb_state(adb, avd.serial) is not None:
            time.sleep(2)
        gone = adb_state(adb, avd.serial) is None
    else:
        try:
            proc.wait(STOP_TIMEOUT_S)
        except subprocess.TimeoutExpired:
            print(f"[android] emulator pid {proc.pid} still up after emu kill; terminating that process", flush=True)
            proc.terminate()
            try:
                proc.wait(30)
            except subprocess.TimeoutExpired:
                proc.kill()
                proc.wait(30)
        gone = proc.poll() is not None
    print(f"[android] {avd.name} " + ("stopped" if gone else "did NOT stop"), flush=True)
    return gone
