#!/usr/bin/env python3
"""One-command ComfyUI setup for GUO's art workflows (images, audio, 3D, voice).

The end-user case: ComfyUI is installed, but its models/ and custom_nodes/ are
empty. This tool reads tools/comfy/models.json (every model file the GUO
workflows reference, plus the custom node packs they need) and fills the gaps:

    python tools/comfy/setup_studio.py --check
    python tools/comfy/setup_studio.py [--comfy-dir DIR] [--url URL]

--check reports only (green/red table, exit 1 when anything is missing) and
touches nothing: no clones, no downloads, no server contact. Default mode
clones missing node packs (git clone, skipped when the folder exists),
downloads missing model files (huggingface_hub with resume; plain resumed
HTTP for the one CivitAI file), then validates every workflow in
tools/comfy/workflows/ against the server's /object_info -- the same check
run.py's `run --validate` does (unknown node types are printed MISSING).

ComfyUI is found via --comfy-dir, else env UO_COMFY_DIR, else the guesses in
models.json (D:/ComfyUIPortable/ComfyUI, C:/ComfyUI, ...). The server URL
resolves like every other tool here (env UO_COMFY_URL -> config.local.bat ->
config.bat, default http://127.0.0.1:8188; see tools/guo/config.py).

Files already on disk are never re-downloaded. Qwen3 TTS/ASR weights and
MoGe-3 are node/tool-managed (downloaded on first use); they are check-only.
Nothing here writes to the UO client install.
"""

from __future__ import annotations

import argparse
import json
import os
import shutil
import subprocess
import sys
import urllib.request
from pathlib import Path

HERE = Path(__file__).resolve().parent
sys.path.insert(0, str(HERE.parents[1]))
sys.path.insert(0, str(HERE))

try:
    from guo.config import load_config  # noqa: E402
except Exception:  # run outside the repo layout; fall back to defaults
    load_config = None  # type: ignore[assignment]

try:
    from run import api_get, resolve_url  # noqa: E402  (same pattern as run.py --validate)
except Exception:
    api_get = None  # type: ignore[assignment]
    resolve_url = None  # type: ignore[assignment]

MANIFEST = HERE / "models.json"
WORKFLOWS = HERE / "workflows"
CHUNK = 1 << 20


def use_color() -> bool:
    return sys.stdout.isatty() and os.environ.get("NO_COLOR") is None


def mark(ok: bool) -> str:
    if not use_color():
        return "[ok]" if ok else "[MISSING]"
    return ("\x1b[32m[ok]\x1b[0m" if ok else "\x1b[31m[MISSING]\x1b[0m")


def load_manifest(path: Path) -> dict:
    try:
        return json.loads(path.read_text(encoding="utf-8"))
    except Exception as ex:
        print(f"[studio] FATAL: cannot read {path}: {ex}")
        raise SystemExit(2)


def looks_like_comfy(d: Path) -> bool:
    try:
        names = {p.name for p in d.iterdir()}
    except OSError:
        return False
    return bool(names & {"models", "custom_nodes", "main.py", "input", "output"})


def resolve_comfy_dir(manifest: dict, override: str | None) -> Path | None:
    if override:
        return Path(override)
    env = os.environ.get("UO_COMFY_DIR", "").strip()
    if env:
        return Path(env)
    section = manifest.get("comfy_dir", {})
    for guess in section.get("guesses", []):
        if looks_like_comfy(Path(guess)):
            return Path(guess)
    return None


def model_candidates(comfy: Path, entry: dict) -> list[Path]:
    """Where this entry's file may already live (file + aliases, exact names)."""
    dest = entry.get("dest", "")
    if dest.startswith("("):  # reference-only entry (MoGe-3): not on this disk
        return []
    base = comfy / "models" / dest
    names = [entry.get("file", "")] + list(entry.get("aliases") or [])
    return [base / n for n in names if n]


def entry_present(comfy: Path, entry: dict) -> Path | None:
    """The on-disk path when this entry is satisfied, else None."""
    if entry.get("source") == "node-managed":
        # A whole repo the node pack fetches itself (Qwen3-TTS/ASR weights):
        # any non-empty folder with the expected name counts.
        d = comfy / "models" / entry.get("dest", "") / entry.get("file", "")
        try:
            if d.is_dir() and any(d.iterdir()):
                return d
        except OSError:
            pass
        return None
    for cand in model_candidates(comfy, entry):
        try:
            if cand.is_file() and cand.stat().st_size > 0:
                return cand
        except OSError:
            continue
    return None


def cmd_check(comfy: Path | None, manifest: dict) -> int:
    if comfy is None or not comfy.is_dir():
        print(f"{mark(False)} ComfyUI dir: NOT FOUND")
        print("[studio] set UO_COMFY_DIR or pass --comfy-dir; guesses tried:")
        for g in manifest.get("comfy_dir", {}).get("guesses", []):
            print(f"[studio]   - {g}")
        return 1
    print(f"{mark(True)} ComfyUI dir: {comfy}")
    missing = 0

    print("--- custom nodes ---")
    nodes_dir = comfy / "custom_nodes"
    for pack in manifest.get("custom_nodes", []):
        d = nodes_dir / pack.get("dir", "")
        ok = d.is_dir() and any(True for _ in d.iterdir())
        missing += not ok
        print(f"{mark(ok)} {pack.get('dir', '?'):22s} {pack.get('repo', '')}")

    print("--- models ---")
    for entry in manifest.get("models", []):
        if entry.get("dest", "").startswith("("):
            print(f"[ref ] {entry.get('file', '?'):44s} {entry.get('url', '')} (managed by tools/moge, not checked)")
            continue
        found = entry_present(comfy, entry) if comfy else None
        if found is not None:
            try:
                if found.is_dir():
                    n = sum(1 for _ in found.iterdir())
                    detail = f"{n} file(s)"
                else:
                    size = found.stat().st_size
                    detail = f"{size / (1 << 20):,.0f} MB"
            except OSError:
                detail = "present"
            print(f"{mark(True)} {entry.get('file', '?'):44s} {entry.get('dest', '')} ({detail})")
        else:
            missing += 1
            auto = " (auto-downloads on first use)" if entry.get("source") == "node-managed" else ""
            print(f"{mark(False)} {entry.get('file', '?'):44s} {entry.get('dest', '')}{auto}")

    print(f"[studio] {'ALL PRESENT' if missing == 0 else f'{missing} item(s) missing'}")
    return 1 if missing else 0


def git_clone(repo: str, dest: Path) -> bool:
    if dest.is_dir():
        if any(True for _ in dest.iterdir()):
            print(f"[studio] nodes {dest.name}: already there, skipping clone")
            return True
        shutil.rmtree(dest, ignore_errors=True)
    dest.parent.mkdir(parents=True, exist_ok=True)
    print(f"[studio] nodes {dest.name}: git clone {repo}")
    r = subprocess.run(["git", "clone", "--depth", "1", repo, str(dest)])
    if r.returncode != 0:
        print(f"[studio] nodes {dest.name}: clone FAILED (exit {r.returncode})")
        return False
    req = dest / "requirements.txt"
    if req.is_file():
        print(f"[studio] nodes {dest.name}: now install its deps yourself, e.g.")
        print(f"[studio]   <comfy-python> -m pip install -r {req}")
        print("[studio]   (portable: ComfyUI path\\python_embeded\\python.exe -m pip install -r ...)")
    print(f"[studio] nodes {dest.name}: cloned")
    return True


def parse_hf_url(url: str) -> tuple[str, str] | None:
    """https://huggingface.co/<repo>/resolve/<rev>/<path> -> (repo, path)."""
    marker = "huggingface.co/"
    if marker not in url:
        return None
    rest = url.split(marker, 1)[1]
    parts = rest.split("/resolve/", 1)
    if len(parts) != 2:
        return None
    repo, after = parts
    path = after.split("/", 1)[1] if "/" in after else ""
    return (repo, path) if repo and path else None


def download_hf(entry: dict, dest: Path) -> bool:
    try:
        from huggingface_hub import hf_hub_download  # type: ignore
    except ImportError:
        print("[studio] FATAL: huggingface_hub is missing (pip install huggingface_hub) -- cannot download")
        return False
    parsed = parse_hf_url(entry["url"])
    if parsed is None:
        print(f"[studio] {entry['file']}: not a /resolve/ URL, cannot map to a repo file")
        return False
    repo, path = parsed
    dest.parent.mkdir(parents=True, exist_ok=True)
    print(f"[studio] {entry['file']}: downloading {repo}/{path}")
    try:
        got = hf_hub_download(repo_id=repo, filename=path,
                              local_dir=str(dest.parent),
                              local_dir_use_symlinks=False,
                              resume_download=True)
    except Exception as ex:
        print(f"[studio] {entry['file']}: download FAILED ({type(ex).__name__}: {ex})")
        return False
    # hf_hub_download caches by repo layout; copy to the wanted filename.
    src = Path(got)
    if src.resolve() != dest.resolve():
        shutil.copyfile(src, dest)
    print(f"[studio] {entry['file']}: done ({dest.stat().st_size / (1 << 20):,.0f} MB)")
    return True


def download_http(entry: dict, dest: Path) -> bool:
    """Resumed plain-HTTPS download (the CivitAI file). Skips when present."""
    dest.parent.mkdir(parents=True, exist_ok=True)
    tmp = dest.with_suffix(dest.suffix + ".part")
    have = tmp.stat().st_size if tmp.is_file() else 0
    req = urllib.request.Request(entry["url"], headers={"User-Agent": "GUO-setup-studio/1"})
    if have > 0:
        req.add_header("Range", f"bytes={have}-")
        print(f"[studio] {entry['file']}: resuming at {have / (1 << 20):,.0f} MB")
    else:
        print(f"[studio] {entry['file']}: downloading {entry['url']}")
    try:
        with urllib.request.urlopen(req, timeout=60) as r, open(tmp, "ab" if have else "wb") as f:
            total = r.headers.get("Content-Length")
            expect = (have + int(total)) if total else None
            n = have
            while True:
                buf = r.read(CHUNK)
                if not buf:
                    break
                f.write(buf)
                n += len(buf)
                if expect:
                    print(f"\r[studio] {entry['file']}: {n / (1 << 20):,.0f}/{expect / (1 << 20):,.0f} MB",
                          end="", flush=True)
            if expect:
                print()
    except Exception as ex:
        print(f"\n[studio] {entry['file']}: download FAILED ({type(ex).__name__}: {ex}) -- rerun to resume")
        return False
    shutil.move(str(tmp), str(dest))
    print(f"[studio] {entry['file']}: done ({dest.stat().st_size / (1 << 20):,.0f} MB)")
    return True


def cmd_install(comfy: Path, manifest: dict, url: str) -> int:
    failures = 0

    print("--- custom nodes ---")
    nodes_dir = comfy / "custom_nodes"
    for pack in manifest.get("custom_nodes", []):
        if not git_clone(pack["repo"], nodes_dir / pack["dir"]):
            failures += 1

    print("--- models ---")
    for entry in manifest.get("models", []):
        if entry.get("source") == "node-managed":
            found = entry_present(comfy, entry)
            print(f"[studio] {entry['file']}: "
                  f"{'present, skipping' if found else 'skipped (pack auto-downloads it on first run)'}")
            continue
        if entry.get("dest", "").startswith("("):
            print(f"[studio] {entry['file']}: reference entry, skipping (tools/moge manages it)")
            continue
        found = entry_present(comfy, entry)
        if found is not None:
            print(f"[studio] {entry['file']}: already there ({found}), skipping")
            continue
        dest = comfy / "models" / entry["dest"] / entry["file"]
        if entry.get("source") == "civitai":
            ok = download_http(entry, dest)
        else:
            ok = download_hf(entry, dest)
        failures += not ok

    if failures:
        print(f"[studio] {failures} item(s) FAILED; fix and rerun (present files are never re-fetched)")
        return 1
    return cmd_validate(url)


def workflow_node_types(path: Path) -> set[str]:
    """All class_types a workflow needs: UI format converted like run.py does."""
    raw = json.loads(path.read_text(encoding="utf-8"))
    if isinstance(raw, dict) and "nodes" in raw:
        from uiconv import convert_file  # noqa: E402  (same conversion run.py run --validate uses)
        wf = convert_file(str(path))
    else:
        wf = raw
    return {n.get("class_type") for n in wf.values()
            if isinstance(n, dict) and n.get("class_type")}


def cmd_validate(url: str) -> int:
    """Same check as run.py cmd_validate: workflow node types vs /object_info."""
    if api_get is None:
        print("[studio] validate FAILED: could not import run.py's api_get")
        return 1
    try:
        info = api_get(url + "/object_info", timeout=120)
    except Exception as ex:
        print(f"[studio] server at {url} is not answering ({type(ex).__name__}: {ex})")
        print("[studio] start ComfyUI, then rerun this tool (files above are all in place)")
        return 2
    files = sorted(WORKFLOWS.glob("*.json"))
    all_missing: set[str] = set()
    for path in files:
        try:
            types = workflow_node_types(path)
        except Exception as ex:
            print(f"[studio] {path.name}: conversion FAILED ({ex})")
            return 1
        missing = sorted(t for t in types if t not in info)
        all_missing.update(missing)
        print(f"[studio] {path.name}: {len(types)} node type(s), {len(missing)} unknown")
        for t in missing:
            print(f"[studio] MISSING: {t} (in {path.name})")
    if all_missing:
        print(f"[studio] {len(all_missing)} unknown type(s): update ComfyUI / install the pack that provides them")
        return 1
    print(f"[studio] validate OK: {len(files)} workflow(s), every node type known to {url}")
    return 0


def main(argv: list[str] | None = None) -> int:
    ap = argparse.ArgumentParser(description="Set up ComfyUI for GUO's art workflows (models + custom nodes).")
    ap.add_argument("--check", action="store_true",
                    help="report only: green/red table of what is present, exit 1 when anything is missing")
    ap.add_argument("--comfy-dir", default=None,
                    help="ComfyUI root (holds models/ and custom_nodes/). Default: UO_COMFY_DIR, then stock install guesses")
    ap.add_argument("--manifest", default=str(MANIFEST), help="models.json to work from")
    ap.add_argument("--url", default=None, help="ComfyUI base URL (default: UO_COMFY_URL)")
    args = ap.parse_args(argv)

    manifest = load_manifest(Path(args.manifest))
    comfy = resolve_comfy_dir(manifest, args.comfy_dir)

    if args.check:
        return cmd_check(comfy, manifest)

    if comfy is None or not comfy.is_dir():
        print(f"[studio] ComfyUI dir not found (tried UO_COMFY_DIR and stock guesses).")
        print("[studio] pass --comfy-dir <path to the folder holding models/ and custom_nodes/>")
        return 1
    if not looks_like_comfy(comfy):
        print(f"[studio] {comfy} does not look like ComfyUI (no models/, custom_nodes/ or main.py). Abort.")
        return 1
    if resolve_url is not None:
        url = resolve_url(args.url)
    elif load_config is not None:
        try:
            url = (args.url or load_config().comfy_url).rstrip("/")
        except Exception:
            url = (args.url or "http://127.0.0.1:8188").rstrip("/")
    else:
        url = (args.url or "http://127.0.0.1:8188").rstrip("/")
    print(f"[studio] ComfyUI dir: {comfy}")
    print(f"[studio] server: {url}")
    return cmd_install(comfy, manifest, url)


if __name__ == "__main__":
    raise SystemExit(main())
