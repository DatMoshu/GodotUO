"""The opt-in Labs compile boundary holds for normal and export configurations."""
import json
from pathlib import Path
import shutil
import subprocess

import pytest


PROJECT = Path(__file__).resolve().parents[2] / "godot/GUO/GUO.csproj"


@pytest.mark.parametrize("configuration, opt_in, expected", [
    ("Debug", None, False),
    ("Debug", "false", False),
    ("Debug", "true", True),
    ("Release", "true", False),
    ("ExportDebug", "true", False),
    ("ExportRelease", "true", False),
])
def test_labs_compile_boundary(configuration, opt_in, expected):
    if not shutil.which("dotnet"):
        pytest.skip(".NET SDK unavailable")
    command = [
        "dotnet", "msbuild", str(PROJECT), "-nologo",
        f"-p:Configuration={configuration}",
        "-getProperty:DefineConstants", "-getItem:Compile",
    ]
    if opt_in is not None:
        command.append(f"-p:GuoLabs={opt_in}")
    result = subprocess.run(command, capture_output=True, text=True, timeout=60)
    assert result.returncode == 0, result.stdout + result.stderr
    shape = json.loads(result.stdout)
    constants = shape["Properties"]["DefineConstants"].split(";")
    labs = [item for item in shape["Items"]["Compile"]
            if item["Identity"].replace("\\", "/").startswith("src/Labs/")]
    assert ("GUO_LABS" in constants) is expected
    assert bool(labs) is expected
