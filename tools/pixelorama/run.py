#!/usr/bin/env python3
"""Pixelorama, GUO's pixel editor (ADR-0029): fetch it, open an image in it, install the UO extension.

    python tools/pixelorama/run.py fetch [--source] [--binary]
    python tools/pixelorama/run.py status
    python tools/pixelorama/run.py open <png> [--sidecar <json>]
    python tools/pixelorama/run.py extension [--install]
    python tools/pixelorama/run.py check

Nothing here writes to the UO client install. Pixelorama itself (binary and
source) is MIT, fetched into the gitignored tools/pixelorama/bin and
tools/pixelorama/src, never committed. See README.md in this folder.
"""

from __future__ import annotations

import argparse
import hashlib
import json
import os
import re
import shutil
import subprocess
import sys
import tarfile
import urllib.request
import zipfile
from pathlib import Path

sys.path.insert(0, str(Path(__file__).resolve().parents[1]))
from guo.config import find_repo_root, load_config, main_checkout  # noqa: E402

HERE = Path(__file__).resolve().parent
TAG = "v1.2.3"
# The owner's fork (ADR-0029). `upstream` stays a second remote for rebasing.
FORK_URL = "https://github.com/DatMoshu/GUO-Pixelorama.git"
UPSTREAM_URL = "https://github.com/Orama-Interactive/Pixelorama.git"
RELEASE_URL = f"https://github.com/Orama-Interactive/Pixelorama/releases/download/{TAG}/"
# asset name, SHA-256 of the file as published for the pinned tag
ASSETS = {
    "win32": ("Pixelorama-Windows-64bit.zip", "942e38288b818ef44e0c834691a00a521f811739af523a2cfdc09aae58753646"),
    "linux": ("Pixelorama-Linux-64bit.tar.gz", ""),
}
EXTENSION_NAME = "GUOTools"


def src_dir() -> Path:
    return HERE / "src"


def bin_dir() -> Path:
    return HERE / "bin"


def find_exe() -> Path | None:
    """The Pixelorama to run: UO_PIXELORAMA, then the fetched release binary, then PATH."""
    configured = load_config().pixelorama_setting
    if configured and configured.is_file():
        return configured
    names = ("Pixelorama.exe",) if sys.platform == "win32" else ("Pixelorama.x86_64", "Pixelorama")
    main = main_checkout(find_repo_root())
    folders = [bin_dir()]
    if main is not None:
        folders.append(main / "tools" / "pixelorama" / "bin")
    for folder in folders:
        for name in names:
            for hit in sorted(folder.rglob(name)) if folder.is_dir() else []:
                if hit.is_file():
                    return hit
    found = shutil.which("Pixelorama") or shutil.which("pixelorama")
    return Path(found) if found else None


def user_dir() -> Path:
    """Pixelorama's user:// (project.godot: custom_user_dir_name = pixelorama)."""
    if sys.platform == "win32":
        base = Path(os.environ.get("APPDATA", str(Path.home() / "AppData" / "Roaming")))
    elif sys.platform == "darwin":
        base = Path.home() / "Library" / "Application Support"
    else:
        base = Path(os.environ.get("XDG_DATA_HOME") or Path.home() / ".local" / "share")
    return base / "pixelorama"


def sha256(path: Path) -> str:
    h = hashlib.sha256()
    with path.open("rb") as f:
        for chunk in iter(lambda: f.read(1 << 20), b""):
            h.update(chunk)
    return h.hexdigest()


def git(*args: str, cwd: Path | None = None) -> str:
    out = subprocess.run(["git", *args], cwd=cwd, capture_output=True, text=True)
    if out.returncode != 0:
        raise RuntimeError(f"git {' '.join(args)}: {out.stderr.strip()}")
    return out.stdout.strip()


def fetch_source() -> None:
    src = src_dir()
    if (src / ".git").is_dir():
        # Re-point an existing clone: origin is the fork, upstream the original.
        remotes = git("remote", cwd=src).split()
        if "origin" in remotes:
            git("remote", "set-url", "origin", FORK_URL, cwd=src)
        if "upstream" not in remotes:
            git("remote", "add", "upstream", UPSTREAM_URL, cwd=src)
        else:
            git("remote", "set-url", "upstream", UPSTREAM_URL, cwd=src)
        print(f"[pixelorama] source already at {src.relative_to(find_repo_root())}; remotes refreshed")
        return
    src.parent.mkdir(parents=True, exist_ok=True)
    git("clone", "--quiet", "--depth", "1", "--branch", TAG, FORK_URL, str(src))
    git("remote", "add", "upstream", UPSTREAM_URL, cwd=src)
    print(f"[pixelorama] cloned {FORK_URL} at {TAG} into tools/pixelorama/src (upstream remote added)")


def fetch_binary() -> None:
    name, want = ASSETS.get(sys.platform, ("", ""))
    if not name:
        print("[pixelorama] no release binary is pinned for this OS; use --source and the pinned Godot")
        return
    bin_dir().mkdir(parents=True, exist_ok=True)
    archive = bin_dir() / name
    if not archive.is_file():
        print(f"[pixelorama] downloading {name}")
        urllib.request.urlretrieve(RELEASE_URL + name, archive)
    got = sha256(archive)
    if want and got != want:
        archive.unlink()
        raise SystemExit(f"[pixelorama] {name} SHA-256 {got} is not the pinned {want}; removed it")
    if archive.suffix == ".zip":
        with zipfile.ZipFile(archive) as z:
            z.extractall(bin_dir())
    else:
        with tarfile.open(archive) as t:
            t.extractall(bin_dir())
    print(f"[pixelorama] {name} ok, sha256 {got}")


def cmd_fetch(args: argparse.Namespace) -> int:
    both = not (args.source or args.binary)
    if args.source or both:
        fetch_source()
    if args.binary or both:
        fetch_binary()
    return cmd_status(args)


def extension_files() -> list[Path]:
    root = HERE / "extension"
    return [p for p in sorted(root.rglob("*")) if p.is_file() and p.name != "README.md"]


def build_extension(out: Path) -> Path:
    """Zip the extension the way Pixelorama loads one: a resource pack whose paths are res://src/Extensions/<name>/..."""
    root = HERE / "extension"
    out.parent.mkdir(parents=True, exist_ok=True)
    with zipfile.ZipFile(out, "w", zipfile.ZIP_DEFLATED) as z:
        for p in extension_files():
            z.write(p, f"src/Extensions/{EXTENSION_NAME}/" + p.relative_to(root).as_posix())
    return out


def enable_extension_text(text: str) -> str:
    """Patch one Godot ConfigFile key without parsing/reformatting Variant values.

    ConfigFile resembles INI but allows multiline dictionaries and arrays that
    Python's configparser cannot read. Preserve every unrelated byte instead.
    """
    newline = "\r\n" if "\r\n" in text else "\n"
    headers = list(re.finditer(r"^\[([^\r\n]+)\][ \t]*\r?$", text, re.MULTILINE))
    for index, header in enumerate(headers):
        if header.group(1) != "extensions":
            continue
        end = headers[index + 1].start() if index + 1 < len(headers) else len(text)
        section = text[header.end():end]
        pattern = rf'^(?P<lead>[ \t]*"?{re.escape(EXTENSION_NAME)}"?[ \t]*=[ \t]*)[^\r\n]*'
        if re.search(pattern, section, re.MULTILINE):
            section = re.sub(pattern, lambda match: match.group("lead") + "true", section, flags=re.MULTILINE)
        else:
            section += ("" if section.endswith("\n") else newline) + f"{EXTENSION_NAME}=true" + newline
        return text[:header.end()] + section + text[end:]
    return text + ("" if not text or text.endswith("\n") else newline) + f"[extensions]{newline}{EXTENSION_NAME}=true{newline}"


def enable_in_config() -> Path:
    """Enable the GUO extension, preserving Pixelorama's other preferences."""
    ini = user_dir() / "config.ini"
    text = ini.read_bytes().decode("utf-8") if ini.is_file() else ""
    updated = enable_extension_text(text)
    if updated != text:
        ini.parent.mkdir(parents=True, exist_ok=True)
        backup = ini.with_suffix(".guo-backup.ini")
        if ini.is_file() and not backup.exists():
            shutil.copy2(ini, backup)
        temporary = ini.with_suffix(".guo-tmp.ini")
        temporary.write_bytes(updated.encode("utf-8"))
        temporary.replace(ini)
    return ini


def cmd_extension(args: argparse.Namespace) -> int:
    cfg = load_config()
    out = build_extension(cfg.build / "pixelorama" / f"{EXTENSION_NAME}.zip")
    print(f"[pixelorama] extension built: {out.relative_to(cfg.root).as_posix()} ({len(extension_files())} files)")
    if args.install:
        dest = user_dir() / "extensions"
        dest.mkdir(parents=True, exist_ok=True)
        shutil.copyfile(out, dest / f"{EXTENSION_NAME}.zip")
        ini = enable_in_config()
        print(f"[pixelorama] installed into Pixelorama's extensions folder and enabled in {ini.name}")
    return 0


def cmd_open(args: argparse.Namespace) -> int:
    cfg = load_config()
    exe = find_exe()
    if exe is None:
        print("[pixelorama] not found. Run: python tools/pixelorama/run.py fetch   (or set UO_PIXELORAMA)")
        return 1
    png = Path(args.png).resolve()
    if not png.is_file():
        print(f"[pixelorama] no such file: {png}")
        return 1
    # Keep the extension current, so "Save back to GUO" exists.
    build_extension(cfg.build / "pixelorama" / f"{EXTENSION_NAME}.zip")
    dest = user_dir() / "extensions"
    dest.mkdir(parents=True, exist_ok=True)
    shutil.copyfile(cfg.build / "pixelorama" / f"{EXTENSION_NAME}.zip", dest / f"{EXTENSION_NAME}.zip")
    enable_in_config()
    env = dict(os.environ)
    env["GUO_ART_EXCHANGE"] = str(cfg.art_exchange)
    if args.sidecar:
        env["GUO_ART_SIDECAR"] = str(Path(args.sidecar).resolve())
    subprocess.Popen([str(exe), str(png)], env=env, cwd=str(exe.parent))
    print(f"[pixelorama] opened {png.name}")
    return 0


def cmd_status(_: argparse.Namespace) -> int:
    cfg = load_config()
    exe = find_exe()
    print(f"[pixelorama] pinned tag      {TAG}   (MIT; fork {FORK_URL.removesuffix('.git')}, upstream Orama-Interactive/Pixelorama)")
    print(f"[pixelorama] binary          {'found: ' + exe.name if exe else 'missing (fetch --binary)'}")
    src = src_dir()
    if (src / "project.godot").is_file():
        try:
            head = git("rev-parse", "--short", "HEAD", cwd=src)
            remotes = git("remote", "-v", cwd=src).splitlines()
            print(f"[pixelorama] source          tools/pixelorama/src @ {head}, remotes: {len(remotes) // 2}")
        except RuntimeError:
            print("[pixelorama] source          tools/pixelorama/src (not a git clone)")
    else:
        print("[pixelorama] source          missing (fetch --source)")
    print(f"[pixelorama] extension       {len(extension_files())} files in tools/pixelorama/extension")
    inst = user_dir() / "extensions" / f"{EXTENSION_NAME}.zip"
    print(f"[pixelorama] installed       {'yes' if inst.is_file() else 'no (extension --install, or open does it)'}")
    print(f"[pixelorama] exchange folder {cfg.art_exchange.relative_to(cfg.root).as_posix() if cfg.art_exchange.is_relative_to(cfg.root) else '(configured)'}")
    return 0


def cmd_check(_: argparse.Namespace) -> int:
    """Load the extension's scripts in the pinned Godot against Pixelorama's source, headless, and report parse errors."""
    cfg = load_config()
    src = src_dir()
    if not (src / "project.godot").is_file():
        print("[pixelorama] check needs the source: python tools/pixelorama/run.py fetch --source")
        return 1
    ext = src / "src" / "Extensions" / EXTENSION_NAME
    shutil.rmtree(ext, ignore_errors=True)
    shutil.copytree(HERE / "extension", ext, ignore=shutil.ignore_patterns("README.md"))
    try:
        # --import makes Pixelorama's own class cache; --check-only parses one script in that project.
        subprocess.run([str(cfg.godot_console_exe), "--headless", "--path", str(src), "--import", "--quit"],
                       capture_output=True, text=True, timeout=900)
        bad = 0
        for gd in sorted(ext.rglob("*.gd")):
            rel = "res://" + gd.relative_to(src).as_posix()
            r = subprocess.run([str(cfg.godot_console_exe), "--headless", "--path", str(src), "--check-only", "--script", rel],
                               capture_output=True, text=True, timeout=300)
            ok = r.returncode == 0 and "ERROR" not in r.stdout + r.stderr
            bad += 0 if ok else 1
            print(f"[pixelorama] parse {rel.removeprefix('res://src/Extensions/')}: {'ok' if ok else 'FAILED'}")
            if not ok:
                print((r.stdout + r.stderr).strip()[-1500:])
        return 1 if bad else 0
    finally:
        shutil.rmtree(ext, ignore_errors=True)


def main() -> int:
    ap = argparse.ArgumentParser(description=__doc__, formatter_class=argparse.RawDescriptionHelpFormatter)
    sub = ap.add_subparsers(dest="cmd", required=True)
    f = sub.add_parser("fetch", help="clone the fork at the pinned tag and download the release build")
    f.add_argument("--source", action="store_true")
    f.add_argument("--binary", action="store_true")
    f.set_defaults(fn=cmd_fetch)
    sub.add_parser("status", help="what is fetched and installed").set_defaults(fn=cmd_status)
    o = sub.add_parser("open", help="open a PNG in Pixelorama")
    o.add_argument("png")
    o.add_argument("--sidecar")
    o.set_defaults(fn=cmd_open)
    e = sub.add_parser("extension", help="build the GUOTools extension zip; --install puts it in Pixelorama")
    e.add_argument("--install", action="store_true")
    e.set_defaults(fn=cmd_extension)
    sub.add_parser("check", help="parse the extension scripts against Pixelorama's source, headless").set_defaults(fn=cmd_check)
    args = ap.parse_args()
    return args.fn(args)


if __name__ == "__main__":
    sys.exit(main())
