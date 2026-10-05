"""Integration regressions through EditorData.LoadAsync in fresh headless editors.

Requires an already-built project, configured retail data and one valid native
stage. Outputs stay local: reports/images derive from the user's client data.
No validator implementation is imported or duplicated by this test.
"""
from __future__ import annotations
import argparse
from contextlib import contextmanager
import json
import os
from pathlib import Path
import subprocess
import sys
import time

sys.path.insert(0, str(Path(__file__).resolve().parents[1]))
from guo import load_config


@contextmanager
def preserve_project(project: Path):
    """Restore exact existing bytes even when a child, report or fixture fails."""
    path = project / 'project.godot'
    before = path.read_bytes()
    try:
        yield
    finally:
        path.write_bytes(before)


def checked_output(output: Path, retail: Path) -> Path:
    # resolve follows existing symlink/junction ancestors of a nonexistent leaf.
    out = output.resolve()
    if out.is_relative_to(retail.resolve()):
        raise SystemExit('Refusing output inside UO_CLIENT_DATA; no fixtures were created.')
    if out.exists():
        raise SystemExit('Choose a new output directory; prior evidence is not overwritten.')
    return out


def main(argv=None) -> int:
    ap = argparse.ArgumentParser(description=__doc__)
    ap.add_argument('--stage', type=Path, required=True)
    ap.add_argument('--multi', default='0x3f00')
    ap.add_argument('--out', type=Path, required=True)
    ap.add_argument('--case', action='append', help='select named cases; default all')
    ap.add_argument('--expect-baseline-bug', action='store_true', help='prove comments-only is accepted by the unpatched loader')
    args = ap.parse_args(argv)
    cfg = load_config()
    out = checked_output(args.out, cfg.client_data)
    with preserve_project(cfg.godot_project):
        return run_cases(args, cfg, out)


def run_cases(args, cfg, out) -> int:
    out.mkdir(parents=True)
    stage = args.stage.resolve()
    # Arrange a real valid overlay, including references outside its folder.
    valid = (stage / 'files_override.txt').read_text(encoding='utf-8-sig')
    lines = [s for s in valid.splitlines() if s and not s.startswith(('#', ';'))]
    first = lines[0]
    logical, target = first.split('=')
    empty_def = out / 'empty.def'
    empty_def.write_text('')
    cases = {
        'valid_native_stage': (valid, True, ''),
        'comments_blanks_uppercase_external_def': ('\n  # comment\n ; another\n' + valid + f'ART.DEF={empty_def}\n', True, ''),
        'nested_key_relative_target': (valid + f'Music/Digital/Config.txt={os.path.relpath(empty_def, cfg.godot_project)}\n', True, ''),
        'base_without_stage': (None, True, ''),
        'missing_mapping_file': (None, False, 'files_override.txt is missing'),
        'empty': ('', False, 'has no mappings'),
        'comments_only': ('\n # comment\n; comment\n', False, 'has no mappings'),
        'malformed': ('malformed override without equals\n', False, 'line 1'),
        'multiple_equals': (first + '=extra\n', False, 'line 1'),
        'empty_key': ('=' + target + '\n', False, 'line 1'),
        'empty_target': (logical + '=\n', False, 'line 1'),
        'duplicate_case': (first + '\n' + logical.upper() + '=' + target + '\n', False, 'duplicate logical file'),
        'missing_named_archive': (logical + '=' + str(out / 'missing.uop') + '\n', False, 'not a readable file'),
        'directory_target': (logical + '=' + str(out) + '\n', False, 'not a readable file'),
        'invalid_target': (logical + '=invalid\0path\n', False, 'not a readable file'),
        'device_target': (logical + '=NUL\n', False, 'not a readable file'),
        'rooted_key': ('C:/MultiCollection.uop=' + target + '\n', False, 'relative logical UO file'),
        'traversal_key': ('../MultiCollection.uop=' + target + '\n', False, 'relative logical UO file'),
        'key_whitespace': (' ' + first + '\n', False, 'relative logical UO file'),
    }
    env = os.environ.copy()
    for key, folder in [('APPDATA', 'roaming'), ('LOCALAPPDATA', 'local')]:
        directory = out / 'user' / folder
        directory.mkdir(parents=True)
        env[key] = str(directory)
    env.update(UO_CLIENT_DATA=str(cfg.client_data), UO_CLIENT_VERSION=cfg.client_version,
               UO_WORLD_PROJECT=str(out / 'empty-world'),
               UseSharedCompilation='false')
    # AiFeatures.ReadPreference reads this saved EditorSettings property.
    # APPDATA is isolated above; no setting in the user's profile is changed.
    settings = out / 'user' / 'roaming' / 'Godot' / 'editor_settings-4.7.tres'
    settings.parent.mkdir(parents=True)
    settings.write_text('[gd_resource type="EditorSettings" format=3]\n\n[resource]\nguo/ai/enabled = false\n', encoding='utf-8')
    results = []
    selected = args.case or list(cases)
    for name in selected:
        content, positive, error = cases[name]
        folder = out / name
        folder.mkdir()
        fixture = folder / 'stage'
        fixture.mkdir()
        if content is not None:
            (fixture / 'files_override.txt').write_text(content, encoding='utf-8')
        cmd = [str(cfg.godot_console_exe), '--headless', '--editor', '--path', str(cfg.godot_project), '--',
               '--guo-editor-smoke', str(folder), '--guo-editor-multi-inspect',
               '0x0064' if name in ('base_without_stage', 'comments_only') else args.multi]
        if name != 'base_without_stage':
            cmd += ['--guo-editor-data-stage', str(fixture)]
        # Act: launch the real addon, not a mirrored parser or helper method.
        start = time.time()
        with (folder / 'editor.log').open('w', encoding='utf-8') as log:
            proc = subprocess.Popen(cmd, env=env, cwd=cfg.godot_project, stdout=log, stderr=subprocess.STDOUT)
            try:
                code = proc.wait(timeout=180)
            except subprocess.TimeoutExpired:
                proc.kill()
                proc.wait()
                code = -999
        report_path = folder / 'report.json'
        report = json.loads(report_path.read_text()) if report_path.exists() else {}
        failures = '; '.join(report.get('failures', []))
        # Assert observable load state: fallback plus failed ID search cannot pass.
        if args.expect_baseline_bug:
            passed = name == 'comments_only' and code == 0 and report.get('data_loaded') is True and report.get('ok') is True
        elif positive:
            passed = code == 0 and report.get('data_loaded') is True and report.get('ok') is True
            if name != 'base_without_stage':
                passed = passed and report.get('multi', {}).get('id') == int(args.multi, 0)
        else:
            passed = code == 1 and report.get('data_loaded') is False and report.get('load_ms') == 0 and 'Explicit editor data stage' in failures and error in failures
        row = dict(case=name, passed=passed, pid=proc.pid, start=start, end=time.time(), exit_code=code,
                   data_loaded=report.get('data_loaded'), failures=failures, command=cmd)
        results.append(row)
        (out / 'results.json').write_text(json.dumps(results, indent=2))
        print(('PASS ' if passed else 'FAIL ') + name, flush=True)
    return 0 if all(r['passed'] for r in results) else 1


if __name__ == '__main__':
    raise SystemExit(main())
