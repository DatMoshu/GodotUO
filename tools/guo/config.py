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
import platform
import re
import sys
from dataclasses import dataclass
from pathlib import Path

# `if not defined NAME set "NAME=VALUE"` / `set "NAME=VALUE"`
_SET_RE = re.compile(
    r'^\s*(?:if\s+not\s+defined\s+(?P<guard>\w+)\s+)?set\s+"(?P<key>[A-Za-z_][A-Za-z0-9_]*)=(?P<val>[^"]*)"',
    re.IGNORECASE,
)
_VAR_RE = re.compile(r"%([A-Za-z_][A-Za-z0-9_]*)%")


def platform_godot_flavor() -> str:
    """The Godot .NET release asset this machine runs, as named on the releases page."""
    if sys.platform == "win32":
        return "mono_win64"
    if sys.platform == "darwin":
        return "mono_macos.universal"
    arch = "arm64" if platform.machine().lower() in ("aarch64", "arm64") else "x86_64"
    return f"mono_linux_{arch}"


def native_path(value: str) -> str:
    r"""config.bat writes its paths with backslashes; elsewhere they are slashes.

    "%UO_ROOT%\build\world" must become a folder two levels down on Linux,
    not one file whose name contains backslashes.
    """
    return value if sys.platform == "win32" else value.replace("\\", "/")


def godot_data_dir() -> Path:
    """Where the Godot editor keeps export templates: %APPDATA%\\Godot on Windows,
    $XDG_DATA_HOME/godot (~/.local/share/godot) on Linux."""
    if sys.platform == "win32":
        return Path(os.environ.get("APPDATA", str(Path.home() / "AppData" / "Roaming"))) / "Godot"
    if sys.platform == "darwin":
        return Path.home() / "Library" / "Application Support" / "Godot"
    return Path(os.environ.get("XDG_DATA_HOME") or Path.home() / ".local" / "share") / "godot"


def godot_config_dir() -> Path:
    """Where the Godot editor keeps editor_settings-*.tres: the same folder as
    the templates on Windows and macOS, $XDG_CONFIG_HOME/godot on Linux."""
    if sys.platform in ("win32", "darwin"):
        return godot_data_dir()
    return Path(os.environ.get("XDG_CONFIG_HOME") or Path.home() / ".config") / "godot"


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


def parse_config_bat(path: Path, initial: dict[str, str] | None = None) -> dict[str, str]:
    """Extract the settings from config.bat without executing it."""
    values = {key.upper(): value for key, value in (initial or {}).items()}
    environment = {key.upper(): value for key, value in os.environ.items()}
    for line in path.read_text(encoding="utf-8-sig", errors="replace").splitlines():
        match = _SET_RE.match(line)
        if not match:
            continue
        key, raw = match.group("key").upper(), match.group("val")
        guard = match.group("guard")
        if guard and (environment.get(guard.upper()) or values.get(guard.upper())):
            continue

        def expand(m: re.Match[str]) -> str:
            name = m.group(1).upper()
            # Prefer a real environment value, then one defined earlier in the file.
            return environment.get(name) or values.get(name) or m.group(0)

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
    store_dir: Path
    store_url: str

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
    web_godot: Path | None
    # LAN mode (tools/web/lan.py): serve and the bridge on this PC's private
    # address, over TLS, for a phone on the same network. Off by default; the
    # address is found at run time unless web_lan_host (config.local.bat) says.
    web_lan: bool
    web_lan_host: str

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

    # --- client data sources (ADR-0021; tools/guo/datasources.py) ---
    # The raw configured values, kept apart so the resolver can tell where
    # client_data came from. client_data above is the first VALID candidate
    # in the ADR's order, or the first configured one when none is valid.
    client_data_env: str = ""
    client_data_setting: str = ""
    custom_data_setting: str = ""
    # UO_SHARD_SRC, when set: another ModernUO checkout (config.bat honours it too).
    shard_src_setting: Path | None = None
    # The store folder's signed catalogue (ADR-0026); no key means an unsigned v1 index.
    store_signing_key: Path | None = None
    store_catalogue_id: str = "local"
    store_catalogue_title: str = "Local GUO packs"
    store_base_url: str = ""
    # The agent request queue (tools/agent_queue): one SQLite file per user, outside the repo.
    agent_queue: Path | None = None

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
        r"""The ModernUO checkout (UO_SHARD_SRC). Untracked; launchers\shard\fetch.bat makes it."""
        return self.shard_src_setting or self.tools / "modernuo" / "src"

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
    def godot_dir(self) -> Path:
        """The extracted release folder under tools/godot."""
        return self.tools / "godot" / f"Godot_v{self.godot_version}_{self.godot_flavor}"

    @property
    def godot_exe(self) -> Path:
        stem = f"Godot_v{self.godot_version}_{self.godot_flavor}"
        if self.godot_flavor.startswith("mono_linux_"):
            # The Linux zip names its binary with the arch after a dot:
            # Godot_v4.7.2-stable_mono_linux_x86_64/Godot_v4.7.2-stable_mono_linux.x86_64
            arch = self.godot_flavor.removeprefix("mono_linux_")
            return self.godot_dir / f"Godot_v{self.godot_version}_mono_linux.{arch}"
        return self.godot_dir / f"{stem}.exe"

    @property
    def godot_console_exe(self) -> Path:
        # An engine somewhere else (a worktree without the tools\godot junction):
        # the same GODOT_CONSOLE variable the launchers set.
        override = os.environ.get("GODOT_CONSOLE")
        if override and Path(override).is_file():
            return Path(override)
        # Only Windows ships a separate console build; elsewhere the one
        # binary already blocks and writes to stdout.
        if not self.godot_flavor.startswith("mono_win"):
            return self.godot_exe
        stem = f"Godot_v{self.godot_version}_{self.godot_flavor}"
        return self.godot_dir / f"{stem}_console.exe"


def load_config(root: Path | None = None) -> Config:
    """Resolve configuration from the environment, falling back to config.bat."""
    root = (root or find_repo_root()).resolve()
    shared = root / "launchers" / "_shared"
    from_bat = {"UO_ROOT": str(root)}
    local = shared / "config.local.bat"
    if local.is_file():
        # config.bat calls it first, and its own lines are all guarded, so
        # whatever the local file sets wins over the defaults.
        from_bat = parse_config_bat(local, from_bat)
    # Resolve in batch execution order: local settings must be available to
    # references in guarded defaults, including values based on UO_ROOT.
    from_bat = parse_config_bat(shared / "config.bat", from_bat)
    environment = {key.upper(): value for key, value in os.environ.items()}

    def get(key: str, default: str = "") -> str:
        # Environment wins, exactly as in common.bat.
        return environment.get(key.upper()) or from_bat.get(key.upper()) or default

    try:
        shard_port = int(get("UO_SHARD_PORT", "2593"))
    except ValueError:
        shard_port = 2593

    # config.bat's default is built on %LOCALAPPDATA%, which only Windows
    # sets; left unexpanded it would be a relative folder named "%LOCALAPPDATA%".
    # The client keeps its profiles in the cache's parent (Main.GuoDataDirectory),
    # so elsewhere the same layout goes under the user's data folder.
    cache = native_path(os.path.expandvars(get("UO_CACHE_DIR")))
    if not cache or "%" in cache:
        data_home = os.environ.get("XDG_DATA_HOME") or str(Path.home() / ".local" / "share")
        cache = str(Path(data_home) / "GUO" / "cache")

    # %APPDATA% is Windows-only; elsewhere the same file goes under the user's config folder.
    agent_queue = native_path(os.path.expandvars(get("UO_AGENT_QUEUE")))
    if not agent_queue or "%" in agent_queue:
        config_home = os.environ.get("XDG_CONFIG_HOME") or os.path.join(os.path.expanduser("~"), ".config")
        agent_queue = str(Path(config_home) / "guo" / "agent_queue.db")

    def path_or_none(key: str) -> Path | None:
        # A value that still holds an unexpanded %VAR% is one whose variable
        # was not set anywhere -- JAVA_HOME on a machine without one -- and
        # means "not configured", not a folder called %JAVA_HOME%.
        # %UO_ROOT% is common.bat's, not the environment's: it is this root.
        raw = native_path(os.path.expandvars(get(key).replace("%UO_ROOT%", str(root))))
        return Path(raw) if raw and "%" not in raw else None

    # config.bat pins the Windows build; a Windows binary cannot run anywhere
    # else, so on another OS that default means "this OS's build".
    godot_flavor = get("GODOT_FLAVOR") or platform_godot_flavor()
    if sys.platform != "win32" and godot_flavor.startswith("mono_win"):
        godot_flavor = platform_godot_flavor()

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
    world = native_path(world.replace("%UO_ROOT%", str(root)))

    def home_path_or_none(key: str) -> Path | None:
        # A key file the user named with ~ or %USERPROFILE%; empty = unset.
        raw = os.path.expandvars(get(key))
        return Path(os.path.expanduser(raw)) if raw and "%" not in raw else None
    store = Path(native_path(os.path.expandvars(get("UO_STORE_DIR", "build/store_cdn").replace("%UO_ROOT%", str(root)))))
    if not store.is_absolute():
        store = root / store

    # ADR-0021: environment, then the saved setting (config.local.bat or the
    # central config; config.bat ships none), then the platform default; the
    # first that is a valid data set wins. None valid: the first configured
    # value, so a tool can still say what it looked at.
    def raw(value: str) -> str:
        value = os.path.expandvars(value.replace("%UO_ROOT%", str(root))) if value else ""
        return "" if "%" in value else value

    client_data_env = raw(environment.get("UO_CLIENT_DATA", ""))
    client_data_setting = raw(from_bat.get("UO_CLIENT_DATA", ""))
    custom_data_setting = raw(get("UO_CUSTOM_DATA"))
    from .datasources import resolve

    found = resolve("", client_data_env, client_data_setting)
    client_data = found.client_data or Path(client_data_env or client_data_setting)

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
        web_godot=path_or_none("UO_WEB_GODOT"),
        web_lan=get("UO_WEB_LAN", "0").strip() == "1",
        web_lan_host=get("UO_WEB_LAN_HOST", "").strip(),
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
        store_dir=store,
        store_url=get("UO_STORE_URL", "http://127.0.0.1:18865"),
        store_signing_key=home_path_or_none("UO_STORE_SIGNING_KEY"),
        store_catalogue_id=get("UO_STORE_CATALOGUE_ID", "local"),
        store_catalogue_title=get("UO_STORE_CATALOGUE_TITLE", "Local GUO packs"),
        store_base_url=get("UO_STORE_BASE_URL"),
        godot_version=get("GODOT_VERSION", "4.7.2-stable"),
        godot_flavor=godot_flavor,
        client_data=client_data,
        client_data_env=client_data_env,
        client_data_setting=client_data_setting,
        custom_data_setting=custom_data_setting,
        client_version=get("UO_CLIENT_VERSION", "7.0.15.1"),
        cache_dir=Path(cache),
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
        shard_src_setting=path_or_none("UO_SHARD_SRC"),
        agent_queue=Path(agent_queue),
    )
