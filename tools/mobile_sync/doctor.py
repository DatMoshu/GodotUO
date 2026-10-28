"""Check the dependencies and output location for the Labs mobile inspector."""
import os
from pathlib import Path
import shutil
import sys
sys.path.insert(0, str(Path(__file__).resolve().parents[1]))
from guo.config import load_config

cfg = load_config()
checks = [
    (cfg.godot_console_exe.is_file(), 'Godot .NET: run launchers/pipeline/00_bootstrap.bat if missing'),
    (shutil.which('dotnet') is not None, '.NET SDK: install the SDK required by godot/GUO/GUO.csproj'),
    (not cfg.build.resolve().is_relative_to(cfg.root.resolve()), 'Set external UO_BUILD_DIR in config.local.bat for JSON exports'),
]
for passed, message in checks:
    print(('OK: ' if passed else 'MISSING: ') + message)
print('Build Debug with -p:GuoLabs=true; open -mobiledebug in game. Release/export builds exclude this feature.')
sys.exit(0 if all(passed for passed, _ in checks) else 1)
