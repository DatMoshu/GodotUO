"""Install/reconnect the editor-only SpriteMotion workspace. No system-wide installs."""
import argparse
import hashlib
import json
import os
from pathlib import Path
import shutil
import socket
import subprocess
import sys
import time
import urllib.error
import urllib.request
import zipfile

ROOT = Path(__file__).resolve().parents[2]
sys.path.insert(0, str(ROOT / 'tools'))
from guo.config import parse_config_bat

REVISION = 'eea81693f7f2ef5dcaf80a79183aa23a8bc20a69'
CEF_VERSION = '1.16.2'
EDITOR_HASH = 'ab2a84770fcf1ecbf753b10a9307868f851f309ff9516ab6c5f75311efb75b36'
CEF_HASH = 'e8f7a24486e77156862baf5a4433e4b9c9da540fa26205be3fd21e1a027b4117'
CACHE = ROOT / 'build/spritemotion'


def settings():
    path = ROOT / 'launchers/_shared/config.local.bat'
    local = parse_config_bat(path, dict(os.environ)) if path.exists() else dict(os.environ)
    return parse_config_bat(ROOT / 'launchers/_shared/config.bat', local)


def say(message):
    print('[SpriteMotion] ' + message, flush=True)


def digest(path):
    with path.open('rb') as stream:
        return hashlib.file_digest(stream, 'sha256').hexdigest()


def download(url, path, expected=None):
    if path.is_file() and (expected is None or digest(path) == expected): return
    temp = path.with_suffix('.partial')
    say('Downloading ' + path.name)
    with urllib.request.urlopen(url, timeout=60) as response, temp.open('wb') as out:
        shutil.copyfileobj(response, out, 1024 * 1024)
    if expected and digest(temp) != expected:
        raise ValueError('Download checksum mismatch: ' + path.name)
    temp.replace(path)


def unpack(archive, destination, cef=False):
    destination = destination.resolve()
    with zipfile.ZipFile(archive) as z:
        for member in z.infolist():
            if member.is_dir(): continue
            name = member.filename.replace('\\', '/')
            if cef:
                marker = 'addons/godot_cef/'
                if marker not in name: continue
                name = name.split(marker, 1)[1]
                if name.startswith('bin/') and not name.startswith('bin/x86_64-pc-windows-msvc/'): continue
            else:
                name = name.split('/', 1)[1] if '/' in name else ''
            if not name: continue
            target = (destination / name).resolve()
            if not target.is_relative_to(destination): raise ValueError('Unsafe archive path')
            target.parent.mkdir(parents=True, exist_ok=True)
            with z.open(member) as src, target.open('wb') as out: shutil.copyfileobj(src, out)


def install_cef(archive=None):
    if sys.platform != 'win32' or os.environ.get('PROCESSOR_ARCHITECTURE','').lower() not in ('amd64','x86_64'):
        raise ValueError('Embedded Fit Lab currently supports Windows x64; other GUO clients are unchanged.')
    addon = ROOT / 'godot/GUO/addons/godot_cef'
    marker = addon / '.guo-version'
    library = addon/'bin/x86_64-pc-windows-msvc/gdcef.dll'
    patch = Path(__file__).parent/'runtime/gdcef.dll'
    if not patch.is_file() or digest(patch) != EDITOR_HASH:
        raise ValueError('The packaged editor browser library is missing or damaged. Restore tools/spritemotion/runtime from the GUO distribution.')
    if marker.is_file() and marker.read_text() == CEF_VERSION and library.is_file():
        if digest(library) != EDITOR_HASH: shutil.copy2(patch, library)
        return
    archive = Path(archive) if archive else CACHE / f'godot-cef-{CEF_VERSION}.zip'
    download(f'https://github.com/dsh0416/godot-cef/releases/download/v{CEF_VERSION}/godot_cef-v{CEF_VERSION}.zip', archive, CEF_HASH)
    say('Installing embedded browser')
    unpack(archive, addon, cef=True)
    shutil.copy2(patch, library)
    marker.write_text(CEF_VERSION)


def install_sprite(config):
    override = config.get('SPRITEMOTION_ROOT')
    if override:
        root = Path(override).resolve()
        if not (root/'tools/fit-lab/run.py').is_file(): raise ValueError('Configured SpriteMotion checkout is missing Fit Lab')
        return root
    root = CACHE / ('source-' + REVISION[:12])
    if not (root/'.guo-installed').exists():
        archive = CACHE / f'source-{REVISION}.zip'
        download(f'https://github.com/DatMoshu/SpriteMotion/archive/{REVISION}.zip', archive)
        unpack(archive, root)
        (root/'.guo-installed').write_text(REVISION)
    return root


def python_for(root):
    existing = root / '.venvs/spritemotion/Scripts/python.exe'
    if existing.is_file(): return existing
    python = CACHE / 'python/Scripts/python.exe'
    if not python.exists(): subprocess.run([sys.executable,'-m','venv',str(python.parents[1])],check=True)
    stamp = CACHE/'python/source.txt'
    if not stamp.exists() or stamp.read_text() != str(root):
        say('Preparing SpriteMotion Python environment')
        subprocess.run([str(python),'-m','pip','install',str(root)],check=True)
        stamp.write_text(str(root))
    return python


def read_url(url):
    with urllib.request.urlopen(url, timeout=2) as response: return json.load(response)


def start(root, python, config):
    catalogs = sorted((root/'workspace/ultima-online/fit-lab').glob('*/manifest.json'))
    pack = config.get('SPRITEMOTION_FIT_PACK') or (catalogs[0].parent.name if len(catalogs)==1 else '')
    if not pack:
        raise ValueError('Choose a prepared asset pack in GUO settings (SPRITEMOTION_FIT_PACK). No fitting model or private asset pack is included in the public code download.')
    port = int(config.get('SPRITEMOTION_PORT','8774'))
    if not 1024 <= port <= 65535: raise ValueError('Invalid Fit Lab port')
    if Path(pack).name != pack or pack in ('.','..'): raise ValueError('Invalid pack identifier')
    if not (root/'workspace/ultima-online/fit-lab'/pack/'manifest.json').is_file():
        raise ValueError('This pack needs its model export before fitting. Existing exports are reused automatically.')
    url = f'http://127.0.0.1:{port}/'
    try:
        manifest = read_url(url+'data/manifest.json')
    except (urllib.error.URLError, TimeoutError, OSError): manifest = None
    if manifest is not None:
        if manifest.get('pack') != pack: raise ValueError('The Fit Lab port belongs to another pack. Choose another port.')
        say('Reconnected to the running Fit Lab')
        return url
    with socket.socket() as probe:
        probe.settimeout(1)
        if probe.connect_ex(('127.0.0.1', port)) == 0:
            raise ValueError('The selected port is occupied by an unrecognized service. Choose another port.')
    env = os.environ.copy()
    for key in ('SPRITEMOTION_SIDECAR','SPRITEMOTION_BLENDER','SPRITEMOTION_UO_SOURCE'):
        if config.get(key): env[key] = config[key]
    log = (CACHE/'service.log').open('ab')
    try:
        subprocess.Popen([str(python),str(root/'tools/fit-lab/run.py'),'serve','--pack',pack,'--port',str(port),'--no-browser'],
                         cwd=root,env=env,stdout=log,stderr=log,creationflags=subprocess.CREATE_NO_WINDOW)
    finally: log.close()
    for _ in range(60):
        time.sleep(.25)
        try:
            manifest = read_url(url+'data/manifest.json')
            if manifest.get('pack')==pack: return url
        except (urllib.error.URLError,TimeoutError,OSError): pass
    raise ValueError('Fit Lab did not start. See build/spritemotion/service.log.')


def main():
    ap=argparse.ArgumentParser(description=__doc__)
    ap.add_argument('command',choices=['open','install-browser'])
    ap.add_argument('--cef-archive')
    args=ap.parse_args()
    CACHE.mkdir(parents=True,exist_ok=True)
    # An OS lock is released even after a crash. Prevent duplicate setup/start from editor reloads.
    import msvcrt
    with (CACHE/'setup.lock').open('a+b') as lock:
        lock.seek(0);lock.write(b'0');lock.flush();lock.seek(0)
        try: msvcrt.locking(lock.fileno(),msvcrt.LK_NBLCK,1)
        except OSError: raise ValueError('Fit Lab setup is already running. Reconnect when it finishes.')
        install_cef(args.cef_archive)
        if args.command=='install-browser': return
        config=settings();root=install_sprite(config);url=start(root,python_for(root),config)
        print(json.dumps({'url':url,'extension':'res://addons/godot_cef/godot_cef.gdextension'}),flush=True)


if __name__=='__main__':
    try: main()
    except Exception as error: say(str(error));sys.exit(1)
