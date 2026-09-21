"""Configuration resolution for GUO tools.

Single source of truth is launchers/_shared/config.bat -- the one file a user
is meant to edit. Tools never define their own defaults for these values.

Resolution order matches the launchers exactly:

    1. environment variable  (set by common.bat, or exported by CI/the user)
    2. launchers/_shared/config.bat  (parsed directly when a tool is run
       outside a launcher, e.g. straight from an IDE or an agent)

Parsing the .bat is deliberately narrow: it only understands the
`if not defined X set "X=VALUE"` form the config file is written in, and it
expands %VAR% references against already-resolved values. It is not a batch
interpreter and does not try to be.
"""

from __future__ import annotations

import os
import re
from dataclasses import dataclass
from pathlib import Path

# `if not defined NAME set "NAME=VALUE"` / `set "NAME=VALUE"`
_SET_RE = re.compile(
    r'^\s*(?:if\s+not\s+defined\s+\w+\s+)?set\s+"(?P<key>[A-Za-z_][A-Za-z0-9_]*)=(?P<val>[^"]*)"',
    re.IGNORECASE,
)
_VAR_RE = re.compile(r"%([A-Za-z_][A-Za-z0-9_]*)%")


def find_repo_root(start: Path | None = None) -> Path:
    """Walk upward until the repo root is found.

    The root is identified by launchers/_shared/config.bat, which exists in
    every GUO checkout and nowhere else.
    """
    here = (start or Path(__file__)).resolve()
    for candidate in [here, *here.parents]:
        if (candidate / "launchers" / "_shared" / "config.bat").is_file():
            return candidate
    raise RuntimeError(
        "Could not locate the GUO repo root "
        "(no launchers/_shared/config.bat found above "
        f"{here}). Run this tool from inside the repo."
    )


def parse_config_bat(path: Path) -> dict[str, str]:
    """Extract the settings from config.bat without executing it."""
    values: dict[str, str] = {}
    for line in path.read_text(encoding="utf-8-sig", errors="replace").splitlines():
        match = _SET_RE.match(line)
        if not match:
            continue
        key, raw = match.group("key"), match.group("val")

        def expand(m: re.Match[str]) -> str:
            name = m.group(1)
            # Prefer a real environment value, then one defined earlier in the file.
            return os.environ.get(name) or values.get(name) or m.group(0)

        values[key] = _VAR_RE.sub(expand, raw)
    return values


@dataclass(frozen=True)
class Config:
    """Resolved project configuration."""

    root: Path
    godot_version: str
    godot_flavor: str
    client_data: Path
    client_version: str
    cache_dir: Path
    shard_host: str
    shard_port: int
    shard_name: str
    log_level: str

    # --- derived paths (never configured directly) ---
    @property
    def godot_project(self) -> Path:
        return self.root / "godot" / "GUO"

    @property
    def sources(self) -> Path:
        return self.root / "sources"

    @property
    def upstream(self) -> Path:
        return self.sources / "ClassicUO"

    @property
    def tools(self) -> Path:
        return self.root / "tools"

    @property
    def docs(self) -> Path:
        return self.root / "docs"

    @property
    def build(self) -> Path:
        return self.root / "build"

    @property
    def shard_src(self) -> Path:
        r"""The ModernUO checkout. Untracked; launchers\shard\fetch.bat makes it."""
        return self.tools / "modernuo" / "src"

    @property
    def shard_dist(self) -> Path:
        r"""The built server. launchers\shard\build.bat makes it."""
        return self.shard_src / "Distribution"

    @property
    def godot_exe(self) -> Path:
        stem = f"Godot_v{self.godot_version}_{self.godot_flavor}"
        return self.tools / "godot" / stem / f"{stem}.exe"

    @property
    def godot_console_exe(self) -> Path:
        stem = f"Godot_v{self.godot_version}_{self.godot_flavor}"
        return self.tools / "godot" / stem / f"{stem}_console.exe"


def load_config(root: Path | None = None) -> Config:
    """Resolve configuration from the environment, falling back to config.bat."""
    root = (root or find_repo_root()).resolve()
    from_bat = parse_config_bat(root / "launchers" / "_shared" / "config.bat")

    def get(key: str, default: str = "") -> str:
        # Environment wins, exactly as in common.bat.
        return os.environ.get(key) or from_bat.get(key) or default

    try:
        shard_port = int(get("UO_SHARD_PORT", "2593"))
    except ValueError:
        shard_port = 2593

    cache = get("UO_CACHE_DIR") or str(Path.home() / ".cache" / "GUO")

    return Config(
        root=root,
        godot_version=get("GODOT_VERSION", "4.7.2-stable"),
        godot_flavor=get("GODOT_FLAVOR", "mono_win64"),
        client_data=Path(os.path.expandvars(get("UO_CLIENT_DATA"))),
        client_version=get("UO_CLIENT_VERSION", "7.0.15.1"),
        cache_dir=Path(os.path.expandvars(cache)),
        shard_name=get("UO_SHARD_NAME", "GUO Dev"),
        shard_host=get("UO_SHARD_HOST", "127.0.0.1"),
        shard_port=shard_port,
        log_level=get("UO_LOG_LEVEL", "INFO"),
    )
