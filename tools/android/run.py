r"""Build, install, run and smoke-test GUO on an Android device.

    launchers\android\doctor.bat      what is missing, and the fix for each
    launchers\android\export.bat      a debug APK, headless, into build\android
    launchers\android\install.bat     adb install onto the attached device
    launchers\android\run.bat         start it and stream its logcat
    launchers\android\smoke.bat       export, install, run, wait for the login
                                      gump, pull a screenshot and a log

    python tools\android\run.py <doctor|templates|keystore|settings|preset|
                                 export|install|run|logcat|push|push-stage|smoke> [...]

What Godot needs to export a .NET project for Android, and where each part
comes from, is in ADR-0017 and README.md next to this file. In short: a
JDK 17, an Android SDK with platform-tools and build-tools, the mono export
templates for the pinned engine, and a debug keystore. Godot reads the SDK,
the JDK and the keystore from ITS editor settings file and nowhere else --
not from ANDROID_HOME, not from JAVA_HOME -- so `settings` writes the values
from config.bat into that file, and `export` does so before every export.

The export preset is rendered from export_presets.template.cfg into
godot\GUO\export_presets.cfg, which is gitignored: it carries the keystore
path and password from config.bat.

WHAT IT TOUCHES

  %APPDATA%\Godot\editor_settings-4.7.tres   three export/android/* keys,
                                             rewritten in place (a .bak is kept)
  %APPDATA%\Godot\export_templates\          the mono templates, if `templates`
  godot\GUO\export_presets.cfg               rendered; gitignored
  godot\GUO\GUO.sln                          generated if missing; gitignored
  build\android\                             the APK, logs, screenshots
"""

from __future__ import annotations

import argparse
import os
import re
import shutil
import subprocess
import sys
import time
import zipfile
from pathlib import Path

sys.path.insert(0, str(Path(__file__).resolve().parents[1]))

from guo.config import Config, godot_config_dir, godot_data_dir, load_config  # noqa: E402
from guo.datasources import WIZARD_EXIT, resolve_config  # noqa: E402

HERE = Path(__file__).resolve().parent
TEMPLATE = HERE / "export_presets.template.cfg"
PRESET_NAME = "Android"
ACTIVITY = "com.godot.game.GodotAppLauncher"  # 4.7: the exported launcher; GodotApp itself is not exported

# What logcat says when the login probe has drawn the login gump, and when it
# has not; see src/Bootstrap/LoginProbe.cs.
LOGIN_OK = "[GUO] login probe: ok"
LOGIN_FAIL = "[GUO] login probe: FAIL"

# The same for the second screen; see src/Bootstrap/DualProbe.cs and ADR-0009.
DUAL_OK = "[GUO] dual screen: ok"
DUAL_FAIL = "[GUO] dual screen: FAIL"
DUAL_NONE = "[GUO] dual screen: no second display"


# ---------------------------------------------------------------------------
# paths
# ---------------------------------------------------------------------------


class Paths:
    def __init__(self, cfg: Config):
        self.cfg = cfg
        self.project = cfg.godot_project
        self.out_dir = cfg.build / "android"
        self.apk = self.out_dir / "GUO-debug.apk"
        self.preset_file = self.project / "export_presets.cfg"
        self.solution = self.project / "GUO.sln"

        self.godot_config = godot_config_dir()
        major_minor = ".".join(cfg.godot_version.split("-")[0].split(".")[:2])
        self.editor_settings = self.godot_config / f"editor_settings-{major_minor}.tres"

        # "4.7.2-stable" + mono -> "4.7.2.stable.mono", Godot's folder name.
        self.templates_version = cfg.godot_version.replace("-", ".") + ".mono"
        self.templates_dir = godot_data_dir() / "export_templates" / self.templates_version
        self.templates_tpz = (
            cfg.tools / "godot" / "templates" / f"Godot_v{cfg.godot_version}_mono_export_templates.tpz"
        )

        self.sdk = cfg.android_sdk
        self.adb = self._first_existing(
            [self.sdk / "platform-tools" / "adb.exe", self.sdk / "platform-tools" / "adb"]
        ) or shutil.which("adb")
        self.jdk = cfg.android_jdk
        self.keystore = cfg.android_keystore

    @staticmethod
    def _first_existing(candidates: list[Path]) -> Path | None:
        for c in candidates:
            if c.exists():
                return c
        return None

    def godot_console(self) -> Path:
        env = os.environ.get("GODOT_CONSOLE")
        if env and Path(env).exists():
            return Path(env)
        return self.cfg.godot_console_exe

    def java(self, tool: str) -> Path | None:
        if self.jdk and (self.jdk / "bin" / f"{tool}.exe").exists():
            return self.jdk / "bin" / f"{tool}.exe"
        found = shutil.which(tool)
        return Path(found) if found else None


# ---------------------------------------------------------------------------
# helpers
# ---------------------------------------------------------------------------


def say(msg: str) -> None:
    print(f"[android] {msg}", flush=True)


def run(cmd: list[str], **kw) -> subprocess.CompletedProcess:
    say("$ " + " ".join(f'"{c}"' if " " in str(c) else str(c) for c in cmd))
    return subprocess.run([str(c) for c in cmd], **kw)


def adb_cmd(p: Paths) -> list[str]:
    if not p.adb:
        sys.exit("[android] adb not found: install Android SDK platform-tools (see doctor)")
    cmd = [str(p.adb)]
    serial = p.cfg.android_device
    if not serial:
        # No serial configured: adb refuses to guess when more than one
        # device is attached, but an "unauthorized" one is no candidate,
        # so if exactly one is ready that is the device.
        ready = [s for s, state in adb_devices(p) if state == "device"]
        if len(ready) == 1:
            serial = ready[0]
        elif len(ready) > 1:
            # Never pick one of several: a run meant for one handheld must
            # not install onto, or tap, another agent's device.
            sys.exit(f"[android] {len(ready)} devices are ready; choose one with UO_ANDROID_DEVICE "
                     "(environment or config.local.bat). `adb devices -l` lists them by model.")
    if serial:
        cmd += ["-s", serial]
    return cmd


def java_major(java: Path) -> int | None:
    try:
        out = subprocess.run([str(java), "-version"], capture_output=True, text=True).stderr
    except OSError:
        return None
    m = re.search(r'version "(\d+)(?:\.(\d+))?', out)
    if not m:
        return None
    major = int(m.group(1))
    # "1.8.0_302" is Java 8.
    return int(m.group(2)) if major == 1 and m.group(2) else major


# ---------------------------------------------------------------------------
# doctor
# ---------------------------------------------------------------------------


class Doctor:
    def __init__(self, p: Paths):
        self.p = p
        self.missing = 0

    def check(self, what: str, ok: bool, detail: str, fix: str | None = None) -> bool:
        mark = "ok  " if ok else "MISS"
        print(f"  {mark} {what:<28} {detail}")
        if not ok:
            self.missing += 1
            if fix:
                print(f"       fix: {fix}")
        return ok

    def run(self, publish_check: bool) -> int:
        p, cfg = self.p, self.p.cfg
        print("[android] doctor -- what a Godot 4.7 .NET Android export needs on this machine\n")

        # --- the build machine ---------------------------------------------
        dotnet = shutil.which("dotnet")
        self.check("dotnet SDK", dotnet is not None, dotnet or "not on PATH",
                   "install the .NET 8 SDK (or newer) and put dotnet on PATH")

        console = p.godot_console()
        self.check("Godot console (mono)", console.exists(), str(console),
                   r"launchers\dev\fetch_godot.bat, or set GODOT_EXE in config.bat")

        have_templates = (p.templates_dir / "android_debug.apk").exists() and (
            p.templates_dir / "android_source.zip"
        ).exists()
        self.check(
            "Android export templates", have_templates, str(p.templates_dir),
            f"python tools\\android\\run.py templates   (unpacks {p.templates_tpz.name})"
            if p.templates_tpz.exists()
            else f"download Godot_v{cfg.godot_version}_mono_export_templates.tpz into tools\\godot\\templates, "
                 "then: python tools\\android\\run.py templates",
        )

        self.check("solution file (GUO.sln)", p.solution.exists(), str(p.solution),
                   "generated on the first export, or: godot-console --headless --path godot\\GUO --editor --quit")

        # --- Java ----------------------------------------------------------
        java = p.java("java")
        major = java_major(java) if java else None
        self.check(
            "JDK 17", java is not None and major == 17,
            f"{java} (Java {major})" if java else "no java found",
            "install a JDK 17 (Eclipse Temurin) and set UO_ANDROID_JDK in config.bat to its folder",
        )
        self.check("keytool", p.java("keytool") is not None, str(p.java("keytool") or "not found"),
                   "comes with the JDK above")

        # --- the Android SDK -----------------------------------------------
        sdk = p.sdk
        self.check("Android SDK root", sdk.is_dir(), str(sdk),
                   "install Android Studio, or the command-line tools, and set UO_ANDROID_SDK in config.bat")
        self.check("  platform-tools (adb)", p.adb is not None, str(p.adb or "not found"),
                   "sdkmanager \"platform-tools\"")
        build_tools = sorted((sdk / "build-tools").glob("*")) if (sdk / "build-tools").is_dir() else []
        self.check("  build-tools", bool(build_tools), ", ".join(b.name for b in build_tools) or "none",
                   "sdkmanager \"build-tools;35.0.1\"")
        platforms = sorted((sdk / "platforms").glob("android-*")) if (sdk / "platforms").is_dir() else []
        self.check("  platforms", bool(platforms), ", ".join(x.name for x in platforms) or "none",
                   "sdkmanager \"platforms;android-35\"")
        cmdline = (sdk / "cmdline-tools").is_dir()
        self.check("  cmdline-tools", cmdline, str(sdk / "cmdline-tools"),
                   "sdkmanager \"cmdline-tools;latest\"  (only needed to run sdkmanager itself)")
        ndks = sorted((sdk / "ndk").glob("*")) if (sdk / "ndk").is_dir() else []
        print(f"  info NDK                          {', '.join(n.name for n in ndks) or 'none'} "
              "(only a Gradle build needs one; this preset does not)")

        # --- signing -------------------------------------------------------
        self.check("debug keystore", p.keystore.exists(), str(p.keystore),
                   "python tools\\android\\run.py keystore")

        # --- Godot's editor settings ---------------------------------------
        settings = read_editor_settings(p.editor_settings)
        wanted = wanted_editor_settings(p)
        for key, value in wanted.items():
            self.check(
                f"editor setting {key.split('/')[-1]}", same_setting(settings.get(key), value),
                settings.get(key, "(unset)"),
                "python tools\\android\\run.py settings   (writes the values from config.bat)",
            )

        # --- a device ------------------------------------------------------
        if p.adb:
            devices = adb_devices(p)
            self.check(
                "adb device", any(state == "device" for _, state in devices),
                ", ".join(f"{s} ({st})" for s, st in devices) or "none attached",
                "plug a device in with USB debugging on and accept the prompt on its screen "
                "(\"unauthorized\" means the prompt is waiting)",
            )

        # --- a second display (ADR-0009) ------------------------------------
        if p.adb and any(state == "device" for _, state in adb_devices(p)):
            displays = android_displays(p)
            second = next((d for d in displays if d.presentation), None)
            print(f"  info displays                     {len(displays)} on the device: "
                  + "; ".join(str(d) for d in displays))
            print(f"  info second display               "
                  + (str(second) if second else "none (the client leaves the second screen alone)"))
            if p.cfg.android_second_display:
                print(f"  info UO_ANDROID_SECOND_DISPLAY    {p.cfg.android_second_display} (overrides the id above for screencap)")

        # --- the C# half, for real ------------------------------------------
        if publish_check:
            print("\n[android] doctor: dotnet publish for android-arm64 (the .NET half of an export) ...")
            ok = dotnet_publish_check(p)
            self.check("dotnet publish android-arm64", ok, "see above",
                       "read the errors above; the csproj guards are in GUO.csproj (ADR-0017)")
        else:
            print("  info dotnet publish check         skipped; run with --publish to build the C# for android-arm64")

        print()
        if self.missing:
            say(f"{self.missing} thing(s) missing; fix them in the order listed")
            return 1
        say("everything an export needs is here")
        return 0


class AndroidDisplay:
    """One entry of `dumpsys display`: what the client's DisplayManager check sees."""

    def __init__(self, display_id: int, name: str, width: int, height: int, rotation: int,
                 presentation: bool, surface_id: str, state: str):
        self.display_id = display_id
        self.name = name
        self.width = width
        self.height = height
        self.rotation = rotation
        self.presentation = presentation
        self.surface_id = surface_id  # what `screencap -d` wants
        self.state = state

    def __str__(self) -> str:
        flags = " presentation" if self.presentation else ""
        return (f"display {self.display_id} \"{self.name}\" {self.width}x{self.height} rotation {self.rotation}"
                f"{flags}, surfaceflinger {self.surface_id or '?'}, {self.state}")


def android_displays(p: Paths) -> list[AndroidDisplay]:
    """Parse `dumpsys display` into the logical displays and their SurfaceFlinger ids.

    The client asks DisplayManager for DISPLAY_CATEGORY_PRESENTATION; here the
    same fact is the FLAG_PRESENTATION on the display's info, and its uniqueId
    "local:<id>" is the id `screencap -d` takes (the same one
    `dumpsys SurfaceFlinger --display-id` lists)."""
    if not p.adb:
        return []
    text = subprocess.run(adb_cmd(p) + ["shell", "dumpsys", "display"],
                          capture_output=True, text=True, errors="replace").stdout
    found: dict[int, AndroidDisplay] = {}
    current: int | None = None
    for line in text.splitlines():
        m = re.match(r"\s*mDisplayId=(\d+)", line)
        if m:
            current = int(m.group(1))
            continue
        # The override info is the rotated one, and it comes second; the
        # last DisplayInfo seen for an id wins, which is what the app gets.
        m = re.match(r'\s*m(?:Base|Override)DisplayInfo=DisplayInfo\{"([^"]*)", displayId (\d+)', line)
        if not m or current is None:
            continue
        size = re.search(r"real (\d+) x (\d+)", line)
        rot = re.search(r"rotation (\d+)", line)
        uid = re.search(r'uniqueId "local:(\d+)"', line)
        state = re.search(r"state (\w+)", line)
        found[current] = AndroidDisplay(
            current, m.group(1),
            int(size.group(1)) if size else 0, int(size.group(2)) if size else 0,
            int(rot.group(1)) if rot else 0,
            "FLAG_PRESENTATION" in line,
            uid.group(1) if uid else "",
            state.group(1) if state else "?",
        )
    return [found[k] for k in sorted(found)]


def second_display(p: Paths) -> AndroidDisplay | None:
    """The display the client will present on, or None."""
    for d in android_displays(p):
        if d.presentation:
            return d
    return None


def second_display_surface_id(p: Paths) -> str:
    """config.bat's override, else what dumpsys says."""
    if p.cfg.android_second_display:
        return p.cfg.android_second_display
    d = second_display(p)
    return d.surface_id if d else ""


def adb_devices(p: Paths) -> list[tuple[str, str]]:
    out = subprocess.run([str(p.adb), "devices"], capture_output=True, text=True).stdout
    devices = []
    for line in out.splitlines()[1:]:
        parts = line.split()
        if len(parts) >= 2:
            devices.append((parts[0], parts[1]))
    return devices


def dotnet_publish_check(p: Paths) -> bool:
    out = p.out_dir / "publish_check"
    cmd = [
        "dotnet", "publish", str(p.project / "GUO.csproj"),
        "-c", "ExportDebug", "-r", "android-arm64", "--self-contained", "true",
        "-p:GodotTargetPlatform=android", "-o", str(out), "-nologo", "-v:q",
    ]
    if os.environ.get("UpstreamDir"):
        cmd.append(f"-p:UpstreamDir={os.environ['UpstreamDir']}")
    result = run(cmd, capture_output=True, text=True)
    for line in (result.stdout + result.stderr).splitlines():
        if "error" in line.lower():
            print("       " + line.strip())
    if result.returncode == 0:
        say(f"published {sum(1 for _ in out.iterdir())} files to {out}; "
            f"plugin_host present: {(out / 'plugin_host').exists()} (must be False)")
    return result.returncode == 0 and not (out / "plugin_host").exists()


# ---------------------------------------------------------------------------
# editor settings
# ---------------------------------------------------------------------------


def wanted_editor_settings(p: Paths) -> dict[str, str]:
    # Forward slashes: what Godot writes into this file itself.
    wanted = {
        "export/android/android_sdk_path": p.sdk.as_posix(),
        "export/android/debug_keystore": p.keystore.as_posix(),
        "export/android/debug_keystore_user": p.cfg.android_keystore_user,
        "export/android/debug_keystore_pass": p.cfg.android_keystore_password,
    }
    if p.jdk:
        wanted["export/android/java_sdk_path"] = p.jdk.as_posix()
    return wanted


def same_setting(current: str | None, wanted: str) -> bool:
    """Godot stores paths with forward slashes; config.bat writes backslashes."""
    if current is None:
        return False
    if "/" in wanted or "\\" in wanted or "/" in current or "\\" in current:
        return os.path.normcase(os.path.normpath(current)) == os.path.normcase(os.path.normpath(wanted))
    return current == wanted


def read_editor_settings(path: Path) -> dict[str, str]:
    values: dict[str, str] = {}
    if not path.exists():
        return values
    for line in path.read_text(encoding="utf-8", errors="replace").splitlines():
        m = re.match(r'^\s*([\w/]+)\s*=\s*"(.*)"\s*$', line)
        if m:
            values[m.group(1)] = m.group(2).replace("\\\\", "\\")
    return values


def write_editor_settings(p: Paths) -> None:
    """Set the export/android/* keys Godot reads, leaving everything else alone."""
    path = p.editor_settings
    if not path.exists():
        # Godot writes this file the first time the editor runs. Without it
        # there is nothing to patch, so make the minimal one Godot accepts.
        path.parent.mkdir(parents=True, exist_ok=True)
        path.write_text('[gd_resource type="EditorSettings" format=3]\n\n[resource]\n', encoding="utf-8")
        say(f"created {path}")
    else:
        shutil.copy2(path, path.with_suffix(".tres.bak"))

    wanted = wanted_editor_settings(p)
    lines = path.read_text(encoding="utf-8", errors="replace").splitlines()
    seen: set[str] = set()
    out: list[str] = []
    for line in lines:
        m = re.match(r"^\s*([\w/]+)\s*=", line)
        key = m.group(1) if m else None
        if key in wanted:
            out.append(f'{key} = "{tres_escape(wanted[key])}"')
            seen.add(key)
        else:
            out.append(line)
    if "[resource]" not in out:
        out.append("[resource]")
    for key, value in wanted.items():
        if key not in seen:
            out.append(f'{key} = "{tres_escape(value)}"')
    path.write_text("\n".join(out) + "\n", encoding="utf-8")
    for key, value in wanted.items():
        say(f"{path.name}: {key} = {value}")


def tres_escape(value: str) -> str:
    return value.replace("\\", "\\\\").replace('"', '\\"')


# ---------------------------------------------------------------------------
# templates, keystore, preset, solution
# ---------------------------------------------------------------------------


def install_templates(p: Paths) -> int:
    if not p.templates_tpz.exists():
        sys.exit(f"[android] no templates archive at {p.templates_tpz}\n"
                 f"  download Godot_v{p.cfg.godot_version}_mono_export_templates.tpz from the Godot "
                 "release and put it there")
    p.templates_dir.mkdir(parents=True, exist_ok=True)
    count = 0
    with zipfile.ZipFile(p.templates_tpz) as z:
        for member in z.infolist():
            # The archive is one folder, "templates/", holding the files.
            name = member.filename.split("/", 1)[1] if "/" in member.filename else member.filename
            if member.is_dir() or not name:
                continue
            target = p.templates_dir / name
            target.parent.mkdir(parents=True, exist_ok=True)
            with z.open(member) as src, open(target, "wb") as dst:
                shutil.copyfileobj(src, dst)
            count += 1
    say(f"unpacked {count} template files into {p.templates_dir}")
    return 0


def make_keystore(p: Paths) -> int:
    keytool = p.java("keytool")
    if not keytool:
        sys.exit("[android] keytool not found; install a JDK 17 and set UO_ANDROID_JDK")
    if p.keystore.exists():
        say(f"keystore already there: {p.keystore}")
        return 0
    p.keystore.parent.mkdir(parents=True, exist_ok=True)
    # The standard Android debug key. Not a secret and never used for a
    # release; every debug APK on every developer machine is signed this way.
    result = run([
        keytool, "-genkeypair", "-v", "-keystore", p.keystore,
        "-alias", p.cfg.android_keystore_user, "-keyalg", "RSA", "-keysize", "2048",
        "-validity", "10000", "-storepass", p.cfg.android_keystore_password,
        "-keypass", p.cfg.android_keystore_password,
        "-dname", "CN=Android Debug,O=Android,C=US",
    ])
    return result.returncode


ABIS = ("arm64", "x86_64", "both")


def abi_flags(abi: str) -> dict[str, str]:
    """The preset's architecture switches: arm64 for handhelds and phones,
    x86_64 for the Android emulator on a PC, or both in one APK."""
    return {
        "ABI_ARM64": "true" if abi in ("arm64", "both") else "false",
        "ABI_X86_64": "true" if abi in ("x86_64", "both") else "false",
    }


def render_preset(p: Paths, extra_args: str, export_path: Path, abi: str = "arm64") -> Path:
    text = TEMPLATE.read_text(encoding="utf-8")
    values = {
        "PACKAGE": p.cfg.android_package,
        "KEYSTORE": str(p.keystore).replace("\\", "/"),
        "KEYSTORE_USER": p.cfg.android_keystore_user,
        "KEYSTORE_PASSWORD": p.cfg.android_keystore_password,
        "EXTRA_ARGS": extra_args.replace('"', '\\"'),
        "EXPORT_PATH": str(export_path).replace("\\", "/"),
        **abi_flags(abi),
    }
    for key, value in values.items():
        text = text.replace("{{" + key + "}}", value)
    leftover = re.findall(r"{{\w+}}", text)
    if leftover:
        sys.exit(f"[android] template placeholders without a value: {leftover}")
    p.preset_file.write_text(text, encoding="utf-8")
    say(f"rendered {p.preset_file} (extra args: {extra_args!r})")
    return p.preset_file


def ensure_solution(p: Paths) -> None:
    if p.solution.exists():
        return
    say("no GUO.sln; asking the editor to generate it (headless, once)")
    run([p.godot_console(), "--headless", "--path", p.project, "--editor", "--quit"],
        stdout=subprocess.DEVNULL, stderr=subprocess.DEVNULL)
    if not p.solution.exists():
        # The editor can be slow to write it; a plain one works just as well.
        # The .NET 10 SDK writes GUO.slnx unless told the classic format, and
        # the export needs GUO.sln. SDKs before 9.0.200 have no --format and
        # write .sln anyway, so they get the plain call.
        new_sln = ["dotnet", "new", "sln", "-n", "GUO", "-o", p.project]
        if subprocess.run([*new_sln, "--format", "sln"], stdout=subprocess.DEVNULL,
                          stderr=subprocess.DEVNULL).returncode != 0:
            run(new_sln, stdout=subprocess.DEVNULL)
        run(["dotnet", "sln", p.solution, "add", p.project / "GUO.csproj"], stdout=subprocess.DEVNULL)
    if not p.solution.exists():
        sys.exit("[android] could not produce GUO.sln; the .NET export needs one")


def device_args(p: Paths, extra: str, sound: bool = False, client_data: bool = True) -> str:
    """The client's command line on the device: where its data is, then whatever the caller adds.

    client_data=False leaves the data folder out, as a player's install has it:
    the client finds its data itself or shows the first-run screen (G2a).
    """
    # After "--": Main.cs reads OS.GetCmdlineUserArgs(), which is only what
    # follows the separator; without it the engine kept the flags and the
    # client saw none of them (it died with "No UO client data directory").
    base = "-- --play" + (f" --client-data {p.cfg.android_client_data}" if client_data else "")
    # Silent unless asked: every device run is either a tool driving the
    # handheld over adb or a person trying a build, and neither wants the
    # Britain theme over the speaker. --sound exports an audible build.
    if not sound:
        base += " --silent"
    # The configured account, unless the caller names one itself.
    if p.cfg.android_account and "--account" not in extra:
        base += f" --account {p.cfg.android_account}"
    # A tool run (a probe, a perf walk, the smoke) in the Classic look: the
    # device keeps a player's look in files/postfx/state.json across
    # reinstalls, and a saved Ink Outline timed its two passes into every
    # Thor number (2026-09-28). A build for a person keeps the saved look.
    if is_tool_run(extra) and "--postfx" not in extra:
        base += " --postfx off"
    return f"{base} {extra}".strip()


def is_tool_run(extra: str) -> bool:
    """A build that runs a probe (--perf-probe, --dual-probe, --login-probe-stay, ...), not one for play."""
    return re.search(r"--[\w-]*probe\b", extra) is not None


# ---------------------------------------------------------------------------
# export / install / run / smoke
# ---------------------------------------------------------------------------


def export(p: Paths, extra_args: str, apk: Path, sound: bool = False, client_data: bool = True,
           abi: str = "arm64") -> int:
    console = p.godot_console()
    if not console.exists():
        sys.exit(f"[android] Godot console not found at {console}; run doctor")
    p.out_dir.mkdir(parents=True, exist_ok=True)
    apk = apk_path(apk)
    apk.parent.mkdir(parents=True, exist_ok=True)
    write_editor_settings(p)
    ensure_solution(p)
    render_preset(p, device_args(p, extra_args, sound, client_data), apk, abi)
    if apk.exists():
        apk.unlink()

    log = p.out_dir / "export.log"
    cmd = [console, "--headless", "--path", p.project, "--export-debug", PRESET_NAME, apk]
    say("$ " + " ".join(f'"{c}"' if " " in str(c) else str(c) for c in cmd))
    with open(log, "w", encoding="utf-8") as f:
        proc = subprocess.Popen([str(c) for c in cmd], stdout=f, stderr=subprocess.STDOUT, text=True)
        returncode, stopped = wait_for_export(proc, log)
    text = log.read_text(encoding="utf-8", errors="replace")
    for line in text.splitlines():
        if re.search(r"error|failed|not found|required", line, re.IGNORECASE):
            print("  " + line.strip())
    if stopped:
        say(stopped)
        # Stopped after it logged DONE: the APK is judged below like any other.
        if export_done(text):
            returncode = 0
    if returncode != 0 or not apk.exists():
        say(f"export FAILED: {export_failure(returncode, text, apk)}; full log: {log}")
        return 1
    problem = export_problem(text, apk)
    if problem:
        say(f"export FAILED: {problem}; full log: {log}")
        return 1
    say(f"exported {apk} ({apk.stat().st_size // 1024 // 1024} MB); log: {log}")
    return 0


EXPORT_GRACE_S = 60      # after Godot logs the export DONE
EXPORT_LIMIT_S = 30 * 60  # the whole export, a cold C# publish included


def export_done(log_text: str) -> bool:
    """Godot's own "[ DONE ] export" line, with its console colours taken out."""
    plain = re.sub(r"\x1b\[[0-9;]*m", "", log_text)
    return re.search(r"\[ DONE \]\s*export", plain) is not None


def kill_tree(proc: subprocess.Popen) -> None:
    if os.name == "nt":
        subprocess.run(["taskkill", "/PID", str(proc.pid), "/T", "/F"],
                       stdout=subprocess.DEVNULL, stderr=subprocess.DEVNULL)
    else:
        proc.kill()
    proc.wait()


def wait_for_export(proc: subprocess.Popen, log: Path, grace: float = EXPORT_GRACE_S,
                    limit: float = EXPORT_LIMIT_S, poll: float = 1.0) -> tuple[int | None, str | None]:
    """Wait for Godot's export to exit: (exit code, None), or (None, why) when it was stopped.

    Godot 4.7.2 can finish an Android export, log DONE, and then never exit
    (after "EditorSettings not instantiated yet ... shutdown_adb_on_exit");
    once cost eleven minutes of a device slot. So: a grace period after DONE,
    and an overall limit.
    """
    start = time.monotonic()
    done_at = None
    while True:
        code = proc.poll()
        if code is not None:
            return code, None
        now = time.monotonic()
        if done_at is None and export_done(log.read_text(encoding="utf-8", errors="replace")):
            done_at = now
        if done_at is not None and now - done_at > grace:
            kill_tree(proc)
            return None, f"Godot logged the export DONE but had not exited {grace:.0f} s later; stopped it"
        if now - start > limit:
            kill_tree(proc)
            return None, f"Godot had not exited after {limit / 60:.0f} min; stopped it"
        time.sleep(poll)


def apk_path(apk: Path) -> Path:
    """The APK path made absolute from where run.py was started.

    Godot runs with --path at the project, so it would read a relative path
    from there: `--out build/android/x.apk` then meant godot/GUO/build/android,
    and failed as "Target folder does not exist" without naming it.
    """
    return Path(apk).resolve()


def export_failure(returncode: int, log_text: str, apk: Path) -> str:
    """Why Godot wrote no APK, naming the path it was asked for."""
    if "Target folder does not exist" in log_text:
        return f"Godot could not write to the folder of {apk}"
    return f"exit {returncode}, no APK at {apk}"


# Godot exits 0 and writes an APK even when the C# did not build: the APK
# then holds no GUO.dll and the device boots to nothing (C9's first Odin run).
BUILD_FAILED = "Failed to build project"


def export_problem(log_text: str, apk: Path) -> str | None:
    """Why an APK Godot says it exported is not a build, or None when it is."""
    if BUILD_FAILED in log_text:
        return f"Godot logged '{BUILD_FAILED}' (the C# did not compile; run dotnet build to see why)"
    try:
        with zipfile.ZipFile(apk) as z:
            if not any(n.startswith("assets/.godot/mono/") and n.endswith("/GUO.dll") for n in z.namelist()):
                return "the APK holds no GUO.dll (no C# assemblies were packed)"
    except zipfile.BadZipFile:
        return "the APK is not a valid zip"
    return None


def install(p: Paths, apk: Path) -> int:
    if not apk.exists():
        sys.exit(f"[android] no APK at {apk}; run export first")
    return run(adb_cmd(p) + ["install", "-r", "-d", str(apk)]).returncode


def wake_device(p: Paths) -> None:
    """Screen on, lock screen away, notification shade closed.

    An app started on a sleeping device renders nothing and logs nothing
    (Godot pauses), and the smoke would wait its whole timeout on a black
    screencap. `dismiss-keyguard` only works without a PIN; with one, unlock
    the device by hand first.
    """
    subprocess.run(adb_cmd(p) + ["shell", "input keyevent KEYCODE_WAKEUP; wm dismiss-keyguard; cmd statusbar collapse"],
                   stdout=subprocess.DEVNULL, stderr=subprocess.DEVNULL)


def start_app(p: Paths) -> int:
    wake_device(p)
    return run(adb_cmd(p) + ["shell", "am", "start", "-n", f"{p.cfg.android_package}/{ACTIVITY}"]).returncode


def stop_app(p: Paths) -> None:
    subprocess.run(adb_cmd(p) + ["shell", "am", "force-stop", p.cfg.android_package],
                   stdout=subprocess.DEVNULL, stderr=subprocess.DEVNULL)


LOGCAT_FILTER = ["-s", "godot:*", "GodotSharp:*", "AndroidRuntime:E", "DEBUG:*", "libc:F", "mono:*"]


def stream_logcat(p: Paths) -> int:
    say("streaming logcat (Ctrl+C to stop)")
    try:
        return subprocess.call(adb_cmd(p) + ["logcat", "-v", "time"] + LOGCAT_FILTER)
    except KeyboardInterrupt:
        return 0


def run_app(p: Paths) -> int:
    subprocess.run(adb_cmd(p) + ["logcat", "-c"])
    if start_app(p) != 0:
        return 1
    return stream_logcat(p)


def push_data(p: Paths) -> int:
    """Copy the top-level files of the UO install to the device.

    Only the files: the client reads its archives from the install's root,
    and the launcher's subfolders (Data, Music, GDF, Overrides, logs, notes,
    patcher) are not needed -- and adb cannot create them anyway, because
    scoped storage refuses `mkdir` below the app's files folder from outside
    the app (`secure_mkdirs failed`). `--sync` skips what is already there
    and unchanged, so reruns are cheap. Non-zero on the first adb failure.
    """
    res = resolve_config(p.cfg)
    if not res.ok:
        # ADR-0021: nothing to push is not an error; the app opens the
        # first-run wizard on the device.
        say(res.message())
        for note in res.notes:
            say(f"  passed over: {note}")
        return WIZARD_EXIT
    src = res.client_data
    dst = p.cfg.android_client_data
    files = sorted(f for f in src.iterdir() if f.is_file())
    total = sum(f.stat().st_size for f in files)
    say(f"pushing {len(files)} files, {total / 2**30:.1f} GB: {src} -> {dst} (subfolders skipped)")
    if subprocess.run(adb_cmd(p) + ["shell", "mkdir", "-p", dst]).returncode != 0:
        say("FAILED: could not create the folder on the device")
        return 1
    # In batches: one adb push takes many files, but a Windows command line
    # has a length limit that 340 long paths would exceed.
    batch = 40
    for i in range(0, len(files), batch):
        chunk = files[i:i + batch]
        say(f"  {i + 1}-{i + len(chunk)} of {len(files)}")
        result = subprocess.run(adb_cmd(p) + ["push", "--sync"] + [str(f) for f in chunk] + [dst + "/"])
        if result.returncode != 0:
            say(f"FAILED: adb push exited {result.returncode} on batch starting at {chunk[0].name}")
            return result.returncode or 1
    say(f"pushed; {len(files)} files on the device under {dst}")
    return 0


def push_stage(p: Paths, stage: Path, reverse: int | None) -> int:
    """Put a staged data set (ADR-0022) on the device next to the pushed install.

    Each file the stage's files_override.txt names is pushed into the device's
    client data folder as stage-<pack>-<name> -- flat, because adb cannot make
    a subfolder there (see push_data) -- with an override file,
    stage-<pack>.override.txt, mapping the names to those paths. The device's
    copy of the install is not changed. A build made with
    --args "--files-override <that file>" reads the stage; the command to make
    it is printed. --reverse PORT forwards the device's 127.0.0.1:PORT to this
    PC's, so the device reaches the private shard, which listens on loopback only.
    """
    stage = stage.resolve()
    listing = stage / "files_override.txt"
    if not listing.exists():
        sys.exit(f"[android] {stage} is not a staged data set (no files_override.txt)")
    pack = stage.name
    dst = p.cfg.android_client_data
    lines, files = [], []
    for line in listing.read_text(encoding="utf-8").splitlines():
        if "=" not in line or line.lstrip().startswith(("#", ";")):
            continue
        name, local = (x.strip() for x in line.split("=", 1))
        remote = f"{dst}/stage-{pack}-{Path(local).name}"
        files.append((Path(local), remote))
        lines.append(f"{name}={remote}")
    override = p.out_dir / f"stage-{pack}.override.txt"
    override.parent.mkdir(parents=True, exist_ok=True)
    override.write_bytes(("".join(line + chr(10) for line in lines)).encode("utf-8"))
    files.append((override, f"{dst}/{override.name}"))
    total = sum(f.stat().st_size for f, _ in files)
    say(f"pushing stage {pack}: {len(files)} files, {total / 2**20:.0f} MB -> {dst}")
    for local, remote in files:
        if subprocess.run(adb_cmd(p) + ["push", "--sync", str(local), remote]).returncode != 0:
            say(f"FAILED: adb push {local.name}")
            return 1
    if reverse:
        if subprocess.run(adb_cmd(p) + ["reverse", f"tcp:{reverse}", f"tcp:{reverse}"]).returncode != 0:
            say(f"FAILED: adb reverse tcp:{reverse}")
            return 1
        say(f"device 127.0.0.1:{reverse} -> this PC's 127.0.0.1:{reverse}")
    say("pushed. Build with the stage:")
    say(f'  python tools/android/run.py export --args "--files-override {dst}/{override.name}'
        + (f' --host 127.0.0.1 --port {reverse}"' if reverse else '"'))
    return 0


def smoke(p: Paths, timeout: int, skip_export: bool, sound: bool = False) -> int:
    apk = p.out_dir / "GUO-smoke.apk"
    if not skip_export:
        # The smoke build says on the log when the login gump is drawn, and
        # stays up so a screenshot can be taken of it.
        if export(p, "--login-probe-stay", apk, sound) != 0:
            return 1
    if install(p, apk) != 0:
        say("install FAILED")
        return 1

    adb = adb_cmd(p)
    subprocess.run(adb + ["logcat", "-c"])
    stop_app(p)
    if start_app(p) != 0:
        return 1

    say(f"waiting up to {timeout}s for '{LOGIN_OK}' on logcat")
    deadline = time.time() + timeout
    verdict = None
    log_text = ""
    while time.time() < deadline:
        time.sleep(3)
        log_text = subprocess.run(adb + ["logcat", "-d", "-v", "time"] + LOGCAT_FILTER,
                                  capture_output=True, text=True, errors="replace").stdout
        if LOGIN_OK in log_text:
            verdict = True
            break
        if LOGIN_FAIL in log_text or "FATAL EXCEPTION" in log_text or "[GUO] FATAL" in log_text:
            verdict = False
            break
        if f"Process {p.cfg.android_package}" in log_text and "has died" in log_text:
            verdict = False
            break

    p.out_dir.mkdir(parents=True, exist_ok=True)
    log_file = p.out_dir / "smoke_logcat.txt"
    log_file.write_text(log_text, encoding="utf-8")

    shot = p.out_dir / "smoke.png"
    with open(shot, "wb") as f:
        subprocess.run(adb + ["exec-out", "screencap", "-p"], stdout=f)
    stop_app(p)

    for line in log_text.splitlines():
        if "[GUO]" in line:
            print("  " + line.strip())

    if verdict:
        say(f"OK login gump rendered on the device; screenshot {shot}, log {log_file}")
        return 0
    say("FAILED: " + ("the client reported a failure or died" if verdict is False
                      else f"no '{LOGIN_OK}' within {timeout}s") + f"; log {log_file}, screenshot {shot}")
    return 1


def screencap(p: Paths, path: Path, surface_id: str = "") -> bool:
    """Photograph a display into a PNG; the main one unless a SurfaceFlinger id is given."""
    cmd = adb_cmd(p) + ["exec-out", "screencap", "-p"]
    if surface_id:
        cmd += ["-d", surface_id]
    with open(path, "wb") as f:
        result = subprocess.run(cmd, stdout=f, stderr=subprocess.DEVNULL)
    return result.returncode == 0 and path.stat().st_size > 0


def wake_device(p: Paths) -> None:
    """The Thor sleeps between runs; a sleeping panel photographs black."""
    adb = adb_cmd(p)
    subprocess.run(adb + ["shell", "input", "keyevent", "KEYCODE_WAKEUP"], stdout=subprocess.DEVNULL)
    subprocess.run(adb + ["shell", "wm", "dismiss-keyguard"], stdout=subprocess.DEVNULL)


def dual_probe(p: Paths, timeout: int, skip_export: bool, extra_args: str, stay: bool) -> int:
    """Export with --dual-probe, run it, wait for the verdict, photograph both displays."""
    apk = p.out_dir / "GUO-dual.apk"
    if not skip_export:
        if export(p, f"--dual-probe {extra_args}".strip(), apk) != 0:
            return 1
    if install(p, apk) != 0:
        say("install FAILED")
        return 1

    second = second_display(p)
    surface = second_display_surface_id(p)
    say("second display: " + (str(second) if second else "none reported by dumpsys display"))

    adb = adb_cmd(p)
    wake_device(p)
    subprocess.run(adb + ["logcat", "-c"])
    stop_app(p)
    if start_app(p) != 0:
        return 1

    say(f"waiting up to {timeout}s for '{DUAL_OK}' on logcat")
    deadline = time.time() + timeout
    verdict = None
    log_text = ""
    while time.time() < deadline:
        time.sleep(3)
        log_text = subprocess.run(adb + ["logcat", "-d", "-v", "time"] + LOGCAT_FILTER,
                                  capture_output=True, text=True, errors="replace").stdout
        if DUAL_OK in log_text or DUAL_NONE in log_text:
            verdict = True
            break
        if DUAL_FAIL in log_text or "FATAL EXCEPTION" in log_text or "[GUO] FATAL" in log_text:
            verdict = False
            break
        if f"Process {p.cfg.android_package}" in log_text and "has died" in log_text:
            verdict = False
            break

    p.out_dir.mkdir(parents=True, exist_ok=True)
    log_file = p.out_dir / "dual_logcat.txt"
    log_file.write_text(log_text, encoding="utf-8")

    # Both panels, while the app is still up. The client also saves what it
    # pushed to the second screen (guo_second.png in its screenshots folder);
    # this is the panel itself, which is the only proof the pixels arrived.
    main_shot = p.out_dir / "dual_main.png"
    second_shot = p.out_dir / "dual_second.png"
    shots = [f"main {main_shot}" if screencap(p, main_shot) else "main: screencap failed"]
    if surface:
        shots.append(f"second {second_shot}" if screencap(p, second_shot, surface)
                     else f"second: screencap -d {surface} failed")
    else:
        shots.append("second: no display id to photograph")
    if not stay:
        stop_app(p)

    for line in log_text.splitlines():
        if "[GUO] dual screen" in line or "[GUO] FATAL" in line or "FATAL EXCEPTION" in line:
            print("  " + line.strip())

    if verdict:
        say(f"OK; {'; '.join(shots)}; log {log_file}")
        return 0
    say("FAILED: " + ("the client reported a failure or died" if verdict is False
                      else f"no '{DUAL_OK}' within {timeout}s") + f"; {'; '.join(shots)}; log {log_file}")
    return 1


PORTRAIT_DONE = "[GUO] portrait probe: done"
PORTRAIT_FAIL = "[GUO] portrait probe: FAIL"
PORTRAIT_HOLD = "[GUO] portrait: hold "
PORTRAIT_ROW = "[GUO] portrait: row "


def portrait_probe(p: Paths, timeout: int, skip_export: bool, extra_args: str, stay: bool) -> int:
    """C9: export with --portrait-probe, photograph each state it holds, write the table."""
    apk = p.out_dir / "GUO-portrait.apk"
    if not skip_export:
        if export(p, f"--portrait-probe {extra_args}".strip(), apk) != 0:
            return 1
    if install(p, apk) != 0:
        say("install FAILED")
        return 1

    out = p.out_dir / "portrait"
    out.mkdir(parents=True, exist_ok=True)
    adb = adb_cmd(p)
    wake_device(p)
    subprocess.run(adb + ["logcat", "-c"])
    stop_app(p)
    if start_app(p) != 0:
        return 1

    say(f"waiting up to {timeout}s for '{PORTRAIT_DONE}' on logcat, photographing each hold")
    deadline = time.time() + timeout
    shot: set[str] = set()
    verdict = None
    log_text = ""
    while time.time() < deadline:
        time.sleep(1)
        log_text = subprocess.run(adb + ["logcat", "-d", "-v", "time"] + LOGCAT_FILTER,
                                  capture_output=True, text=True, errors="replace").stdout
        # The client holds each state for five seconds after naming it.
        for line in log_text.splitlines():
            if PORTRAIT_HOLD in line:
                name = line.split(PORTRAIT_HOLD, 1)[1].strip()
                if name and name not in shot:
                    shot.add(name)
                    ok = screencap(p, out / f"{name}.png")
                    say(f"hold {name}: " + ("photographed" if ok else "screencap failed"))
        if PORTRAIT_DONE in log_text:
            verdict = True
            break
        if PORTRAIT_FAIL in log_text or "FATAL EXCEPTION" in log_text or "[GUO] FATAL" in log_text:
            verdict = False
            break
        if f"Process {p.cfg.android_package}" in log_text and "has died" in log_text:
            verdict = False
            break

    if not stay:
        stop_app(p)
    (out / "portrait_logcat.txt").write_text(log_text, encoding="utf-8")

    rows = [line.split(PORTRAIT_ROW, 1)[1].strip() for line in log_text.splitlines() if PORTRAIT_ROW in line]
    table = out / "portrait_table.md"
    header = ["| # | Measure | Landscape | Portrait | Verdict |", "|---|---|---|---|---|"]
    table.write_text("\n".join(header + rows) + "\n", encoding="utf-8")
    for r in rows:
        print("  " + r)

    if verdict:
        say(f"OK; {len(shot)} photos and the table in {out}")
        return 0
    say("FAILED: " + ("the client reported a failure or died" if verdict is False
                      else f"no '{PORTRAIT_DONE}' within {timeout}s") + f"; what there is: {out}")
    return 1


# ---------------------------------------------------------------------------
# main
# ---------------------------------------------------------------------------


def main(argv: list[str] | None = None) -> int:
    parser = argparse.ArgumentParser(description=__doc__, formatter_class=argparse.RawDescriptionHelpFormatter)
    sub = parser.add_subparsers(dest="command", required=True)

    d = sub.add_parser("doctor", help="check the toolchain and say what is missing")
    d.add_argument("--publish", action="store_true", help="also dotnet publish the C# for android-arm64")
    sub.add_parser("templates", help="unpack the mono export templates for the pinned Godot")
    sub.add_parser("keystore", help="generate the standard debug keystore")
    sub.add_parser("settings", help="write the Android paths into Godot's editor settings")
    pr = sub.add_parser("preset", help="render export_presets.cfg from the template")
    pr.add_argument("--args", default="", help="extra client flags to bake in")
    pr.add_argument("--sound", action="store_true", help="audible build (default bakes --silent)")
    ex = sub.add_parser("export", help="export a debug APK, headless")
    ex.add_argument("--args", default="", help="extra client flags to bake in (after --play --client-data ...)")
    ex.add_argument("--out", default=None, help="APK path (default build\\android\\GUO-debug.apk)")
    ex.add_argument("--sound", action="store_true", help="audible build (default bakes --silent)")
    ex.add_argument("--abi", choices=ABIS, default="arm64",
                    help="arm64 (devices, the default), x86_64 (the Android emulator on a PC) or both")
    ex.add_argument("--no-client-data", action="store_true",
                    help="bake no --client-data: the client looks for its data itself, as a player's install does")
    ins = sub.add_parser("install", help="adb install the APK")
    ins.add_argument("--apk", default=None)
    sub.add_parser("run", help="start the app and stream its logcat")
    sub.add_parser("logcat", help="stream the app's logcat")
    sub.add_parser("push", help="adb push the UO client data to the device")
    ps = sub.add_parser("push-stage", help="adb push a staged data set (ADR-0022) beside the client data")
    ps.add_argument("stage", type=Path, help="the stage folder, e.g. build/uodata/moshu")
    ps.add_argument("--reverse", type=int, metavar="PORT", help="adb reverse tcp:PORT, to reach a loopback shard")
    sm = sub.add_parser("smoke", help="export, install, run, wait for the login gump, pull a screenshot")
    sm.add_argument("--timeout", type=int, default=240, help="seconds to wait for the login gump")
    sm.add_argument("--no-export", action="store_true", help="reuse build\\android\\GUO-smoke.apk")
    sm.add_argument("--sound", action="store_true", help="audible build (default bakes --silent)")
    dp = sub.add_parser("dual_probe", help="export with --dual-probe, run, wait for the verdict, photograph both displays")
    dp.add_argument("--timeout", type=int, default=300, help="seconds to wait for the verdict")
    dp.add_argument("--no-export", action="store_true", help="reuse build\\android\\GUO-dual.apk")
    dp.add_argument("--args", default="", help="extra client flags to bake in (e.g. --host <pc-lan-ip>)")
    dp.add_argument("--stay", action="store_true", help="leave the app running afterwards")
    pp = sub.add_parser("portrait_probe", help="C9: export with --portrait-probe, photograph each state, write the table")
    pp.add_argument("--timeout", type=int, default=600, help="seconds to wait for the probe to finish")
    pp.add_argument("--no-export", action="store_true", help="reuse build\\android\\GUO-portrait.apk")
    pp.add_argument("--args", default="", help="extra client flags to bake in (e.g. --host <pc-lan-ip>)")
    pp.add_argument("--stay", action="store_true", help="leave the app running afterwards")
    sub.add_parser("displays", help="list the device's displays as dumpsys reports them")

    args = parser.parse_args(argv)
    p = Paths(load_config())

    if args.command == "doctor":
        return Doctor(p).run(args.publish)
    if args.command == "templates":
        return install_templates(p)
    if args.command == "keystore":
        return make_keystore(p)
    if args.command == "settings":
        write_editor_settings(p)
        return 0
    if args.command == "preset":
        render_preset(p, device_args(p, args.args, args.sound), p.apk)
        return 0
    if args.command == "export":
        return export(p, args.args, Path(args.out) if args.out else p.apk, args.sound, not args.no_client_data,
                      args.abi)
    if args.command == "install":
        return install(p, Path(args.apk) if args.apk else p.apk)
    if args.command == "run":
        return run_app(p)
    if args.command == "logcat":
        return stream_logcat(p)
    if args.command == "push":
        return push_data(p)
    if args.command == "push-stage":
        return push_stage(p, args.stage, args.reverse)
    if args.command == "smoke":
        return smoke(p, args.timeout, args.no_export, args.sound)
    if args.command == "dual_probe":
        return dual_probe(p, args.timeout, args.no_export, args.args, args.stay)
    if args.command == "portrait_probe":
        return portrait_probe(p, args.timeout, args.no_export, args.args, args.stay)
    if args.command == "displays":
        for d in android_displays(p):
            print("  " + str(d))
        return 0
    return 2


if __name__ == "__main__":
    sys.exit(main())
