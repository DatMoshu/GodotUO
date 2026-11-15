"""Headless native-widget regression, isolated from the open editor's assembly.

python tools/editor_smoke/tool_approval_layout.py --out NEW_DIRECTORY
Requires the configured Godot .NET console executable and dotnet on PATH;
no UO data or running editor is needed. Does not build or reload GUO.
"""
import argparse
import shutil
import subprocess
import sys
from pathlib import Path

sys.path.insert(0, str(Path(__file__).resolve().parents[1]))
from guo import load_config
from guo.process import build_child_env, no_activate


def main():
    parser = argparse.ArgumentParser(description=__doc__)
    parser.add_argument('--out', type=Path, required=True)
    args = parser.parse_args()
    out = args.out.resolve()
    if out.exists():
        parser.error('Use a fresh isolated output directory')
    cfg = load_config()
    root = Path(__file__).resolve().parents[2]
    out.mkdir(parents=True)
    shutil.copy2(root / 'godot/GUO/addons/guo_editor/AI/AiToolApprovalDialog.cs', out / 'AiToolApprovalDialog.cs')
    shutil.copy2(Path(__file__).parent / 'fixtures/ToolApprovalLayoutProbe.cs', out / 'ToolApprovalLayoutProbe.cs')
    (out / 'ToolApprovalLayoutProbe.csproj').write_text(
        '<Project Sdk="Godot.NET.Sdk/4.7.2"><PropertyGroup><TargetFramework>net8.0</TargetFramework>'
        '<EnableDynamicLoading>true</EnableDynamicLoading></PropertyGroup></Project>\n')
    (out / 'project.godot').write_text(
        'config_version=5\n[application]\nconfig/name="Tool approval layout regression"\n'
        'run/main_scene="res://main.tscn"\n[dotnet]\nproject/assembly_name="ToolApprovalLayoutProbe"\n'
        '[display]\nwindow/size/viewport_width=640\nwindow/size/viewport_height=480\n'
        '[debug]\nfile_logging/enable_file_logging=false\nfile_logging/enable_file_logging.pc=false\n'
        '[rendering]\nrenderer/rendering_method="gl_compatibility"\n')
    (out / 'main.tscn').write_text(
        '[gd_scene load_steps=2 format=3]\n[ext_resource type="Script" path="res://ToolApprovalLayoutProbe.cs" id="1"]\n'
        '[node name="ToolApprovalLayoutProbe" type="Node"]\nscript=ExtResource("1")\n')
    env = build_child_env()
    build = subprocess.run(['dotnet', 'build', str(out / 'ToolApprovalLayoutProbe.csproj'), '-v', 'quiet',
                            '-p:ImportDirectoryBuildProps=false', '-p:ImportDirectoryBuildTargets=false'],
                           env=env, text=True, capture_output=True)
    (out / 'build.log').write_text(build.stdout + build.stderr)
    if build.returncode:
        print(build.stdout + build.stderr)
        return build.returncode
    run = subprocess.run([str(cfg.godot_console_exe), '--headless', '--path', str(out)],
                         env=env, text=True, capture_output=True, timeout=120, **no_activate())
    (out / 'run.log').write_text(run.stdout + run.stderr)
    print(run.stdout + run.stderr)
    return run.returncode


if __name__ == '__main__':
    raise SystemExit(main())
