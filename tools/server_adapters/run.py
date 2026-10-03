# SPDX-License-Identifier: BSD-2-Clause
"""Generate and install backend-native GUO content packages without touching unrelated files."""
from __future__ import annotations
import argparse
import hashlib
import json
import os
import re
from pathlib import Path
import shutil
import sys
import tempfile

sys.path.insert(0, str(Path(__file__).resolve().parents[1]))
from server_adapters.adapter import CAPABILITIES, load, render, require
from guo import load_config

PATHS = {
    'servuo': {'Scripts/Custom/GUO/GUOContent.cs'},
    'runuo': {'Scripts/Custom/GUO/GUOContent.cs'},
    'pol': {'pkg/guo_content/config/itemdesc.cfg', 'pkg/guo_content/pkg.cfg'},
    'sphere': {'scripts/guo_content.scp'},
    'uox3': {'dfndata/items/guo_content.dfn'},
}
EXTRA = {'Data/GUO/server-content.json', 'Data/GUO/public/shard-content.json'}


def no_links(path):
    path = Path(os.path.abspath(path))
    for p in (path, *path.parents):
        require(not p.is_symlink() and not (hasattr(p, 'is_junction') and p.is_junction()), 'Linked adapter path refused: '+str(p))
    return path


def generate(export, backend, out, slots=None, existing_graphics=()):
    files = render(load(export), backend, slots, existing_graphics)
    out = no_links(out)
    protect_install(out)
    require(not out.exists(), 'Output already exists; use a new directory for a reviewable deployment')
    out.parent.mkdir(parents=True, exist_ok=True)
    temporary = Path(tempfile.mkdtemp(prefix='.guo-stage-', dir=out.parent))
    try:
        for relative, content in files.items():
            dest = temporary / relative; dest.parent.mkdir(parents=True, exist_ok=True); dest.write_text(content, encoding='utf-8', newline='\n')
        os.rename(temporary, out)
    finally:
        if temporary.exists():
            require(no_links(temporary).parent == out.parent and temporary.name.startswith('.guo-stage-'), 'Unexpected staging cleanup target')
            shutil.rmtree(temporary)
    return out


def protect_install(path):
    client = load_config().client_data
    if str(client) != '.' and client.is_dir():
        require(not path.resolve().is_relative_to(client.resolve()), 'The UO client installation is read-only')
    sources = Path(__file__).resolve().parents[2] / 'sources'
    require(not path.resolve().is_relative_to(sources), 'Upstream reference sources are read-only')


def install(bundle, shard, extra=None):
    shard = no_links(shard)
    require(shard.is_dir(), 'Server directory does not exist')
    protect_install(shard)
    lock = no_links(shard / 'Data/GUO/adapter-install.lock')
    lock.parent.mkdir(parents=True, exist_ok=True)
    try:
        descriptor = os.open(lock, os.O_CREAT | os.O_EXCL | os.O_WRONLY)
    except FileExistsError:
        raise ValueError('Another adapter installation is active; inspect a stale adapter-install.lock before removing it')
    try:
        os.write(descriptor, str(os.getpid()).encode('ascii'))
        return _install(bundle, shard, extra)
    finally:
        os.close(descriptor)
        lock.unlink()


def _install(bundle, shard, extra=None):
    """Stage and check all ownership before replacing any file; rollback if publication raises."""
    bundle = no_links(bundle); shard = no_links(shard)
    require(shard.is_dir(), 'Server directory does not exist')
    manifest = json.loads((bundle / 'adapter-manifest.json').read_text(encoding='utf-8'))
    backend = manifest['backend']; require(backend in PATHS, 'Unknown adapter backend')
    require(manifest['schema'] == 'guo/server-adapter@1' and set(manifest['files']) == PATHS[backend], 'Invalid adapter file list')
    if backend == 'sphere':
        existing = set()
        for script in (shard / 'scripts').rglob('*.scp'):
            if script.relative_to(shard).as_posix() in PATHS[backend]: continue
            for token in re.findall(r'^\s*\[ITEMDEF\s+([0-9][0-9a-fA-FxX]*)\s*\]', no_links(script).read_text(encoding='utf-8-sig', errors='replace'), re.I | re.M):
                existing.add(int(token, 16 if token.startswith('0') else 10))
        require(not (existing & set(manifest.get('sphere_new_graphics', []))), 'Sphere graphic base already exists; declare it with --sphere-existing-graphic before generating')
        require(set(manifest.get('sphere_existing_graphics', [])) <= existing, 'Required Sphere graphic base not found in scripts')
    payload = {}
    for name, expected in manifest['files'].items():
        data = no_links(bundle / name).read_bytes()
        require(hashlib.sha256(data).hexdigest() == expected, 'Bundle checksum differs: '+name)
        payload[name] = data
    extra = extra or {}; require(not extra or set(extra) == EXTRA, 'Publish the neutral export and client descriptor together'); payload.update(extra)
    marker = no_links(shard / 'Data/GUO/adapter-install.json')
    previous = json.loads(marker.read_text(encoding='utf-8')) if marker.exists() else None
    if previous:
        require(previous.get('backend') == backend, 'Cannot replace a different backend installation')
        require(not (set(previous['files']) & EXTRA) or set(extra) == EXTRA, 'Use shard_content deploy to update or roll back a published server/client deployment together')
    for name in payload:
        destination = no_links(shard / name)
        if destination.exists():
            require(previous is not None and name in previous['files'], 'Refusing to overwrite unowned file: '+name)
            require(hashlib.sha256(destination.read_bytes()).hexdigest() == previous['files'][name], 'Installed file was edited; preserve/merge it before deploying: '+name)
    # Keep a revision backup for operators; no world saves are changed by installation.
    revisions = no_links(shard / 'Data/GUO/revisions'); revisions.mkdir(parents=True, exist_ok=True)
    transaction = Path(tempfile.mkdtemp(prefix='deploy-', dir=revisions))
    originals = {}
    for name in [*payload, 'Data/GUO/adapter-install.json']:
        target = no_links(shard / name); originals[name] = target.read_bytes() if target.exists() else None
        if originals[name] is not None:
            backup = transaction / name; backup.parent.mkdir(parents=True, exist_ok=True); backup.write_bytes(originals[name])
    installed = dict(manifest); installed['files'] = {k: hashlib.sha256(v).hexdigest() for k,v in payload.items()}
    payload['Data/GUO/adapter-install.json'] = (json.dumps(installed, indent=2)+'\n').encode('utf-8')
    written = []
    def replace(name, data):
        target = no_links(shard / name); target.parent.mkdir(parents=True, exist_ok=True)
        fd, temporary = tempfile.mkstemp(prefix='.guo-', dir=target.parent)
        try:
            with os.fdopen(fd, 'wb') as stream: stream.write(data)
            os.replace(temporary, target)
        finally:
            if os.path.exists(temporary): os.unlink(temporary)
    try:
        for name, data in payload.items(): replace(name, data); written.append(name)
    except BaseException:
        for name in reversed(written):
            if originals[name] is None: (shard / name).unlink()
            else: replace(name, originals[name])
        raise
    return marker


def main():
    parser=argparse.ArgumentParser(description=__doc__)
    sub=parser.add_subparsers(dest='command', required=True)
    sub.add_parser('capabilities')
    g=sub.add_parser('generate');g.add_argument('--backend',choices=CAPABILITIES,required=True);g.add_argument('--export',type=Path,required=True);g.add_argument('--out',type=Path,required=True);g.add_argument('--slots',type=Path);g.add_argument('--sphere-existing-graphic',action='append',type=lambda s:int(s,0),default=[])
    i=sub.add_parser('install');i.add_argument('--bundle',type=Path,required=True);i.add_argument('--shard-dir',type=Path,required=True)
    a=parser.parse_args()
    try:
        if a.command=='capabilities': print(json.dumps(CAPABILITIES,indent=2));return 0
        if a.command=='generate': print(generate(a.export,a.backend,a.out,load(a.slots) if a.slots else None,a.sphere_existing_graphic))
        else: print(install(a.bundle,a.shard_dir))
        return 0
    except (ValueError,KeyError,TypeError,OSError,json.JSONDecodeError) as error:
        print('[server_adapters] '+str(error),file=sys.stderr);return 2

if __name__=='__main__': raise SystemExit(main())
