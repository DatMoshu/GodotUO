"""Run existing GUO checks with process-scoped Windows compiler lifetime settings."""
from __future__ import annotations

import hashlib
import json
import os
from pathlib import Path
import shutil
import subprocess
import sys
import time

from guo.process import no_activate


def child_environment():
    env = dict(os.environ)
    env['UO_PYTHON'] = sys.executable
    if os.name == 'nt':
        # The Godot console wrapper waits for every descendant. Roslyn's
        # persistent compiler server would outlive the engine and keep that
        # wrapper running after a successful build/editor quit. MSBuild imports
        # this property from the environment; direct csc exits after its work.
        env['UseSharedCompilation'] = 'false'
    return env


def run(cfg, out: Path, repeats: int = 2):
    if os.name != 'nt':
        raise ValueError('this acceptance command replays the Windows smoke launcher')
    if repeats not in (1, 2, 3):
        raise ValueError('acceptance repeats must be 1..3')
    out = out.resolve()
    if out.is_relative_to(cfg.client_data.resolve()):
        raise ValueError('acceptance output is inside the retail installation')
    if out.exists() and any(out.iterdir()):
        raise ValueError('acceptance output must be new or empty; prior reports are preserved')
    out.mkdir(parents=True, exist_ok=True)
    env = child_environment()
    report = {'format': 1, 'passed': False, 'controls': {'UseSharedCompilation': env['UseSharedCompilation']},
              'steps': [], 'runtime_scope': 'no new runtime walk; existing layout proofs are preserved'}
    original = (cfg.godot_project / 'project.godot').read_bytes()

    def step(name, command):
        folder = out / name
        folder.mkdir()
        started = time.monotonic()
        with (folder / 'command.log').open('w', encoding='utf-8') as stream:
            result = subprocess.run(command, cwd=cfg.root, env=env, stdout=stream,
                                    stderr=subprocess.STDOUT, **no_activate())
        record = {'name': name, 'exit': result.returncode,
                  'elapsed_seconds': round(time.monotonic()-started, 3),
                  'log': str(folder / 'command.log'),
                  'log_sha256': hashlib.sha256((folder / 'command.log').read_bytes()).hexdigest()}
        report['steps'].append(record)
        (out / 'report.json').write_text(json.dumps(report, indent=2), encoding='utf-8')
        print(json.dumps(record), flush=True)
        return result.returncode == 0

    try:
        for n in range(repeats):
            name = f'smoke-{n+1}'
            passed = step(name, ['cmd.exe', '/d', '/c', str(cfg.root / 'launchers/dev/smoke.bat')])
            # The shared smoke's default output gets reused. Preserve each
            # functional report and editor log alongside its distinct command log.
            for file in ('report.json', 'editor.log'):
                source = cfg.build / 'editor_smoke/headless' / file
                if source.is_file():
                    shutil.copyfile(source, out / name / ('editor-' + file))
            if not passed:
                return report
        if not step('editor-reload', [sys.executable, str(cfg.tools / 'editor_smoke/run.py'),
                                     '--headless', '--reload', '--out', str(out / 'reload')]):
            return report
        report['passed'] = True
        return report
    finally:
        project = cfg.godot_project / 'project.godot'
        if project.read_bytes() != original:
            project.write_bytes(original)
        (out / 'report.json').write_text(json.dumps(report, indent=2), encoding='utf-8')
