#!/usr/bin/env python3
"""Pinned ComfyUI dependency installer; --check is offline and read-only.

See README.md and docs/data_formats.md. No generation or pip installation occurs.
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
import urllib.request
from pathlib import Path, PurePosixPath
from urllib.parse import urlparse

HERE = Path(__file__).resolve().parent
sys.path.insert(0, str(HERE.parent))
sys.path.insert(0, str(HERE))
from guo.config import load_config

MANIFEST = HERE / 'models.json'
WORKFLOWS = HERE / 'workflows'
CHUNK = 1 << 20
DOCTOR = 'launchers/comfy/doctor.bat (or doctor.sh)'


def child(root: Path, relative: str) -> Path:
    """Reject traversal, absolute paths, Windows drive paths and escaping links."""
    p = PurePosixPath(relative.replace('\\', '/'))
    if not relative or p.is_absolute() or '..' in p.parts or ':' in relative:
        raise ValueError(f'unsafe manifest path: {relative}')
    result = root / Path(*p.parts)
    if not result.resolve().is_relative_to(root.resolve()):
        raise ValueError(f'path escapes installation: {relative}')
    return result


def informational(entry: dict) -> bool:
    return entry.get('source') == 'node-managed' or entry.get('dest', '').startswith('(')


def validate_manifest(m: dict) -> None:
    if m.get('format') != 1:
        raise ValueError('unsupported manifest format')
    dummy = Path.cwd()
    for pack in m['custom_nodes']:
        child(dummy, pack['dir'])
        if not re.fullmatch('[0-9a-f]{40}', pack.get('commit', '')):
            raise ValueError(f"missing node commit: {pack['dir']}")
        if urlparse(pack['repo']).scheme != 'https':
            raise ValueError('node repository must use HTTPS')
    for entry in m['models']:
        if informational(entry):
            continue
        child(dummy, entry['dest'])
        for name in [entry['file'], *entry.get('aliases', [])]:
            child(dummy, name)
        if not re.fullmatch('[0-9a-f]{64}', entry.get('sha256') or ''):
            raise ValueError(f"missing model hash: {entry['file']}")
        if not isinstance(entry.get('size'), int) or entry['size'] <= 0:
            raise ValueError(f"missing model size: {entry['file']}")
        u = urlparse(entry['url'])
        if u.scheme != 'https':
            raise ValueError('model URL must use HTTPS')
        if entry.get('source') == 'huggingface':
            revision = entry.get('revision', '')
            if not re.fullmatch('[0-9a-f]{40}', revision) or f'/resolve/{revision}/' not in u.path:
                raise ValueError(f"model URL is not pinned: {entry['file']}")


def load_manifest(path: Path) -> dict:
    m = json.loads(path.read_text(encoding='utf-8'))
    validate_manifest(m)
    return m


def looks_like_comfy(d: Path) -> bool:
    return d.is_dir() and any((d / name).exists() for name in ('models', 'custom_nodes', 'main.py'))


def resolve_comfy_dir(manifest: dict, override: str | None) -> Path | None:
    configured = override or os.environ.get('UO_COMFY_DIR', '').strip() or load_config().comfy_dir
    if configured:
        return Path(configured)
    return next((Path(g) for g in manifest.get('comfy_dir', {}).get('guesses', [])
                 if looks_like_comfy(Path(g))), None)


def model_candidates(comfy: Path, entry: dict) -> list[Path]:
    base = child(comfy, 'models/' + entry['dest'])
    return [child(base, n) for n in [entry['file'], *entry.get('aliases', [])]]


def verified(path: Path, entry: dict) -> bool:
    if not path.is_file() or path.stat().st_size != entry['size']:
        return False
    with path.open('rb') as f:
        return hashlib.file_digest(f, 'sha256').hexdigest() == entry['sha256']


def entry_present(comfy: Path, entry: dict) -> Path | None:
    return next((p for p in model_candidates(comfy, entry) if verified(p, entry)), None)


def git_output(dest: Path, *args: str) -> str | None:
    # status normally refreshes the index; the offline doctor must not write it.
    r = subprocess.run(['git', '--no-optional-locks', '-C', str(dest), *args], capture_output=True, text=True)
    return r.stdout.strip() if r.returncode == 0 else None


def node_verified(dest: Path, commit: str) -> bool:
    # A normal folder below another repository must not count as this pack.
    return ((dest / '.git').exists() and git_output(dest, 'rev-parse', 'HEAD') == commit
            and git_output(dest, 'status', '--porcelain', '--untracked-files=normal') == '')


def cmd_check(comfy: Path | None, manifest: dict) -> int:
    if comfy is None or not looks_like_comfy(comfy):
        print('[MISSING] ComfyUI root. Set UO_COMFY_DIR or pass --comfy-dir.')
        return 1
    failures = 0
    print(f'[studio] Checking {comfy} (offline; models are read in full)')
    for pack in manifest['custom_nodes']:
        ok = node_verified(child(comfy, 'custom_nodes/' + pack['dir']), pack['commit'])
        failures += not ok
        print(f"[{'ok' if ok else 'MISSING/DRIFT'}] {pack['dir']} @ {pack['commit']}")
    for entry in manifest['models']:
        if informational(entry):
            print(f"[UNVERIFIED] {entry['file']}: managed outside this installer")
            continue
        ok = entry_present(comfy, entry) is not None
        failures += not ok
        print(f"[{'ok' if ok else 'MISSING/HASH'}] {entry['dest']}/{entry['file']}")
    for entry in manifest.get('references', []):
        print(f"[UNVERIFIED] {entry['file']}: managed outside this installer")
    print(f'[studio] {failures} required item(s) missing or unverified; first-use models above need separate verification.')
    if failures:
        print('[studio] Run launchers/comfy/setup_studio.bat (or setup_studio.sh); existing drift needs manual repair.')
    return int(bool(failures))


def git_clone(pack: dict, dest: Path) -> bool:
    if dest.exists():
        ok = node_verified(dest, pack['commit'])
        print(f"[studio] {dest.name}: {'verified' if ok else 'existing pack differs; repair manually (left untouched)'}")
        return ok
    dest.parent.mkdir(parents=True, exist_ok=True)
    # No checkout until the exact commit has been fetched. Never run node code.
    for args in (['init', str(dest)], ['-C', str(dest), 'remote', 'add', 'origin', pack['repo']],
                 ['-C', str(dest), 'fetch', '--depth', '1', 'origin', pack['commit']],
                 ['-C', str(dest), 'checkout', '--detach', 'FETCH_HEAD']):
        if subprocess.run(['git', *args]).returncode:
            print(f'[studio] {dest.name}: git failed; repair the partial folder manually.')
            return False
    if (dest / 'requirements.txt').is_file():
        print(f'[studio] Install requirements manually with ComfyUI Python: -m pip install -r {dest / "requirements.txt"}')
    return node_verified(dest, pack['commit'])


def download_http(entry: dict, dest: Path) -> bool:
    """Resume into .part; hash before atomic promotion; never replace existing files."""
    if dest.exists():
        return verified(dest, entry)
    dest.parent.mkdir(parents=True, exist_ok=True)
    tmp = dest.with_suffix(dest.suffix + '.part')
    if tmp.is_symlink() or tmp.resolve().parent != dest.resolve().parent:
        raise ValueError('unsafe partial download path')
    have = tmp.stat().st_size if tmp.exists() else 0
    if have == entry['size'] and verified(tmp, entry):
        tmp.rename(dest)
        return True
    if have >= entry['size']:
        print(f'[studio] {tmp.name}: invalid completed partial; remove it manually before retrying.')
        return False
    req = urllib.request.Request(entry['url'], headers={'User-Agent': 'GUO-setup-studio/1'})
    if have:
        req.add_header('Range', f'bytes={have}-')
    with urllib.request.urlopen(req, timeout=60) as response:
        status = response.status
        if status not in (200, 206):
            raise ValueError(f'unexpected HTTP status {status}')
        if status == 206:
            expected = f'bytes {have}-'
            if not response.headers.get('Content-Range', '').startswith(expected):
                raise ValueError('server returned a different resume offset')
        # A server ignoring Range sends the full file: truncate, never append.
        with tmp.open('ab' if status == 206 and have else 'wb') as out:
            while block := response.read(CHUNK):
                out.write(block)
                if out.tell() > entry['size']:
                    raise ValueError('download exceeds pinned size')
    if not verified(tmp, entry):
        print(f'[studio] {tmp.name}: size/SHA256 mismatch; final file was not created.')
        return False
    tmp.rename(dest)
    return True


def cmd_install(comfy: Path, manifest: dict, url: str) -> int:
    failures = 0
    for pack in manifest['custom_nodes']:
        failures += not git_clone(pack, child(comfy, 'custom_nodes/' + pack['dir']))
    for entry in manifest['models']:
        if informational(entry):
            print(f"[UNVERIFIED] {entry['file']}: skipped; managed separately")
            continue
        if entry_present(comfy, entry):
            print(f"[studio] {entry['file']}: SHA256 verified")
            continue
        candidates = model_candidates(comfy, entry)
        if any(p.exists() for p in candidates):
            print(f"[studio] {entry['file']}: existing file hash differs; repair manually (left untouched)")
            failures += 1
            continue
        try:
            failures += not download_http(entry, candidates[0])
        except (OSError, ValueError) as ex:
            print(f"[studio] {entry['file']}: download failed ({ex}); rerun to resume.")
            failures += 1
    for entry in manifest.get('references', []):
        print(f"[UNVERIFIED] {entry['file']}: skipped; managed separately")
    return 1 if failures else cmd_validate(url)


def workflow_node_types(path: Path) -> set[str]:
    raw = json.loads(path.read_text(encoding='utf-8'))
    if isinstance(raw, dict) and 'nodes' in raw:
        from comfy.uiconv import convert_file
        raw = convert_file(str(path))
    return {n['class_type'] for n in raw.values() if isinstance(n, dict) and n.get('class_type')}


def cmd_validate(url: str) -> int:
    from comfy.run import api_get
    try:
        info = api_get(url.rstrip('/') + '/object_info', timeout=30)
    except Exception as ex:
        print(f'[studio] ComfyUI is not answering ({type(ex).__name__}); start it, then run {DOCTOR} --validate.')
        return 2
    failed = False
    for path in sorted(WORKFLOWS.glob('*.json')):
        missing = sorted(workflow_node_types(path) - info.keys())
        failed |= bool(missing)
        print(f'[studio] {path.name}: ' + (f'MISSING {missing}' if missing else 'node types known'))
    return int(failed)


def main(argv: list[str] | None = None) -> int:
    ap = argparse.ArgumentParser(description=__doc__)
    ap.add_argument('--check', action='store_true', help='offline read-only integrity doctor')
    ap.add_argument('--validate', action='store_true', help='check server workflow node types; never queue work')
    ap.add_argument('--comfy-dir')
    ap.add_argument('--manifest', default=str(MANIFEST))
    ap.add_argument('--url')
    args = ap.parse_args(argv)
    try:
        manifest = load_manifest(Path(args.manifest))
        if (args.check or not args.validate) and manifest['custom_nodes'] and shutil.which('git') is None:
            raise ValueError('Git is missing; install Git and put git on PATH before running setup or the doctor')
        comfy = resolve_comfy_dir(manifest, args.comfy_dir)
        from comfy.run import resolve_url
        if args.check:
            result = cmd_check(comfy, manifest)
            if args.validate:
                result = max(result, cmd_validate(resolve_url(args.url)))
            return result
        if args.validate:
            return cmd_validate(resolve_url(args.url))
        if comfy is None or not looks_like_comfy(comfy):
            print('[studio] Set UO_COMFY_DIR or pass --comfy-dir to an existing ComfyUI root.')
            return 1
        return cmd_install(comfy, manifest, resolve_url(args.url))
    except (OSError, ValueError, KeyError, TypeError) as ex:
        print(f'[studio] Setup failed: {ex}. Run {DOCTOR}.')
        return 1


if __name__ == '__main__':
    raise SystemExit(main())
