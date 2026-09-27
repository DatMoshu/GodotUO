# Configuration

One file holds every setting with its default: `launchers\_shared\config.bat`.
Your machine's values go in `launchers\_shared\config.local.bat` (gitignored,
copied from `config.local.bat.example`). Every launcher, tool and runtime
resolves the same values in the same order:

1. an **environment variable** already set in the shell;
2. **`config.local.bat`**, yours;
3. **`config.bat`**, the committed defaults;
4. an optional **central shared config** named by `UO_COMMON_CONFIG`, for a
   machine hosting several projects.

`tools\guo\config.py` parses the same `.bat` files when a tool runs outside a
launcher, so the editor, an agent and CI see the same paths. No launcher,
tool or runtime hardcodes a path. CI rejects a commit that puts a machine
path anywhere but `config.local.bat`.

`common.bat` derives `UO_ROOT`, `UO_GODOT_PROJECT`, `UO_SOURCES`, `UO_TOOLS`,
`UO_DOCS`, `UO_BUILD`, `GODOT_EXE` and `GODOT_CONSOLE` from these; they are
not settings.

## Engine

| Key | Default | Meaning |
|---|---|---|
| `GODOT_VERSION` | `4.7.2-stable` | Pinned engine version the bootstrap fetches. |
| `GODOT_FLAVOR` | `mono_win64` | Must be a mono/.NET flavour; the plain build cannot run C#. |
| `GODOT_EXE` | unset | Override the engine executable. Leave unset to use `tools\godot`. |

## Client data and cache

| Key | Default | Meaning |
|---|---|---|
| `UO_CLIENT_DATA` | none, **required** | Folder of your UO install (holds `tiledata.mul` and the rest). Read in place, never written. |
| `UO_CLIENT_VERSION` | `7.0.107.76` | The client version of that install; `01_verify_client_data.bat` reports what it sees. |
| `UO_CACHE_DIR` | `%LOCALAPPDATA%\GUO\cache` | The runtime decode cache. Safe to delete. |

## Shard (the server the client connects to)

| Key | Default | Meaning |
|---|---|---|
| `UO_SHARD_HOST` | `127.0.0.1` | Host the client logs in to. |
| `UO_SHARD_PORT` | `2593` | Its port. |
| `UO_SHARD_NAME` | `GUO Dev` | The name the local dev shard announces. |
| `UO_SHARD_REPO` | `https://github.com/modernuo/ModernUO.git` | Where `launchers\shard\fetch.bat` clones from. |
| `UO_SHARD_SRC` | `%UO_ROOT%\tools\modernuo\src` | The ModernUO checkout (gitignored, about 1 GB built). |
| `UO_SHARD_DIST` | `%UO_SHARD_SRC%\Distribution` | Its built distribution. |
| `UO_SHARD_OWNER` | set in `config.bat` | The account the headless boot raises to owner, and the one `playtest.bat` and `populate.bat` log in as. |
| `UO_SHARD_OWNER_PASSWORD` | set in `config.bat` | Its password. Change both on any shard someone else can reach. |
| `UO_SHARD_GM_ACCOUNTS` | `guoeffects,guohighlight,guosweep` | Comma-separated accounts the headless boot creates with game master access, one per `multi_client.bat` lane. |
| `UO_SHARD_UPDATE_RANGE` | `72` | The shard's update range in tiles (patch `0002-settable-update-range`), so a zoomed-out client sees the whole screen. |

## Editor (the Godot editor add-on)

| Key | Default | Meaning |
|---|---|---|
| `UO_WORLD_PROJECT` | `%UO_ROOT%\build\world\default` | The world project folder the editor edits and `tools\world` exports. |
| `UO_EDITOR_LIVE_HOST` | `127.0.0.1` | The bridge the editor's live tier connects to (ADR-0012). |
| `UO_EDITOR_LIVE_PORT` | `2595` | Its port. |
| `UO_EDITOR_NAME` | `%USERNAME%` | The name an editor announces on the bridge. |

## Android

| Key | Default | Meaning |
|---|---|---|
| `UO_ANDROID_SDK` | `%LOCALAPPDATA%\Android\Sdk` | Android SDK with `platform-tools` and `build-tools`. |
| `UO_ANDROID_JDK` | `%JAVA_HOME%` | A JDK 17. |
| `UO_ANDROID_KEYSTORE` | `%APPDATA%\Godot\keystores\debug.keystore` | The debug keystore; `run.py keystore` makes it. |
| `UO_ANDROID_KEYSTORE_USER` | `androiddebugkey` | The standard Android debug alias. |
| `UO_ANDROID_KEYSTORE_PASSWORD` | `android` | The standard Android debug password. Not a secret; a release keystore is your own. |
| `UO_ANDROID_PACKAGE` | `org.guo.client` | The application id. |
| `UO_ANDROID_DEVICE` | empty | An adb serial. Empty means the single device in state `device`; set it with two attached. |
| `UO_ANDROID_CLIENT_DATA` | `/sdcard/Android/data/%UO_ANDROID_PACKAGE%/files/uo` | Where `run.py push` puts the UO data on the device, baked into the APK as `--client-data`. |
| `UO_ANDROID_SECOND_DISPLAY` | empty | Force a display id for the second screen; empty lets the client pick (see [Dual Screen](Dual-Screen.md)). |

## Web

| Key | Default | Meaning |
|---|---|---|
| `UO_WEB_PORT` | `8060` | Port `launchers\web\serve.bat` serves `build\web` on. The export itself is blocked upstream, see [FAQ](FAQ.md). |
| `UO_WEB_BROWSER` | unset | A browser executable for the web smoke; Chrome or Edge is found otherwise. |

## Tooling

| Key | Default | Meaning |
|---|---|---|
| `UO_PYTHON` | `python` | The interpreter every tool runs under. |
| `UO_LOG_LEVEL` | `INFO` | Log level for the Python tools. |
| `UO_COMMON_CONFIG` | unset | Path of a central config `.bat` read after `config.bat`, for a repo hosting several projects. |
| `UO_PROFILE_PLATFORM` | unset | Runtime only: force the per-platform profile table (`desktop`, `mobile`, `web`) on the desktop, for checks. See [Mobile UI](Mobile-UI.md). |
| `GUO_BRIDGE_MAPS` | `0,1,2,3,4,5` | Which maps the editor's ModernUO bridge assembly serves (ADR-0012). |

## Where the client keeps its own settings

Like ClassicUO, the client writes `settings.json`, the per-character profiles
and `gumps.xml` under its working directory, which the host sets before the
client namespace is touched (ADR-0006). Scripted runs that need a clean home
pass `--cache-dir` and get their own copy; the real settings are never edited
by a probe.
