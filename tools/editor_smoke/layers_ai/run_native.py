#!/usr/bin/env python3
"""Exercise the real Layers dock in a native editor, using only synthetic providers."""
from __future__ import annotations

import argparse
import hashlib
import json
import ntpath
import os
from pathlib import Path
import shutil
import subprocess
import sys
import tempfile
from xml.sax.saxutils import escape




def report_line(message: str) -> None:
    encoding = getattr(sys.stdout, "encoding", None) or "utf-8"
    print(message.encode(encoding, errors="backslashreplace").decode(encoding))


def local_spelling(value: str) -> bool:
    """Reject network/device/URI spellings before any filesystem evaluation."""
    normalized = value.replace("\\", "/")
    if "\0" in value or normalized.startswith("//") or normalized.lower().startswith(("/??/", "/device/", "/global??/", "/dosdevices/")):
        return False
    drive, tail = ntpath.splitdrive(value)
    if drive and (len(drive) != 2 or not drive[0].isalpha() or drive[1] != ":" or not tail.startswith(("/", "\\"))):
        return False
    return ":" not in tail


def main() -> int:
    parser = argparse.ArgumentParser(description=__doc__)
    parser.add_argument('--out', required=True, help='New external evidence directory.')
    parser.add_argument('--mode', choices=['component', 'plugin'], default='component')
    parser.add_argument('--assembly', help='Production Debug GUO.dll (defaults to this checkout).')
    parser.add_argument('--capture', action='store_true', help='Plugin mode: exercise actual frame waits using read-only configured client data.')
    parser.add_argument('--persisted-preference', action='store_true', help='Plugin mode: seed false in isolated settings, then cold reopen.')
    args = parser.parse_args()
    for name, value in [("--out", args.out), ("--assembly", args.assembly)]:
        if value is not None and not local_spelling(value):
            parser.error(name + " requires local path spellings, without network/device/URI or drive-relative paths")
    repo = Path(__file__).resolve().parents[3]
    sys.path.insert(0, str(repo / 'tools'))
    from guo import load_config
    cfg = load_config(repo)
    out = Path(args.out).resolve()
    if out.exists() or out.is_relative_to(repo):
        parser.error('--out must be a new local directory outside the checkout')
    if args.mode != 'plugin' and (args.capture or args.persisted_preference):
        parser.error('--capture/--persisted-preference require --mode plugin')
    if args.mode == 'plugin' and not args.persisted_preference:
        parser.error('Plugin mode requires --persisted-preference to isolate editor settings')
    out.mkdir(parents=True)
    scratch = Path(tempfile.mkdtemp(prefix='guo-layers-ai-'))
    # Match the real checkout depth: WorldHost and EditorData resolve ../.. from res://.
    project = scratch / 'godot' / 'GUO'
    plugin = project / 'addons/proof'
    plugin.mkdir(parents=True)
    probe = 'LayersAiPluginProof' if args.mode == 'plugin' else 'LayersAiProof'
    shutil.copyfile(Path(__file__).parent / 'native' / (probe + '.cs'), plugin / (probe + '.cs'))
    (plugin / 'plugin.cfg').write_text('[plugin]\nname="Layers AI synthetic proof"\ndescription="Local synthetic tests"\nauthor="Moshu"\nversion="1"\nscript="' + probe + '.cs"\n', encoding='utf-8')
    (project / 'project.godot').write_text('''config_version=5
[application]
config/name="Layers AI synthetic proof"
[dotnet]
project/assembly_name="LayersAiProof"
[rendering]
renderer/rendering_method="gl_compatibility"
textures/canvas_textures/default_texture_filter=0
[editor_plugins]
enabled=PackedStringArray("res://addons/proof/plugin.cfg")
''', encoding='utf-8')
    assembly = (Path(args.assembly) if args.assembly else repo / 'godot/GUO/.godot/mono/temp/bin/Debug/GUO.dll').resolve()
    refs = ''.join(f'<Reference Include="{name}"><HintPath>{escape((assembly.parent / (name + ".dll")).as_posix())}</HintPath></Reference>' for name in ['GUO', 'GUO.ModApi', 'cuoapi'])
    (project / 'LayersAiProof.csproj').write_text('<Project Sdk="Godot.NET.Sdk/4.7.2"><PropertyGroup><TargetFramework>net8.0</TargetFramework><EnableDynamicLoading>true</EnableDynamicLoading><AssemblyName>LayersAiProof</AssemblyName><Nullable>disable</Nullable></PropertyGroup><ItemGroup>' + refs + '</ItemGroup></Project>', encoding='utf-8')
    env = os.environ.copy()
    env.update(UseSharedCompilation='false', MSBUILDDISABLENODEREUSE='1', UO_BUILD_DIR=str(scratch / 'build'),
               UO_WORKSPACE_DIR=str(scratch / 'workspace'), GUO_EDITOR_SCRIPTED='1',
               UO_CLIENT_DATA=str(cfg.client_data), UO_CLIENT_VERSION=str(cfg.client_version))
    for name in ['GUO_EDITOR_MCP_PORT', 'GUO_EDITOR_MCP_TOKEN']:
        env.pop(name, None)
    layout = {'scratch': str(scratch), 'project': str(project), 'child_cwd': str(project),
              'world_host_scratch': str((project / '../../build/editor_world').resolve()),
              'build': str(scratch / 'build'), 'workspace': str(scratch / 'workspace'),
              'engine_home': str(scratch / 'engine'), 'evidence': str(out)}
    (out / 'layout.json').write_text(json.dumps(layout, indent=2), encoding='utf-8')
    quiet = {'creationflags': subprocess.CREATE_NO_WINDOW} if os.name == 'nt' else {}

    def sha(path: Path) -> str:
        return hashlib.sha256(path.read_bytes()).hexdigest()

    def run(name: str, command: list[str], timeout: int = 180) -> int:
        with (out / (name + '.log')).open('w', encoding='utf-8') as log:
            child = subprocess.Popen(command, stdout=log, stderr=subprocess.STDOUT, env=env, cwd=project, **quiet)
            (out / (name + '-pid.json')).write_text(json.dumps({'pid': child.pid, 'args': command}), encoding='utf-8')
            try:
                code = child.wait(timeout=timeout)
            except subprocess.TimeoutExpired:
                child.terminate()
                child.wait(timeout=20)
                code = 124
            (out / (name + '-exit.txt')).write_text(str(code), encoding='utf-8')
            return code

    code = run('build', ['dotnet', 'build', str(project / 'LayersAiProof.csproj'), '--nologo', '-v:q'])
    if code:
        report_line(f'Build failed: {out / "build.log"}')
        return code
    copied = project / '.godot/mono/temp/bin/Debug/GUO.dll'
    if sha(copied) != sha(assembly):
        raise RuntimeError('Probe reference copy differs from requested production assembly')
    sources = {str(p.relative_to(repo)): sha(p) for p in (repo / 'godot/GUO/addons/guo_editor').rglob('*.cs')}
    identity = {'mode': args.mode, 'layout': layout, 'project': str(project), 'production_assembly': str(assembly),
                'production_sha256': sha(assembly), 'probe_source_sha256': sha(plugin / (probe + '.cs')),
                'probe_assembly_sha256': sha(project / '.godot/mono/temp/bin/Debug/LayersAiProof.dll'),
                'engine_files': {p.name: sha(p) for p in cfg.godot_console_exe.parent.glob('*.exe')}, 'sources': sources}
    engine = cfg.godot_console_exe
    if args.persisted_preference:
        engine_home = scratch / 'engine'
        engine_home.mkdir()
        for original in cfg.godot_console_exe.parent.glob('*.exe'):
            shutil.copyfile(original, engine_home / original.name)
            assert sha(engine_home / original.name) == identity['engine_files'][original.name]
        shutil.copytree(cfg.godot_console_exe.parent / 'GodotSharp', engine_home / 'GodotSharp')
        (engine_home / '_sc_').touch()
        engine = engine_home / cfg.godot_console_exe.name
    command = [str(engine), '--headless', '--editor', '--path', str(project), '--', '--proof-out', str(out / 'result')]
    if args.persisted_preference:
        code = run('seed-false', command + ['--seed-false'])
        if code:
            return code
        settings = list((engine.parent / 'editor_data').rglob('editor_settings-*.tres'))
        if len(settings) != 1 or 'guo/ai/enabled = false' not in settings[0].read_text(encoding='utf-8'):
            raise RuntimeError('Isolated editor did not persist false preference')
        shutil.copyfile(settings[0], out / 'saved-false-settings.tres')
        identity['isolated_saved_false_sha256'] = sha(settings[0])
        command.append('--require-saved-false')
    if args.capture:
        command.append('--capture')
    (out / 'identity.json').write_text(json.dumps(identity, indent=2), encoding='utf-8')
    code = run('native', command)
    report = out / 'result/result.json'
    if not report.exists():
        report_line(f'Missing native result: {out / "native.log"}')
        return 1
    data = json.loads(report.read_text(encoding='utf-8'))
    if data['assembly_sha256'].lower() != identity['production_sha256']:
        raise RuntimeError('Native reference file differs from requested production assembly')
    ok = code == 0 and data['ok']
    report_line(f'{"PASS" if ok else "FAIL"}: {len(data["passed"])} scoped native assertions; {report}')
    report_line(f'Frozen native project retained: {project}')
    return 0 if ok else 1


if __name__ == '__main__':
    raise SystemExit(main())
