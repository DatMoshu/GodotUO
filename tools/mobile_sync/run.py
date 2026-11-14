"""Bounded two-GUO-client equipment/movement diagnosis on the configured local dev shard."""
from __future__ import annotations

import argparse
import hashlib
import json
import ipaddress
import os
from pathlib import Path
import shutil
import socket
import subprocess
import sys
import time

sys.path.insert(0, str(Path(__file__).resolve().parents[1]))
from guo.config import load_config
from analyze import analyze


def main():
    parser = argparse.ArgumentParser(description=__doc__)
    parser.add_argument('--stage', type=Path, required=True, help='External disposable source/build folder')
    parser.add_argument('--out', type=Path, required=True, help='Fresh evidence data folder')
    parser.add_argument('--build-only', action='store_true')
    parser.add_argument('--skip-build', action='store_true')
    parser.add_argument('--renderer', choices=('depth', 'canvas'), default='depth')
    parser.add_argument('--laps', type=int, choices=(1, 2), default=2)
    parser.add_argument('--labs', action='store_true', help='Build the optional Labs diagnostics')
    parser.add_argument('--gizmos', action='store_true', help='Open and capture each Labs mobile inspector')
    parser.add_argument('--negative-control', action='store_true', help='Remove only the late flush in the disposable snapshot; expected to fail')
    args = parser.parse_args()
    if args.gizmos and not args.labs:
        parser.error('--gizmos requires --labs')
    if args.negative_control and args.skip_build:
        parser.error('Negative control must rebuild its disposable snapshot')
    cfg = load_config()
    stage, out = args.stage.resolve(), args.out.resolve()
    for p in (stage, out):
        if p.is_relative_to(cfg.root.resolve()):
            parser.error('Generated files must stay outside the checkout')
    if out.exists() and any(out.iterdir()):
        parser.error('Use a fresh, empty evidence folder')
    if stage.exists() and any(stage.iterdir()):
        marker = stage / 'README.txt'
        if not marker.is_file() or not marker.read_text().startswith('Disposable mobile_sync build.'):
            parser.error('Stage must be empty or an existing mobile_sync snapshot')
    if len(cfg.shard_gm_accounts) < 2 or not cfg.shard_gm_password:
        parser.error('Need two configured dev GM accounts; use launchers/shard/configure.bat')
    if cfg.shard_host != 'localhost' and not ipaddress.ip_address(cfg.shard_host).is_loopback:
        parser.error('This admin-command probe requires a loopback dev shard')
    stage.mkdir(parents=True, exist_ok=True)
    out.mkdir(parents=True, exist_ok=True)
    env = {k: v for k, v in os.environ.items() if not k.startswith(('GUO_PERF', 'GUO_SHOT', 'GUO_ZOOM',
           'GUO_RECOVERY', 'GUO_ATLAS', 'GUO_MINIMAP_TEXTURE', 'GUO_DW', 'GUO_MOBILE'))}
    env.update(UO_CLIENT_DATA=str(cfg.client_data), UO_CLIENT_VERSION=cfg.client_version,
               UO_SHARD_HOST=cfg.shard_host, UO_SHARD_PORT=str(cfg.shard_port),
               UO_SHARD_GM_ACCOUNTS=','.join(cfg.shard_gm_accounts), UO_SHARD_GM_PASSWORD=cfg.shard_gm_password)
    # The shared config resolves the main checkout's read-only reference.
    env['UO_UPSTREAM_DIR'] = str(cfg.upstream.parent)
    if not args.skip_build:
        files = subprocess.check_output(['git', 'ls-files', 'godot', 'servers', 'tools/msbuild', 'tools/plugin_host'], cwd=cfg.root, text=True).splitlines()
        files += ['godot/GUO/src/Bootstrap/MobileRenderTrace.cs', 'godot/GUO/src/Bootstrap/MobileSyncProbe.cs']
        for rel in sorted(set(files)):
            source, target = cfg.root / rel, stage / rel
            if source.is_file():
                target.parent.mkdir(parents=True, exist_ok=True)
                shutil.copy2(source, target)
        if args.negative_control:
            controller = stage / 'godot/GUO/src/Client/GameController.cs'
            source = controller.read_text(encoding='utf-8')
            needle = 'TextureAtlas.FlushAll();\n                GUO.Host.MobileRenderTrace.CompleteFrame();'
            if source.count(needle) != 1:
                raise RuntimeError('Late-flush seam changed; refusing negative-control edit')
            controller.write_text(source.replace(needle, 'GUO.Host.MobileRenderTrace.CompleteFrame();'), encoding='utf-8')
        (stage / 'README.txt').write_text('Disposable mobile_sync build. Regenerate with tools/mobile_sync/run.py. Retain only through fix verification. No client data copied.\n')
        with (out / 'build.log').open('w') as log:
            result = subprocess.run(['dotnet', 'build', str(stage / 'godot/GUO/GUO.csproj'), '-v:q', '--nologo',
                                     '-p:GuoLabs=' + str(args.labs).lower()], env=env, stdout=log, stderr=subprocess.STDOUT)
        if result.returncode:
            print('Build failed:', out / 'build.log')
            return result.returncode
        with (out / 'import.log').open('w') as log:
            result = subprocess.run([str(cfg.godot_console_exe), '--headless', '--path', str(stage / 'godot/GUO'), '--editor', '--import'], env=env, stdout=log, stderr=subprocess.STDOUT, timeout=180)
        if result.returncode:
            print('Import failed:', out / 'import.log')
            return result.returncode
        manifest = {
            'head': subprocess.check_output(['git', 'rev-parse', 'HEAD'], cwd=cfg.root, text=True).strip(),
            'negative_control': args.negative_control,
            'labs': args.labs,
            'assembly_sha256': hashlib.sha256((stage / 'godot/GUO/.godot/mono/temp/bin/Debug/GUO.dll').read_bytes()).hexdigest(),
            'sources': {rel: hashlib.sha256((stage / rel).read_bytes()).hexdigest()
                        for rel in sorted(set(files)) if (stage / rel).is_file()},
        }
        (stage / 'source-manifest.json').write_text(json.dumps(manifest, indent=2))
    shutil.copy2(stage / 'source-manifest.json', out / 'source-manifest.json')
    built = json.loads((stage / 'source-manifest.json').read_text())
    if args.gizmos and not built.get('labs', False):
        parser.error('The frozen snapshot has no Labs inspector; rebuild with --labs')
    (out / 'settings.json').write_text(json.dumps({'renderer': args.renderer, 'reuse': True, 'laps': args.laps,
                                                  'negative_control': built.get('negative_control', False)}, indent=2))
    if args.build_only:
        return 0
    with socket.create_connection((cfg.shard_host, cfg.shard_port), timeout=3):
        pass
    if any((out / (r + '.ready')).exists() for r in ('a', 'b')):
        parser.error('Use a fresh output folder; stale barrier files are unsafe')
    procs = []
    try:
        for i, role in enumerate(('a', 'b')):
            home = stage / ('home-' + role)
            (home / 'cache').mkdir(parents=True, exist_ok=True)
            child = dict(env, UO_CACHE_DIR=str(home / 'cache'), UO_WORKSPACE_DIR=str(home / 'workspace'),
                         GUO_MOBILE_TRACE=str(out / (role + '.jsonl')), GUO_MOBILE_PROBE=role,
                         GUO_MOBILE_LAPS=str(args.laps), GUO_DW_REUSE='1', UO_BUILD_DIR=str(out),
                         GUO_MOBILE_GIZMOS='1' if args.gizmos else '0')
            cmd = [str(cfg.godot_console_exe), '--path', str(stage / 'godot/GUO'), '--', '--play', '--no-focus',
                   '--account', cfg.shard_gm_accounts[i], '--character', cfg.shard_gm_accounts[i].capitalize(),
                   '--window-size', '1280x720', '--window-position', f'{i * 640},0', '--silent']
            if args.renderer == 'canvas':
                cmd.append('--canvas-world')
            with (out / (role + '.log')).open('w') as log:
                procs.append(subprocess.Popen(cmd, env=child, stdout=log, stderr=subprocess.STDOUT,
                                              creationflags=subprocess.CREATE_NO_WINDOW if os.name == 'nt' else 0))
        deadline = time.monotonic() + 480
        while any(p.poll() is None for p in procs) and time.monotonic() < deadline:
            time.sleep(0.5)
        codes = [p.poll() for p in procs]
        (out / 'result.json').write_text(json.dumps({'exit_codes': codes}, indent=2))
        print('Two-client exits:', codes, 'evidence:', out)
        if codes != [0, 0]:
            return 1
        report = analyze(out)
        return 0 if all(row['passed'] for row in report.values()) else 1
    finally:
        for p in procs:
            if p.poll() is None:
                if os.name == 'nt':
                    subprocess.run(['taskkill', '/PID', str(p.pid), '/T', '/F'], capture_output=True)
                else:
                    p.kill()


if __name__ == '__main__':
    raise SystemExit(main())
