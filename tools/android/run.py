r"""Build, install, run and smoke-test GUO on an Android device.

    launchers\android\doctor.bat      what is missing, and the fix for each
    launchers\android\export.bat      a debug APK, headless, into build\android
    launchers\android\install.bat     adb install onto the attached device
    launchers\android\run.bat         start it and stream its logcat
    launchers\android\smoke.bat       export, install, run, wait for the login
                                      gump, pull a screenshot and a log

    python tools\android\run.py <doctor|templates|keystore|settings|preset|
                                 export|install|run|logcat|push|smoke> [...]

What Godot needs to export a .NET project for Android, and where each part
comes from, is in ADR-0007 and README.md next to this file. In short: a
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

from guo.config import Config, load_config  # noqa: E402

HERE = Path(__file__).resolve().parent
TEMPLATE = HERE / "export_presets.template.cfg"
PRESET_NAME = "Android"
ACTIVITY = "com.godot.game.GodotAppLauncher"  # 4.7: the exported launcher; GodotApp itself is not exported

# What logcat says when the login probe has drawn the login gump, and when it
# has not; see src/Bootstrap/LoginProbe.cs.
LOGIN_OK = "[GUO] login probe: ok"
LOGIN_FAIL = "[GUO] login probe: FAIL"


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

        appdata = Path(os.environ.get("APPDATA", str(Path.home() / "AppData" / "Roaming")))
        self.godot_config = appdata / "Godot"
        major_minor = ".".join(cfg.godot_version.split("-")[0].split(".")[:2])
        self.editor_settings = self.godot_config / f"editor_settings-{major_minor}.tres"

        # "4.7.2-stable" + mono -> "4.7.2.stable.mono", Godot's folder name.
        self.templates_version = cfg.godot_version.replace("-", ".") + ".mono"
        self.templates_dir = self.godot_config / "export_templates" / self.templates_version
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

        # --- the C# half, for real ------------------------------------------
        if publish_check:
            print("\n[android] doctor: dotnet publish for android-arm64 (the .NET half of an export) ...")
            ok = dotnet_publish_check(p)
            self.check("dotnet publish android-arm64", ok, "see above",
                       "read the errors above; the csproj guards are in GUO.csproj (ADR-0007)")
        else:
            print("  info dotnet publish check         skipped; run with --publish to build the C# for android-arm64")

        print()
        if self.missing:
            say(f"{self.missing} thing(s) missing; fix them in the order listed")
            return 1
        say("everything an export needs is here")
        return 0


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


def render_preset(p: Paths, extra_args: str, export_path: Path) -> Path:
    text = TEMPLATE.read_text(encoding="utf-8")
    values = {
        "PACKAGE": p.cfg.android_package,
        "KEYSTORE": str(p.keystore).replace("\\", "/"),
        "KEYSTORE_USER": p.cfg.android_keystore_user,
        "KEYSTORE_PASSWORD": p.cfg.android_keystore_password,
        "EXTRA_ARGS": extra_args.replace('"', '\\"'),
        "EXPORT_PATH": str(export_path).replace("\\", "/"),
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
        run(["dotnet", "new", "sln", "-n", "GUO", "-o", p.project], stdout=subprocess.DEVNULL)
        run(["dotnet", "sln", p.solution, "add", p.project / "GUO.csproj"], stdout=subprocess.DEVNULL)
    if not p.solution.exists():
        sys.exit("[android] could not produce GUO.sln; the .NET export needs one")


def device_args(p: Paths, extra: str) -> str:
    """The client's command line on the device: where its data is, then whatever the caller adds."""
    # After "--": Main.cs reads OS.GetCmdlineUserArgs(), which is only what
    # follows the separator; without it the engine kept the flags and the
    # client saw none of them (it died with "No UO client data directory").
    base = f"-- --play --client-data {p.cfg.android_client_data}"
    return f"{base} {extra}".strip()


# ---------------------------------------------------------------------------
# export / install / run / smoke
# ---------------------------------------------------------------------------


def export(p: Paths, extra_args: str, apk: Path) -> int:
    console = p.godot_console()
    if not console.exists():
        sys.exit(f"[android] Godot console not found at {console}; run doctor")
    p.out_dir.mkdir(parents=True, exist_ok=True)
    write_editor_settings(p)
    ensure_solution(p)
    render_preset(p, device_args(p, extra_args), apk)
    if apk.exists():
        apk.unlink()

    log = p.out_dir / "export.log"
    with open(log, "w", encoding="utf-8") as f:
        result = run(
            [console, "--headless", "--path", p.project, "--export-debug", PRESET_NAME, apk],
            stdout=f, stderr=subprocess.STDOUT, text=True,
        )
    text = log.read_text(encoding="utf-8", errors="replace")
    for line in text.splitlines():
        if re.search(r"error|failed|not found|required", line, re.IGNORECASE):
            print("  " + line.strip())
    if result.returncode != 0 or not apk.exists():
        say(f"export FAILED (exit {result.returncode}); full log: {log}")
        return 1
    say(f"exported {apk} ({apk.stat().st_size // 1024 // 1024} MB); log: {log}")
    return 0


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
    src = p.cfg.client_data
    if not (src / "tiledata.mul").exists():
        sys.exit(f"[android] {src} does not look like a UO install (no tiledata.mul)")
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


def smoke(p: Paths, timeout: int, skip_export: bool) -> int:
    apk = p.out_dir / "GUO-smoke.apk"
    if not skip_export:
        # The smoke build says on the log when the login gump is drawn, and
        # stays up so a screenshot can be taken of it.
        if export(p, "--login-probe-stay", apk) != 0:
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
    ex = sub.add_parser("export", help="export a debug APK, headless")
    ex.add_argument("--args", default="", help="extra client flags to bake in (after --play --client-data ...)")
    ex.add_argument("--out", default=None, help="APK path (default build\\android\\GUO-debug.apk)")
    ins = sub.add_parser("install", help="adb install the APK")
    ins.add_argument("--apk", default=None)
    sub.add_parser("run", help="start the app and stream its logcat")
    sub.add_parser("logcat", help="stream the app's logcat")
    sub.add_parser("push", help="adb push the UO client data to the device")
    sm = sub.add_parser("smoke", help="export, install, run, wait for the login gump, pull a screenshot")
    sm.add_argument("--timeout", type=int, default=240, help="seconds to wait for the login gump")
    sm.add_argument("--no-export", action="store_true", help="reuse build\\android\\GUO-smoke.apk")

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
        render_preset(p, device_args(p, args.args), p.apk)
        return 0
    if args.command == "export":
        return export(p, args.args, Path(args.out) if args.out else p.apk)
    if args.command == "install":
        return install(p, Path(args.apk) if args.apk else p.apk)
    if args.command == "run":
        return run_app(p)
    if args.command == "logcat":
        return stream_logcat(p)
    if args.command == "push":
        return push_data(p)
    if args.command == "smoke":
        return smoke(p, args.timeout, args.no_export)
    return 2


if __name__ == "__main__":
    sys.exit(main())
