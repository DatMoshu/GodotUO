"""Shared path resolution for the post tools: no machine paths in the repo.

Codex's review folder resolves as: --src <dir>, else env GUO_HELL_CARNIVAL_SRC, else
<checkout>/build/uo_original_expansion/hell_carnival, checking this checkout first and then
the main checkout (the folder is gitignored, so a worktree usually lacks it).
"""
import os, shutil, subprocess, sys
from pathlib import Path

ROOT = Path(__file__).resolve().parents[2]
REL = Path("build") / "uo_original_expansion" / "hell_carnival"


def _main_checkout():
    try:
        common = subprocess.run(["git", "rev-parse", "--path-format=absolute", "--git-common-dir"],
                                cwd=ROOT, capture_output=True, text=True, check=True).stdout.strip()
        return Path(common).parent
    except (OSError, subprocess.CalledProcessError):
        return ROOT


def src_dir(argv=sys.argv):
    if "--src" in argv:
        return Path(argv[argv.index("--src") + 1])
    if os.environ.get("GUO_HELL_CARNIVAL_SRC"):
        return Path(os.environ["GUO_HELL_CARNIVAL_SRC"])
    for base in (ROOT, _main_checkout()):
        if (base / REL).is_dir():
            return base / REL
    sys.exit(f"[hell_carnival_post] review folder not found under {REL}; pass --src <dir> or set GUO_HELL_CARNIVAL_SRC")


def rar_exe():
    """env RAR_EXE, else Rar on PATH, else WinRAR's default install folder."""
    found = os.environ.get("RAR_EXE") or shutil.which("rar")
    return found or str(Path(os.environ.get("ProgramFiles", "C:/Program Files")) / "WinRAR" / "Rar.exe")


def font_file(name):
    return str(Path(os.environ.get("WINDIR", "C:/Windows")) / "Fonts" / name)
