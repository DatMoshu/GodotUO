r"""Export GUO as a Windows build, and prove the executable carries the brand.

    launchers\windows\doctor.bat   what a Godot 4.7 .NET Windows export needs,
                                   and what this machine has
    launchers\windows\export.bat   headless --export-debug "Windows Desktop"
                                   into build\windows\GUO.exe, then checks
                                   the executable's icon

    python tools\windows\run.py <doctor|preset|export|icon> [...]

The export preset is rendered from export_presets.template.cfg into
godot\GUO\export_presets.cfg, which is gitignored; the Android and web
tools render the same file from their own templates, and each tool
re-renders before it exports, so the three never need to coexist.

THE ICON. Godot 4.7 writes the icon and the version strings into the
exported executable's PE resources itself; the `export/windows/rcedit`
editor setting of Godot 4.3 and older no longer exists (the 4.7 binary
holds no "rcedit" string at all), so there is nothing to install and
nothing to wire. The preset names godot\GUO\icon.ico, built by
tools\brand\run.py, for both the game executable and its console wrapper.
`icon` extracts the icon Windows associates with the exported .exe, saves
it as a PNG next to the build, and compares it with the template's own
icon (must differ: that one is the engine's logo) and with ours (must
match). `export` runs that check last, so a build whose icon is still the
engine's fails the export.

WHAT IT TOUCHES

  godot\GUO\export_presets.cfg     rendered; gitignored
  godot\GUO\GUO.sln                generated if missing; gitignored
  build\windows\                   the export, its log, the icon dumps
"""

from __future__ import annotations

import argparse
import os
import re
import shutil
import subprocess
import sys
from pathlib import Path

sys.path.insert(0, str(Path(__file__).resolve().parents[1]))

from guo.config import Config, godot_config_dir, godot_data_dir, load_config  # noqa: E402

HERE = Path(__file__).resolve().parent
TEMPLATE = HERE / "export_presets.template.cfg"
PRESET_NAME = "Windows Desktop"
ARCH = "x86_64"

# How far the exported icon may be, per channel on average, from the one we
# built (both at 32 px). Windows re-decodes the frame; a few units is normal.
ICON_TOLERANCE = 12.0


# ---------------------------------------------------------------------------
# paths
# ---------------------------------------------------------------------------


class Paths:
    def __init__(self, cfg: Config):
        self.cfg = cfg
        self.project = cfg.godot_project
        self.out_dir = cfg.build / "windows"
        self.exe = self.out_dir / "GUO.exe"
        self.preset_file = self.project / "export_presets.cfg"
        self.solution = self.project / "GUO.sln"
        self.ico = self.project / "icon.ico"
        self.splash = self.project / "splash.png"

        self.godot_config = godot_config_dir()
        self.editor_settings = self.godot_config / f"editor_settings-{cfg.godot_version.split('-')[0][:3]}.tres"
        # "4.7.2-stable" + mono -> "4.7.2.stable.mono", Godot's folder name.
        self.templates_version = cfg.godot_version.replace("-", ".") + ".mono"
        self.templates_dir = godot_data_dir() / "export_templates" / self.templates_version
        self.template_exe = self.templates_dir / f"windows_debug_{ARCH}.exe"
        self.template_console = self.templates_dir / f"windows_debug_{ARCH}_console.exe"

    def godot_console(self) -> Path:
        env = os.environ.get("GODOT_CONSOLE")
        if env and Path(env).exists():
            return Path(env)
        return self.cfg.godot_console_exe


# ---------------------------------------------------------------------------
# helpers
# ---------------------------------------------------------------------------


def say(msg: str) -> None:
    print(f"[windows] {msg}", flush=True)


def run(cmd: list, **kw) -> subprocess.CompletedProcess:
    say("$ " + " ".join(f'"{c}"' if " " in str(c) else str(c) for c in cmd))
    return subprocess.run([str(c) for c in cmd], **kw)


def ico_sizes(path: Path) -> list[int]:
    try:
        from PIL import Image
        with Image.open(path) as im:
            return sorted(s[0] for s in im.info.get("sizes", {(im.width, im.height)}))
    except Exception:  # noqa: BLE001 - the doctor reports, it does not crash
        return []


def project_setting(project: Path, key: str) -> str | None:
    text = (project / "project.godot").read_text(encoding="utf-8", errors="replace")
    m = re.search(rf"^{re.escape(key)}=(.*)$", text, re.MULTILINE)
    return m.group(1).strip() if m else None


# ---------------------------------------------------------------------------
# the icon check
# ---------------------------------------------------------------------------


def extract_exe_icon(exe: Path, png: Path) -> bool:
    """The icon Windows shows for `exe` (Explorer, the taskbar), as a 32 px PNG."""
    script = (
        "Add-Type -AssemblyName System.Drawing; "
        f"$i = [System.Drawing.Icon]::ExtractAssociatedIcon('{exe}'); "
        f"$i.ToBitmap().Save('{png}', [System.Drawing.Imaging.ImageFormat]::Png)"
    )
    r = subprocess.run(["powershell", "-NoProfile", "-ExecutionPolicy", "Bypass", "-Command", script],
                       capture_output=True, text=True)
    if r.returncode != 0 or not png.exists():
        say(f"could not extract the icon of {exe}: {(r.stderr or r.stdout).strip()[:200]}")
        return False
    return True


def mean_abs_diff(a_path: Path, b_path: Path) -> float:
    from PIL import Image, ImageChops
    a = Image.open(a_path).convert("RGBA")
    b = Image.open(b_path).convert("RGBA")
    if a.size != b.size:
        b = b.resize(a.size, Image.LANCZOS)
    # Compare over an opaque backdrop so alpha differences count as colour.
    back = Image.new("RGBA", a.size, (128, 128, 128, 255))
    a = Image.alpha_composite(back, a)
    b = Image.alpha_composite(back, b)
    diff = ImageChops.difference(a, b).convert("RGB")
    hist = diff.histogram()
    total = 0
    for ch in range(3):
        total += sum(v * n for v, n in enumerate(hist[ch * 256:(ch + 1) * 256]))
    return total / (a.width * a.height * 3)


def ico_frame_png(ico: Path, size: int, png: Path) -> None:
    from PIL import Image
    im = Image.open(ico)
    im.size = (size, size)
    im.load()
    im.convert("RGBA").save(png)


def check_icon(p: Paths, exe: Path | None = None) -> int:
    """Extract the exported executable's icon and say whose it is."""
    exe = exe or p.exe
    if not exe.exists():
        say(f"no executable at {exe}; export first")
        return 1
    p.out_dir.mkdir(parents=True, exist_ok=True)
    got = p.out_dir / "exe_icon.png"
    ours = p.out_dir / "expected_icon_32.png"
    engine = p.out_dir / "template_icon.png"
    if not extract_exe_icon(exe, got):
        return 1
    ico_frame_png(p.ico, 32, ours)
    d_ours = mean_abs_diff(got, ours)
    line = f"icon of {exe.name}: {got} ; vs ours {d_ours:.1f}"
    d_engine = None
    if p.template_exe.exists() and extract_exe_icon(p.template_exe, engine):
        d_engine = mean_abs_diff(got, engine)
        line += f" ; vs the template's (engine logo) {d_engine:.1f}"
    say(line)
    ok = d_ours <= ICON_TOLERANCE and (d_engine is None or d_engine > ICON_TOLERANCE)
    say("icon ok: the executable carries the sigil" if ok else "icon FAILED: the executable does not carry the sigil")
    return 0 if ok else 1


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

    def run(self) -> int:
        p, cfg = self.p, self.p.cfg
        print("[windows] doctor -- what a Godot 4.7 .NET Windows export needs on this machine\n")

        dotnet = shutil.which("dotnet")
        self.check("dotnet SDK", dotnet is not None, dotnet or "not on PATH",
                   "install the .NET 8 SDK (or newer) and put dotnet on PATH")

        console = p.godot_console()
        self.check("Godot console (mono)", console.exists(), str(console),
                   r"launchers\dev\fetch_godot.bat, or set GODOT_EXE in config.bat")

        have = p.template_exe.exists() and p.template_console.exists()
        self.check("Windows export templates", have,
                   str(p.template_exe) if have else f"{p.template_exe.name} / {p.template_console.name} not in {p.templates_dir}",
                   f"put Godot_v{cfg.godot_version}_mono_export_templates.tpz in tools\\godot\\templates and run "
                   "python tools\\android\\run.py templates (the same archive holds every platform)")

        self.check("solution file (GUO.sln)", p.solution.exists(), str(p.solution),
                   "generated on the first export")

        sizes = ico_sizes(p.ico)
        self.check("icon.ico (the sigil)", bool(sizes) and 256 in sizes and 16 in sizes,
                   f"{p.ico} sizes {sizes}" if sizes else f"{p.ico} missing or unreadable",
                   "python tools\\brand\\run.py")
        self.check("splash.png (the sigil)", p.splash.exists(), str(p.splash), "python tools\\brand\\run.py")

        native = project_setting(p.project, "config/windows_native_icon")
        self.check("config/windows_native_icon", native == '"res://icon.ico"', native or "(unset)",
                   'project.godot: config/windows_native_icon="res://icon.ico"')
        image = project_setting(p.project, "boot_splash/image")
        self.check("boot_splash/image", image == '"res://splash.png"', image or "(unset: the engine's logo would show)",
                   'project.godot: boot_splash/image="res://splash.png"')

        self.check("preset template", TEMPLATE.exists(), str(TEMPLATE))

        # Not a check: there is nothing to install. Godot 4.3 and older
        # needed rcedit for the icon; 4.7 edits the PE itself.
        print("  info rcedit                       not needed: Godot 4.7 writes the icon and version info into the "
              "executable itself (no export/windows/rcedit setting exists in this version)")
        signtool = None
        if p.editor_settings.exists():
            m = re.search(r'export/windows/signtool = "(.*)"', p.editor_settings.read_text(encoding="utf-8", errors="replace"))
            signtool = m.group(1) if m else None
        print(f"  info signtool                     {signtool or '(none)'}; the debug preset does not sign")

        print()
        if self.missing:
            say(f"{self.missing} of the checks above failed")
            return 1
        say("everything a Windows export needs is here")
        return 0


# ---------------------------------------------------------------------------
# preset / export
# ---------------------------------------------------------------------------


def render_preset(p: Paths, export_path: Path) -> Path:
    text = TEMPLATE.read_text(encoding="utf-8")
    values = {"EXPORT_PATH": str(export_path).replace("\\", "/")}
    for key, value in values.items():
        text = text.replace("{{" + key + "}}", value)
    leftover = re.findall(r"{{\w+}}", text)
    if leftover:
        sys.exit(f"[windows] template placeholders without a value: {leftover}")
    p.preset_file.write_text(text, encoding="utf-8")
    say(f"rendered {p.preset_file}")
    return p.preset_file


def ensure_solution(p: Paths) -> None:
    if p.solution.exists():
        return
    say("no GUO.sln; generating one (the .NET export needs it)")
    # The .NET 10 SDK writes GUO.slnx unless told the classic format, and
    # the export needs GUO.sln. SDKs before 9.0.200 have no --format and
    # write .sln anyway, so they get the plain call.
    new_sln = ["dotnet", "new", "sln", "-n", "GUO", "-o", p.project]
    if subprocess.run([*new_sln, "--format", "sln"], stdout=subprocess.DEVNULL,
                      stderr=subprocess.DEVNULL).returncode != 0:
        run(new_sln, stdout=subprocess.DEVNULL)
    run(["dotnet", "sln", p.solution, "add", p.project / "GUO.csproj"], stdout=subprocess.DEVNULL)
    if not p.solution.exists():
        sys.exit("[windows] could not produce GUO.sln")


def export(p: Paths, exe: Path) -> int:
    console = p.godot_console()
    if not console.exists():
        sys.exit(f"[windows] Godot console not found at {console}; run doctor")
    if not p.ico.exists():
        sys.exit(f"[windows] no {p.ico}; run python tools\\brand\\run.py first")
    exe.parent.mkdir(parents=True, exist_ok=True)
    ensure_solution(p)
    render_preset(p, exe)

    log = exe.parent / "export.log"
    with open(log, "w", encoding="utf-8") as f:
        result = run(
            [console, "--headless", "--path", p.project, "--export-debug", PRESET_NAME, exe],
            stdout=f, stderr=subprocess.STDOUT, text=True,
        )
    text = log.read_text(encoding="utf-8", errors="replace")
    for line in text.splitlines():
        if re.search(r"error|failed|not found|required|not support|icon", line, re.IGNORECASE):
            print("  " + line.strip()[:200])
    if result.returncode != 0 or not exe.exists():
        say(f"export FAILED (exit {result.returncode}); full log: {log}")
        return 1
    say(f"exported {exe} ({exe.stat().st_size // 1024} KB); log: {log}")
    return check_icon(p, exe)


# ---------------------------------------------------------------------------
# main
# ---------------------------------------------------------------------------


def main(argv: list[str] | None = None) -> int:
    parser = argparse.ArgumentParser(description=__doc__, formatter_class=argparse.RawDescriptionHelpFormatter)
    sub = parser.add_subparsers(dest="command", required=True)

    sub.add_parser("doctor", help="check the toolchain and say what is missing")
    sub.add_parser("preset", help="render export_presets.cfg from the template")
    ex = sub.add_parser("export", help="export the Windows build, headless, then check its icon")
    ex.add_argument("--out", default=None, help="executable path (default build\\windows\\GUO.exe)")
    ic = sub.add_parser("icon", help="extract the exported executable's icon and say whose it is")
    ic.add_argument("--exe", default=None, help="executable to inspect (default build\\windows\\GUO.exe)")

    args = parser.parse_args(argv)
    p = Paths(load_config())

    if args.command == "doctor":
        return Doctor(p).run()
    if args.command == "preset":
        render_preset(p, p.exe)
        return 0
    if args.command == "export":
        return export(p, Path(args.out) if args.out else p.exe)
    if args.command == "icon":
        return check_icon(p, Path(args.exe) if args.exe else None)
    return 2


if __name__ == "__main__":
    sys.exit(main())
