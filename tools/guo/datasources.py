"""Where the client's data comes from (ADR-0021): one resolution order everywhere.

    1. a custom data folder: UO_CUSTOM_DATA (environment, then config.local.bat,
       then config.bat), or guo_data/ beside a shipped client. It holds a
       guo_data.json manifest (docs/data_formats.md, "Custom data folders") and is
       either "complete" (a whole data set of its own) or "layered" (files that
       replace single files of a UO install, like a staged set, ADR-0022).
    2. the UO install: UO_CLIENT_DATA from the environment, else the saved
       setting (config.local.bat or the central config; on a device, the
       wizard's saved choice), else the platform defaults. The first value that
       is set is the only one tried: a broken setting is reported, never
       silently replaced by some other install.
    3. neither valid: the first-run wizard. Tools report it and stop cleanly
       instead of failing on a missing file.

"Valid" is tools/guo/formats.py's required set: every required DataFile is
satisfied by its UOP form, or by its MUL form together with its index.

Reads only. Nothing here writes anywhere.
"""

from __future__ import annotations

import json
import os
import sys
from dataclasses import dataclass, field
from pathlib import Path

from .formats import required_files

MANIFEST = "guo_data.json"
MANIFEST_FORMAT = "guo/data-folder@1"
MODES = ("complete", "layered")
WIZARD_EXIT = 3
"""Exit code a tool uses for "no valid data: run the first-run wizard"."""


@dataclass
class Validity:
    ok: bool
    missing: list[str] = field(default_factory=list)
    reason: str = ""


def validate_install(folder: Path | None) -> Validity:
    """A folder is a valid data set when every required DataFile is satisfied."""
    if folder is None or str(folder) in ("", "."):
        return Validity(False, reason="not set")
    if not folder.is_dir():
        return Validity(False, reason=f"not a folder: {folder}")
    missing = [f.key for f in required_files() if not f.is_satisfied(folder)]
    if missing:
        return Validity(False, missing, f"missing required data: {', '.join(missing)}")
    return Validity(True)


def read_manifest(folder: Path) -> tuple[dict | None, str]:
    """The folder's guo_data.json, or None and why not."""
    path = folder / MANIFEST
    if not path.is_file():
        return None, f"no {MANIFEST}"
    try:
        m = json.loads(path.read_text(encoding="utf-8"))
    except (OSError, ValueError) as e:
        return None, f"{MANIFEST} unreadable: {e}"
    if m.get("format") != MANIFEST_FORMAT:
        return None, f"{MANIFEST} format is {m.get('format')!r}, expected {MANIFEST_FORMAT!r}"
    if m.get("mode") not in MODES:
        return None, f"{MANIFEST} mode is {m.get('mode')!r}, expected one of {MODES}"
    files = m.get("files", {})
    if not isinstance(files, dict):
        return None, f"{MANIFEST} files must be an object"
    for name in files:
        if Path(name).name != name or not (folder / name).is_file():
            return None, f"{MANIFEST} names {name!r}, which is not a file in the folder"
    if not isinstance(m.get("contains_ea_data"), bool):
        return None, f"{MANIFEST} must declare contains_ea_data (true or false)"
    return m, ""


def platform_defaults(platform: str | None = None) -> list[Path]:
    """Where a UO install usually is, in the order they are tried."""
    platform = platform or sys.platform
    out: list[Path] = []
    if platform == "win32":
        try:
            import winreg

            key = r"SOFTWARE\WOW6432Node\Electronic Arts\EA Games\Ultima Online Classic"
            with winreg.OpenKey(winreg.HKEY_LOCAL_MACHINE, key) as k:
                out.append(Path(winreg.QueryValueEx(k, "InstallDir")[0]))
        except OSError:
            pass
        for base in ("ProgramFiles(x86)", "ProgramFiles"):
            if os.environ.get(base):
                out.append(Path(os.environ[base]) / "Electronic Arts" / "Ultima Online Classic")
    elif platform.startswith("linux"):
        out.append(Path.home() / "UO")
    return out


@dataclass
class Resolution:
    """What the client would read, and why."""

    source: str
    """"custom" (a complete custom folder), "install", "install+custom" (a layered
    folder over the install), or "wizard" (nothing valid)."""

    client_data: Path | None = None
    """The folder the client opens as its install, or None for the wizard."""

    origin: str = ""
    """Where client_data came from: custom, environment, setting or default."""

    custom: Path | None = None
    """The custom folder in use, if any."""

    overrides: dict[str, Path] = field(default_factory=dict)
    """For a layered folder: install file name -> replacement (see write_override)."""

    notes: list[str] = field(default_factory=list)
    """Every candidate that was rejected, and why, in the order tried."""

    local_only: bool = False
    """The custom folder declares EA-derived data: usable here, never shipped."""

    @property
    def ok(self) -> bool:
        return self.source != "wizard"

    def message(self) -> str:
        if not self.ok:
            return "no valid UO data found: run the first-run wizard, or set UO_CLIENT_DATA in launchers\\_shared\\config.local.bat"
        where = f"{self.client_data} ({self.origin})"
        if self.source == "custom":
            return f"custom data (complete): {where}"
        if self.source == "install+custom":
            local = " (holds EA-derived data: local only, never shipped)" if self.local_only else ""
            return f"UO install {where}, layered with custom data {self.custom}{local}"
        return f"UO install {where}"


def overrides(folder: Path, manifest: dict) -> dict[str, Path]:
    """A layered folder's replacements: install file name (lowercase) -> the folder's copy.
    From the manifest's files, so a shipped pack carries no absolute paths."""
    return {name.lower(): folder / name for name in manifest.get("files", {})}


def write_override(res: Resolution, path: Path) -> Path | None:
    """Writes the client's files_override (name=path lines) for a layered folder."""
    if not res.overrides:
        return None
    path.parent.mkdir(parents=True, exist_ok=True)
    lines = [f"{k}={v}" for k, v in sorted(res.overrides.items())]
    path.write_text("\n".join(lines) + "\n", encoding="utf-8")
    return path


def resolve(custom: str = "", env_install: str = "", setting_install: str = "",
            defaults: list[Path] | None = None) -> Resolution:
    """The ADR-0021 order. Arguments are the raw configured values ("" = unset)."""
    notes: list[str] = []
    # A configured install is authoritative: the first one set (environment,
    # then the saved setting) is the only one tried, and a broken one is
    # reported rather than silently replaced by another install that may be a
    # different client. The platform defaults are tried only when none is set.
    if env_install:
        installs = [("environment", Path(env_install))]
    elif setting_install:
        installs = [("setting", Path(setting_install))]
    else:
        installs = [("default", d) for d in (platform_defaults() if defaults is None else defaults)]

    def first_install() -> tuple[str, Path] | None:
        for origin, path in installs:
            v = validate_install(path)
            if v.ok:
                return origin, path
            notes.append(f"install ({origin}) {path}: {v.reason}")
        return None

    layered: tuple[Path, dict] | None = None
    if custom:
        folder = Path(custom)
        manifest, why = read_manifest(folder) if folder.is_dir() else (None, "not a folder")
        if manifest is None:
            notes.append(f"custom {folder}: {why}")
        elif manifest["mode"] == "complete":
            v = validate_install(folder)
            if v.ok:
                return Resolution("custom", folder, "custom", folder, {}, notes, manifest["contains_ea_data"])
            notes.append(f"custom {folder} (complete): {v.reason}")
        else:
            layered = (folder, manifest)

    found = first_install()
    if found is None:
        if layered:
            notes.append(f"custom {layered[0]} (layered) needs a valid UO install beneath it")
        return Resolution("wizard", notes=notes)
    origin, path = found
    if layered:
        folder, manifest = layered
        files = overrides(folder, manifest)
        if files:
            return Resolution("install+custom", path, origin, folder, files, notes, manifest["contains_ea_data"])
        notes.append(f"custom {folder} (layered): its manifest lists no files")
    return Resolution("install", path, origin, notes=notes)


def resolve_config(cfg) -> Resolution:
    """resolve() with a loaded Config's raw values (see config.load_config)."""
    return resolve(cfg.custom_data_setting, cfg.client_data_env, cfg.client_data_setting)
