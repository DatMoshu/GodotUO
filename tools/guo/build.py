"""Building GUO's C# from tools, the way the timing tools need it.

The editor, and a client started with ``--path``, run the Debug build in
``.godot/mono/temp/bin/Debug``, which is compiled with ``Optimize=false``: the
JIT optimiser is off there. That is right for debugging and wrong for timing,
and ClassicUO, which GUO is timed against, is built Release. So the timing
tools rebuild that same folder with ``-p:Optimize=true`` before they run
(ADR-0007's B4 results, 2026-09-28). The next plain ``dotnet build`` or editor
build puts the unoptimised one back.
"""

from __future__ import annotations

import subprocess

from .config import Config


def build_client(cfg: Config, optimize: bool = True) -> None:
    """Rebuild GUO's assembly where the client loads it from; optimised unless asked otherwise.

    ``--no-incremental``, because MSBuild does not count a changed Optimize
    property as a reason to recompile.
    """
    project = cfg.godot_project / "GUO.csproj"
    print(f"[build] GUO, Optimize={str(optimize).lower()}", flush=True)
    subprocess.run(
        ["dotnet", "build", str(project), "--no-incremental", f"-p:Optimize={str(optimize).lower()}",
         "-v", "q", "--nologo"],
        check=True,
    )
