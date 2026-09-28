"""Stages the shareable Hellmaw / Cosmic Carnival ARTWORK bundle and packs it with WinRAR.

Copies an allow-list out of Codex's review folder (read only) into
build/hell_carnival_post/stage/<NAME>, writes README_BUNDLE.md, refuses to go on
if anything client-derived slipped in, then runs
    Rar.exe a -m5 -ma5 -s -md1g -rr3% [-v490m]
into build/hell_carnival_post/upload/.

    python tools/hell_carnival_post/bundle.py [--no-rar] [--src <review folder>]
"""
import shutil, subprocess, sys
from pathlib import Path

from paths import ROOT, rar_exe, src_dir

SRC = src_dir()
POST = ROOT / "build" / "hell_carnival_post"
NAME = "GUO_Hellmaw_CosmicCarnival_art_2026-09-27"
STAGE = POST / "stage" / NAME
UPLOAD = POST / "upload"
RAR = rar_exe()

TOP = ["README.md", "DUNGEON_DEMO_AGENT_HANDOFF.md", "DUNGEON_PREVIEW_AUDIT.md",
       "manifest.json", "manifest.csv", "native-atlas.png", "native-atlas.json", "packaging-map.json",
       "texmap-targets.json", "preview-aliases.json", "validation.json", "chunk-coverage.json", "coverage.json",
       "terrain-validation.json", "animation-dependencies.json", "hell-optional-layer.json",
       "carnival-optional-layer.json"]
DIRS = ["assets", "masters", "prompts", "payloads"]
FORBIDDEN_EXT = {".mul", ".uop", ".idx", ".def", ".exe", ".dll", ".cfg", ".ini", ".env", ".key", ".token"}
FORBIDDEN_PARTS = {"references", "fixtures", "worlds", "local-only-clients", ".cache", "cache"}


def keep_preview(n):
    return "chunks" in n or "contact" in n or "seams" in n


def stage():
    if STAGE.exists():
        shutil.rmtree(STAGE)
    STAGE.mkdir(parents=True)
    for f in TOP:
        shutil.copy2(SRC / f, STAGE / f)
    for d in DIRS:
        shutil.copytree(SRC / d, STAGE / d)
    (STAGE / "previews").mkdir()
    for p in (SRC / "previews").glob("*.png"):
        if keep_preview(p.name):
            shutil.copy2(p, STAGE / "previews" / p.name)
    for d in (SRC / "guo").iterdir():
        if "-chunks-" in d.name:
            (STAGE / "guo" / d.name).mkdir(parents=True)
            for f in ["world_shot.png", "report.json", "provenance.json"]:
                shutil.copy2(d / f, STAGE / "guo" / d.name / f)
    (STAGE / "tools").mkdir()
    for p in (SRC / "tools").glob("*.py"):
        shutil.copy2(p, STAGE / "tools" / p.name)
    # The A/B page, without the two buttons for the earlier audits of other packs (not in this bundle).
    html = (SRC / "index.html").read_text(encoding="utf-8")
    for b in ["<button onclick=\"choose('old-cave')\">🔍 Earlier cave audit</button>",
              "<button onclick=\"choose('old-brick')\">🔍 Earlier brick audit</button>"]:
        assert b in html, b
        html = html.replace(b, "")
    (STAGE / "index.html").write_text(html, encoding="utf-8")
    shutil.copytree(POST / "attachments", STAGE / "post" / "attachments")
    for f in ["post_messages.md", "README_BUNDLE.md"]:
        if (POST / f).exists():
            shutil.copy2(POST / f, STAGE / ("post" if f == "post_messages.md" else ".") / f)


def audit():
    bad = [p for p in STAGE.rglob("*") if p.is_file() and (p.suffix.lower() in FORBIDDEN_EXT
           or FORBIDDEN_PARTS & set(p.relative_to(STAGE).parts[:-1]))]
    assert not bad, bad
    files = [p for p in STAGE.rglob("*") if p.is_file()]
    size = sum(p.stat().st_size for p in files)
    print(f"[bundle] staged {len(files)} files, {size / 1e6:.1f} MB, audit clean")
    return size


def pack(size):
    UPLOAD.mkdir(parents=True, exist_ok=True)
    for old in UPLOAD.glob(NAME + "*.rar"):
        old.unlink()
    cmd = [RAR, "a", "-m5", "-ma5", "-s", "-md1g", "-rr3%", "-ep1", "-idq", "-y"]
    if size > 490e6:
        cmd.append("-v490m")
    cmd += [str(UPLOAD / (NAME + ".rar")), str(STAGE)]
    subprocess.run(cmd, check=True)
    subprocess.run([RAR, "t", "-idq", str(sorted(UPLOAD.glob(NAME + "*.rar"))[0])], check=True)
    for p in sorted(UPLOAD.glob(NAME + "*.rar")):
        print(f"[bundle] {p} {p.stat().st_size / 1e6:.1f} MB (tested)")


if __name__ == "__main__":
    stage()
    s = audit()
    if "--no-rar" not in sys.argv:
        pack(s)
