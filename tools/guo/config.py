"""Configuration resolution for GUO tools.

Defaults live in launchers/_shared/config.bat; a user's own values go in
launchers/_shared/config.local.bat. Tools never define their own defaults for
these values.

Resolution order matches the launchers exactly:

    1. environment variable  (set by common.bat, or exported by CI/the user)
    2. launchers/_shared/config.local.bat  (the user's own paths; gitignored,
       and read by config.bat before its defaults)
    3. launchers/_shared/config.bat  (parsed directly when a tool is run
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
    world_project: Path
    editor_live_host: str
    editor_live_port: int
    editor_name: str
    shard_host: str
    shard_port: int
    shard_name: str
    shard_owner: str
    shard_owner_password: str
    # Game master accounts the shard makes on a headless boot, for scripted
    # clients that run beside the owner. Each one's password is its name.
    shard_gm_accounts: tuple[str, ...]
    log_level: str

    # --- Android (optional; see tools/android and ADR-0017) ---
    android_sdk: Path
    android_jdk: Path | None
    android_keystore: Path
    android_keystore_user: str
    android_keystore_password: str
    android_package: str
    android_device: str
    android_client_data: str
    android_second_display: str
    android_account: str

    # --- Web (optional; see tools/web and ADR-0008) ---
    web_port: int
    ws_bridge_port: int

    # --- Steam Deck (optional; see tools/steamdeck and ADR-0018) ---
    # The host, key and known-hosts file are the user's own network and live
    # in config.local.bat; the committed defaults are empty.
    deck_host: str
    deck_user: str
    deck_ssh_key: Path | None
    deck_known_hosts: Path | None
    deck_install_dir: str
    deck_client_data: str
    deck_account: str

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
    def upstream_build(self) -> Path:
        r"""Where ClassicUO is built. Out of tree: sources\ stays pristine."""
        return self.build / "cuo"

    @property
    def upstream_exe(self) -> Path:
        return self.upstream_build / "cuo.exe"

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
    shared = root / "launchers" / "_shared"
    from_bat = parse_config_bat(shared / "config.bat")
    local = shared / "config.local.bat"
    if local.is_file():
        # config.bat calls it first, and its own lines are all guarded, so
        # whatever the local file sets wins over the defaults.
        from_bat.update(parse_config_bat(local))

    def get(key: str, default: str = "") -> str:
        # Environment wins, exactly as in common.bat.
        return os.environ.get(key) or from_bat.get(key) or default

    try:
        shard_port = int(get("UO_SHARD_PORT", "2593"))
    except ValueError:
        shard_port = 2593

    cache = get("UO_CACHE_DIR") or str(Path.home() / ".cache" / "GUO")

    def path_or_none(key: str) -> Path | None:
        # A value that still holds an unexpanded %VAR% is one whose variable
        # was not set anywhere -- JAVA_HOME on a machine without one -- and
        # means "not configured", not a folder called %JAVA_HOME%.
        # %UO_ROOT% is common.bat's, not the environment's: it is this root.
        raw = os.path.expandvars(get(key).replace("%UO_ROOT%", str(root)))
        return Path(raw) if raw and "%" not in raw else None

    package = get("UO_ANDROID_PACKAGE", "org.guo.client")

    try:
        web_port = int(get("UO_WEB_PORT", "8060"))
    except ValueError:
        web_port = 8060
    try:
        ws_bridge_port = int(get("UO_WS_BRIDGE_PORT", "2594"))
    except ValueError:
        ws_bridge_port = 2594
    # config.bat builds this on UO_ROOT, which common.bat sets before calling
    # it; outside a launcher it is this repo's root.
    world = get("UO_WORLD_PROJECT") or str(root / "build" / "world" / "default")
    world = world.replace("%UO_ROOT%", str(root))

    def home_path_or_none(key: str) -> Path | None:
        # A key file the user named with ~ or %USERPROFILE%; empty = unset.
        raw = os.path.expandvars(get(key))
        return Path(os.path.expanduser(raw)) if raw and "%" not in raw else None

    return Config(
        deck_host=get("UO_DECK_HOST", ""),
        deck_user=get("UO_DECK_USER", "deck"),
        deck_ssh_key=home_path_or_none("UO_DECK_SSH_KEY"),
        deck_known_hosts=home_path_or_none("UO_DECK_KNOWN_HOSTS"),
        deck_install_dir=get("UO_DECK_INSTALL_DIR", "~/GUO"),
        deck_client_data=get("UO_DECK_CLIENT_DATA", "~/UO"),
        deck_account=get("UO_DECK_ACCOUNT", ""),
        web_port=web_port,
        ws_bridge_port=ws_bridge_port,
        android_sdk=path_or_none("UO_ANDROID_SDK")
        or Path(os.path.expandvars("%LOCALAPPDATA%")) / "Android" / "Sdk",
        android_jdk=path_or_none("UO_ANDROID_JDK"),
        android_keystore=path_or_none("UO_ANDROID_KEYSTORE")
        or Path(os.path.expandvars("%APPDATA%")) / "Godot" / "keystores" / "debug.keystore",
        android_keystore_user=get("UO_ANDROID_KEYSTORE_USER", "androiddebugkey"),
        android_keystore_password=get("UO_ANDROID_KEYSTORE_PASSWORD", "android"),
        android_package=package,
        android_device=get("UO_ANDROID_DEVICE", ""),
        android_client_data=get("UO_ANDROID_CLIENT_DATA", f"/sdcard/Android/data/{package}/files/uo"),
        android_second_display=get("UO_ANDROID_SECOND_DISPLAY", ""),
        android_account=get("UO_ANDROID_ACCOUNT", ""),
        root=root,
        godot_version=get("GODOT_VERSION", "4.7.2-stable"),
        godot_flavor=get("GODOT_FLAVOR", "mono_win64"),
        client_data=Path(os.path.expandvars(get("UO_CLIENT_DATA"))),
        client_version=get("UO_CLIENT_VERSION", "7.0.15.1"),
        cache_dir=Path(os.path.expandvars(cache)),
        world_project=Path(os.path.expandvars(world)),
        editor_live_host=get("UO_EDITOR_LIVE_HOST", "127.0.0.1"),
        editor_live_port=int(get("UO_EDITOR_LIVE_PORT", "2595") or 2595),
        editor_name=os.path.expandvars(get("UO_EDITOR_NAME", os.environ.get("USERNAME", "editor"))),
        shard_name=get("UO_SHARD_NAME", "GUO Dev"),
        shard_host=get("UO_SHARD_HOST", "127.0.0.1"),
        shard_port=shard_port,
        shard_owner=get("UO_SHARD_OWNER", "guoprobe"),
        shard_owner_password=get("UO_SHARD_OWNER_PASSWORD", "guoprobe"),
        shard_gm_accounts=tuple(
            a.strip() for a in get("UO_SHARD_GM_ACCOUNTS", "guoeffects,guohighlight,guosweep").split(",") if a.strip()
        ),
        log_level=get("UO_LOG_LEVEL", "INFO"),
    )
