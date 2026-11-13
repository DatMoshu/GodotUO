#!/usr/bin/env python3
"""Run synthetic terrain reader/dock checks in an isolated native editor project."""
from __future__ import annotations

import argparse
import hashlib
import json
import os
from pathlib import Path
import shutil
import subprocess
import sys

sys.path.insert(0, str(Path(__file__).resolve().parents[2]))
from guo.config import load_config
from guo.process import build_child_env, no_activate


def sha(path: Path) -> str:
    return hashlib.sha256(path.read_bytes()).hexdigest()


def main() -> int:
    parser = argparse.ArgumentParser(description=__doc__)
    parser.add_argument('--out', type=Path, required=True, help='New external run folder; never reused.')
    parser.add_argument('--mode', choices=['fixed', 'control'], default='fixed')
    parser.add_argument('--assembly', type=Path, help='Built production Debug GUO.dll for fixed mode.')
    parser.add_argument('--control-ref', help='Frozen Git ref for the independent reader control.')
    parser.add_argument('--prepared-root', type=Path, help='Cold reopen an existing synthetic prepared fixture.')
    parser.add_argument('--windowed', action='store_true', help='Visible dock captures, only with --prepared-root.')
    parser.add_argument('--reader-only', action='store_true', help='Prepared reader/Release type checks without editor dock.')
    args = parser.parse_args()
    for path in [args.out, args.assembly, args.prepared_root]:
        if path is not None and str(path).replace('\\', '/').startswith('//'):
            parser.error('Synthetic test inputs and outputs must use local paths')
    repo = Path(__file__).resolve().parents[3]
    cfg = load_config(repo)
    out = args.out.resolve()
    if out.is_relative_to(repo) or out.exists():
        parser.error('--out must be a new folder outside the checkout')
    if args.windowed and not args.prepared_root:
        parser.error('--windowed requires --prepared-root')
    if args.reader_only and not args.prepared_root:
        parser.error('--reader-only requires --prepared-root')
    if args.mode == 'control' and not args.control_ref:
        parser.error('--control-ref is required for the frozen reader control')
    out.mkdir(parents=True)
    project = out/'project'
    plugin = project/'addons/proof'
    plugin.mkdir(parents=True)
    shutil.copyfile(Path(__file__).parent/'native/NativeProofPlugin.cs', plugin/'NativeProofPlugin.cs')
    (plugin/'plugin.cfg').write_text('[plugin]\nname="Synthetic terrain proof"\ndescription="Local path tests"\nauthor="Moshu"\nversion="1"\nscript="NativeProofPlugin.cs"\n', encoding='utf-8')
    (project/'project.godot').write_text('''config_version=5
[application]
config/name="Synthetic terrain proof"
[dotnet]
project/assembly_name="TerrainProof"
[rendering]
renderer/rendering_method="gl_compatibility"
textures/canvas_textures/default_texture_filter=0
[editor_plugins]
enabled=PackedStringArray("res://addons/proof/plugin.cfg")
''', encoding='utf-8')
    identity = {'mode': args.mode, 'production_stubbed': False}
    refs = ''
    if args.mode == 'control':
        frozen = subprocess.check_output(['git', 'show', f'{args.control_ref}:godot/GUO/src/Render/TerrainLayers.cs'], cwd=repo)
        (project/'FrozenTerrainLayers.cs').write_bytes(frozen)
        identity.update(control_ref=args.control_ref, frozen_reader_sha256=hashlib.sha256(frozen).hexdigest())
    else:
        assembly = (args.assembly or repo/'godot/GUO/.godot/mono/temp/bin/Debug/GUO.dll').resolve()
        identity.update(production_assembly=str(assembly), production_assembly_sha256=sha(assembly))
        for name in ['GUO', 'GUO.ModApi', 'cuoapi']:
            path = assembly.parent/f'{name}.dll'
            if path.exists():
                # XML escape for arbitrary user-selected folders.
                from xml.sax.saxutils import escape
                refs += f'<Reference Include="{name}"><HintPath>{escape(path.as_posix())}</HintPath></Reference>\n'
    (project/'TerrainProof.csproj').write_text(f'''<Project Sdk="Godot.NET.Sdk/4.7.2">
<PropertyGroup><TargetFramework>net8.0</TargetFramework><EnableDynamicLoading>true</EnableDynamicLoading><AssemblyName>TerrainProof</AssemblyName><Nullable>disable</Nullable></PropertyGroup>
<ItemGroup>{refs}</ItemGroup>
</Project>
''', encoding='utf-8')
    (out/'identity.json').write_text(json.dumps(identity, indent=2), encoding='utf-8')
    env = build_child_env()
    env['MSBUILDDISABLENODEREUSE'] = '1'
    quiet = {'creationflags': subprocess.CREATE_NO_WINDOW} if os.name == 'nt' else {}
    with (out/'build.log').open('w', encoding='utf-8') as log:
        code = subprocess.run(['dotnet', 'build', str(project/'TerrainProof.csproj'), '--nologo', '-v:q'], stdout=log, stderr=subprocess.STDOUT, env=env, **quiet).returncode
    if code:
        print(f'Build failed: {out / "build.log"}')
        return code
    identity['probe_source_sha256'] = sha(plugin/'NativeProofPlugin.cs')
    identity['probe_assembly_sha256'] = sha(project/'.godot/mono/temp/bin/Debug/TerrainProof.dll')
    identity['engine_sha256'] = sha(cfg.godot_console_exe)
    (out/'identity.json').write_text(json.dumps(identity, indent=2), encoding='utf-8')
    engine = [str(cfg.godot_console_exe), '--editor', '--path', str(project)]
    if not args.windowed:
        engine.append('--headless')
    else:
        engine += ['--resolution', '1280x900']
    engine += ['--', '--proof-out', str(out/'result')]
    if args.mode == 'control':
        engine.append('--control')
    if args.prepared_root:
        engine += ['--prepared-root', str(args.prepared_root.resolve())]
    if args.reader_only:
        engine.append('--reader-only')
    with (out/'native.log').open('w', encoding='utf-8') as log:
        code = subprocess.run(engine, stdout=log, stderr=subprocess.STDOUT, env=env, **no_activate()).returncode
    report_path = out/'result/result.json'
    if not report_path.is_file():
        print(f'No native result: {out / "native.log"}')
        return 1
    report = json.loads(report_path.read_text(encoding='utf-8'))
    expected = identity.get('production_assembly_sha256')
    if expected and report['assembly_sha256'].lower() != expected:
        print('FAIL: native assembly differs from requested production assembly')
        return 1
    print(f'{"PASS" if code == 0 and report["ok"] else "FAIL"}: {len(report["passed"])} native assertions; {report_path}')
    return 0 if code == 0 and report['ok'] else 1


if __name__ == '__main__':
    raise SystemExit(main())
