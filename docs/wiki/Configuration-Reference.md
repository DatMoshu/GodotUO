# Launcher configuration reference

Audit baseline: `main` `fc9e3588`, 2026-10-28.
These are source-discovered entry points, not new execution receipts.
Read the linked README/source before running a tool; dependencies, approvals,
output paths and destructive verbs differ. Generated output belongs outside
the checkout. A shell twin may explain a Windows-only limitation.

This table transcribes active `if not defined … set` defaults from
`launchers/_shared/config.bat`. It omits commented examples and secrets that
have no committed default. Empty means unset/off or supplied by another layer,
depending on the key. Percent expressions resolve when the launcher runs.
Use [Configuration](Configuration.md) for precedence and meanings.

Required/local-only settings such as `UO_CLIENT_DATA`, optional engine/upstream
overrides and credentials are documented next to their comments in
[config.bat](../../launchers/_shared/config.bat). Shell counterparts live in
[config.sh](../../launchers/_shared/config.sh). Never paste your local values
into an issue or this table.

| Key | Committed default expression | Source |
|---|---|---|
| `GODOT_VERSION` | `4.7.2-stable` | [config.bat line 28](../../launchers/_shared/config.bat#L28) |
| `GODOT_FLAVOR` | `mono_win64` | [config.bat line 29](../../launchers/_shared/config.bat#L29) |
| `GUO_WEATHER_PROOF` | `` | [config.bat line 32](../../launchers/_shared/config.bat#L32) |
| `GUO_WEATHER_PROOF_DIR` | `` | [config.bat line 33](../../launchers/_shared/config.bat#L33) |
| `UO_CLIENT_VERSION` | `7.0.107.76` | [config.bat line 58](../../launchers/_shared/config.bat#L58) |
| `UO_BUILD_DIR` | `%UO_ROOT%\build` | [config.bat line 66](../../launchers/_shared/config.bat#L66) |
| `UO_CACHE_DIR` | `%LOCALAPPDATA%\GUO\cache` | [config.bat line 71](../../launchers/_shared/config.bat#L71) |
| `GUO_EDITOR_MCP_PORT` | `` | [config.bat line 85](../../launchers/_shared/config.bat#L85) |
| `GUO_EDITOR_MCP_PYTHON` | `` | [config.bat line 86](../../launchers/_shared/config.bat#L86) |
| `UO_AGENT_QUEUE` | `%APPDATA%\GUO\agent_queue.db` | [config.bat line 91](../../launchers/_shared/config.bat#L91) |
| `UO_WORLD_PROJECT` | `%UO_BUILD_DIR%\world\default` | [config.bat line 104](../../launchers/_shared/config.bat#L104) |
| `UO_MAPGEN_DATA` | `%LOCALAPPDATA%\GUO\mapgen` | [config.bat line 110](../../launchers/_shared/config.bat#L110) |
| `UO_EDITOR_LIVE_HOST` | `127.0.0.1` | [config.bat line 115](../../launchers/_shared/config.bat#L115) |
| `UO_EDITOR_LIVE_PORT` | `2595` | [config.bat line 116](../../launchers/_shared/config.bat#L116) |
| `UO_EDITOR_NAME` | `%USERNAME%` | [config.bat line 117](../../launchers/_shared/config.bat#L117) |
| `UO_SHARD_HOST` | `127.0.0.1` | [config.bat line 120](../../launchers/_shared/config.bat#L120) |
| `UO_SHARD_PORT` | `2593` | [config.bat line 121](../../launchers/_shared/config.bat#L121) |
| `UO_SHARD_NAME` | `GUO Dev` | [config.bat line 127](../../launchers/_shared/config.bat#L127) |
| `UO_SHARD_REPO` | `https://github.com/modernuo/ModernUO.git` | [config.bat line 128](../../launchers/_shared/config.bat#L128) |
| `UO_SHARD_REF` | `d4531cd94b739613155225c234900de9f47d2c88` | [config.bat line 130](../../launchers/_shared/config.bat#L130) |
| `UO_SHARD_SRC` | `%UO_ROOT%\tools\modernuo\src` | [config.bat line 131](../../launchers/_shared/config.bat#L131) |
| `UO_SHARD_DIST` | `%UO_SHARD_SRC%\Distribution` | [config.bat line 132](../../launchers/_shared/config.bat#L132) |
| `UO_PLAYERBOTS_DIR` | `%UO_BUILD_DIR%\playerbots` | [config.bat line 135](../../launchers/_shared/config.bat#L135) |
| `UO_PLAYERBOTS_PORT` | `2640` | [config.bat line 136](../../launchers/_shared/config.bat#L136) |
| `UO_SHARD_BIND` | `127.0.0.1` | [config.bat line 141](../../launchers/_shared/config.bat#L141) |
| `UO_SHARD_OWNER` | Local account name; see source default | [config.bat line 151](../../launchers/_shared/config.bat#L151) |
| `UO_SHARD_GM_ACCOUNTS` | `guoeffects,guohighlight,guosweep` | [config.bat line 157](../../launchers/_shared/config.bat#L157) |
| `UO_SHARD_UPDATE_RANGE` | `72` | [config.bat line 166](../../launchers/_shared/config.bat#L166) |
| `UO_ANDROID_SDK` | `%LOCALAPPDATA%\Android\Sdk` | [config.bat line 173](../../launchers/_shared/config.bat#L173) |
| `UO_ANDROID_JDK` | `%JAVA_HOME%` | [config.bat line 177](../../launchers/_shared/config.bat#L177) |
| `UO_ANDROID_KEYSTORE` | `%APPDATA%\Godot\keystores\debug.keystore` | [config.bat line 181](../../launchers/_shared/config.bat#L181) |
| `UO_ANDROID_KEYSTORE_USER` | `androiddebugkey` | [config.bat line 182](../../launchers/_shared/config.bat#L182) |
| `UO_ANDROID_KEYSTORE_PASSWORD` | `android` | [config.bat line 183](../../launchers/_shared/config.bat#L183) |
| `UO_ANDROID_RELEASE_KEYSTORE` | `` | [config.bat line 190](../../launchers/_shared/config.bat#L190) |
| `UO_ANDROID_RELEASE_KEYSTORE_USER` | `` | [config.bat line 191](../../launchers/_shared/config.bat#L191) |
| `UO_ANDROID_RELEASE_KEYSTORE_PASSWORD` | `` | [config.bat line 192](../../launchers/_shared/config.bat#L192) |
| `UO_ANDROID_PACKAGE` | `org.guo.client` | [config.bat line 195](../../launchers/_shared/config.bat#L195) |
| `UO_ANDROID_DEVICE` | `` | [config.bat line 196](../../launchers/_shared/config.bat#L196) |
| `UO_ANDROID_CLIENT_DATA` | `/sdcard/Android/data/%UO_ANDROID_PACKAGE%/files/uo` | [config.bat line 201](../../launchers/_shared/config.bat#L201) |
| `UO_ANDROID_SECOND_DISPLAY` | `` | [config.bat line 207](../../launchers/_shared/config.bat#L207) |
| `UO_ANDROID_ACCOUNT` | `` | [config.bat line 211](../../launchers/_shared/config.bat#L211) |
| `UO_WEB_PORT` | `8060` | [config.bat line 220](../../launchers/_shared/config.bat#L220) |
| `UO_WS_BRIDGE_PORT` | `2594` | [config.bat line 224](../../launchers/_shared/config.bat#L224) |
| `UO_WEB_LAN` | `0` | [config.bat line 230](../../launchers/_shared/config.bat#L230) |
| `UO_WEB_LAN_HOST` | `` | [config.bat line 231](../../launchers/_shared/config.bat#L231) |
| `UO_WEB_GODOT` | `%UO_ROOT%\tools\godot_web\Godot_v4.7.2-stable_mono_web_export_win64\Godot_v4.7.2-stable_mono_web_export_win64_console.exe` | [config.bat line 235](../../launchers/_shared/config.bat#L235) |
| `UO_DECK_HOST` | `` | [config.bat line 244](../../launchers/_shared/config.bat#L244) |
| `UO_DECK_USER` | `deck` | [config.bat line 246](../../launchers/_shared/config.bat#L246) |
| `UO_DECK_SSH_KEY` | `` | [config.bat line 249](../../launchers/_shared/config.bat#L249) |
| `UO_DECK_KNOWN_HOSTS` | `` | [config.bat line 252](../../launchers/_shared/config.bat#L252) |
| `UO_DECK_INSTALL_DIR` | `~/GUO` | [config.bat line 255](../../launchers/_shared/config.bat#L255) |
| `UO_DECK_CLIENT_DATA` | `~/UO` | [config.bat line 260](../../launchers/_shared/config.bat#L260) |
| `UO_DECK_ACCOUNT` | `` | [config.bat line 263](../../launchers/_shared/config.bat#L263) |
| `UO_LAYOUT_CDDA_DIR` | `` | [config.bat line 266](../../launchers/_shared/config.bat#L266) |
| `UO_LAYOUT_ZOMBOID_DIR` | `` | [config.bat line 267](../../launchers/_shared/config.bat#L267) |
| `UO_PYTHON` | `python` | [config.bat line 270](../../launchers/_shared/config.bat#L270) |
| `UO_BUILD_TIMEOUT` | `180` | [config.bat line 273](../../launchers/_shared/config.bat#L273) |
| `UO_LOG_LEVEL` | `INFO` | [config.bat line 277](../../launchers/_shared/config.bat#L277) |
| `UO_STORE_DIR` | `build/store_cdn` | [config.bat line 280](../../launchers/_shared/config.bat#L280) |
| `UO_STORE_URL` | `http://127.0.0.1:18865` | [config.bat line 281](../../launchers/_shared/config.bat#L281) |
| `UO_STORE_SIGNING_KEY` | `` | [config.bat line 285](../../launchers/_shared/config.bat#L285) |
| `UO_STORE_CATALOGUE_ID` | `local` | [config.bat line 286](../../launchers/_shared/config.bat#L286) |
| `UO_STORE_CATALOGUE_TITLE` | `Local GUO packs` | [config.bat line 287](../../launchers/_shared/config.bat#L287) |
| `UO_STORE_BASE_URL` | `` | [config.bat line 288](../../launchers/_shared/config.bat#L288) |
| `UO_ART_EXCHANGE` | `%UO_BUILD_DIR%\art_exchange` | [config.bat line 296](../../launchers/_shared/config.bat#L296) |
| `UO_PIXELORAMA` | `` | [config.bat line 297](../../launchers/_shared/config.bat#L297) |
| `UO_PINTA` | `` | [config.bat line 298](../../launchers/_shared/config.bat#L298) |
| `UO_COMFY_URL` | `http://127.0.0.1:8188` | [config.bat line 299](../../launchers/_shared/config.bat#L299) |
| `UO_COMFY_DIR` | `` | [config.bat line 300](../../launchers/_shared/config.bat#L300) |
| `UO_COMFY_WORKFLOWS` | `%UO_BUILD_DIR%\art_exchange\workflows` | [config.bat line 301](../../launchers/_shared/config.bat#L301) |
