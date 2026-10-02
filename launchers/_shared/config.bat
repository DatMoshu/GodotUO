@echo off
REM ============================================================================
REM  GUO - project configuration
REM
REM  THIS IS THE ONLY FILE YOU SHOULD EDIT to point the project at your machine.
REM  Every launcher and tool reads its paths from here.
REM
REM  Resolution order for every setting (first hit wins):
REM      1. an already-set environment variable
REM      2. config.local.bat, next to this file (yours; gitignored)
REM      3. this file
REM      4. the central shared config (%UO_COMMON_CONFIG%), if you use one
REM
REM  Because of (1), nothing here overwrites a value you exported yourself, so
REM  CI and one-off overrides work without editing this file.
REM
REM  YOUR OWN PATHS go in config.local.bat, next to this file. It is gitignored,
REM  so your install location never ends up in a commit or a diff. Copy
REM  config.local.bat.example to start one. It is read before the defaults
REM  below, so anything it sets wins over them (and loses to the environment,
REM  as long as it keeps the `if not defined` guard).
REM ============================================================================

if exist "%~dp0config.local.bat" call "%~dp0config.local.bat"

REM --- Pinned engine ------------------------------------------------------
REM  Version string must match the folder under tools\godot.
if not defined GODOT_VERSION        set "GODOT_VERSION=4.7.2-stable"
if not defined GODOT_FLAVOR         set "GODOT_FLAVOR=mono_win64"

REM  Leave GODOT_EXE unset to use the pinned build in tools\godot.
REM  Set it to an absolute path to use a Godot installed elsewhere.
REM  if not defined GODOT_EXE       set "GODOT_EXE=C:\Path\To\Godot.exe"

REM --- UO client data -----------------------------------------------------
REM  Folder holding the .mul / .uop / .idx files the client reads at runtime.
REM  This is YOUR legally-obtained Ultima Online install. Nothing is copied
REM  into this repo and nothing here is ever committed.
REM  There is no default: set it in config.local.bat, or in the environment.
REM  if not defined UO_CLIENT_DATA   set "UO_CLIENT_DATA=C:\Path\To\Ultima Online Classic"

REM  Client version the data above corresponds to. Drives which file formats
REM  and packet layouts the readers expect. See docs\data_formats.md.
if not defined UO_CLIENT_VERSION    set "UO_CLIENT_VERSION=7.0.107.76"

REM --- Runtime cache ------------------------------------------------------
REM  Where decoded textures/atlases are cached. Safe to delete at any time;
REM  it is rebuilt on demand. Keep it OFF the repo tree.
if not defined UO_CACHE_DIR         set "UO_CACHE_DIR=%LOCALAPPDATA%\GUO\cache"

REM --- Agent request queue ------------------------------------------------
REM  One SQLite file per user, shared by the editor chat window and the AI
REM  agent sessions that watch it. Keep it OFF the repo tree. See
REM  tools\agent_queue\README.md.
if not defined UO_AGENT_QUEUE       set "UO_AGENT_QUEUE=%APPDATA%\GUO\agent_queue.db"

REM --- World project (the editor) -----------------------------------------
REM  Where the editor keeps map edits: whole replaced blocks laid over the
REM  install, never written into it. See docs\data_formats.md section 9 and
REM  docs\architecture\ADR-0011-world-project-overlay.md.
if not defined UO_WORLD_PROJECT     set "UO_WORLD_PROJECT=%UO_ROOT%\build\world\default"

REM  The live tier: the editor bridge of the shard the UO Shard dock connects
REM  to (tools\editor_shard\bridge; the private instance by default), and the
REM  name this editor shows to other editors there. ADR-0012.
if not defined UO_EDITOR_LIVE_HOST  set "UO_EDITOR_LIVE_HOST=127.0.0.1"
if not defined UO_EDITOR_LIVE_PORT  set "UO_EDITOR_LIVE_PORT=2595"
if not defined UO_EDITOR_NAME       set "UO_EDITOR_NAME=%USERNAME%"

REM --- Shard to connect to ------------------------------------------------
if not defined UO_SHARD_HOST        set "UO_SHARD_HOST=127.0.0.1"
if not defined UO_SHARD_PORT        set "UO_SHARD_PORT=2593"

REM --- Local dev shard (ModernUO) -----------------------------------------
REM  The server the client is developed against. See tools\modernuo\README.md.
REM  Set UO_SHARD_HOST above to something else to play on a remote shard; none
REM  of this is needed then.
if not defined UO_SHARD_NAME        set "UO_SHARD_NAME=GUO Dev"
if not defined UO_SHARD_REPO        set "UO_SHARD_REPO=https://github.com/modernuo/ModernUO.git"
REM  The ModernUO commit the dev shard is pinned to (fetch.bat checks it out).
if not defined UO_SHARD_REF         set "UO_SHARD_REF=d4531cd94b739613155225c234900de9f47d2c88"
if not defined UO_SHARD_SRC         set "UO_SHARD_SRC=%UO_ROOT%\tools\modernuo\src"
if not defined UO_SHARD_DIST        set "UO_SHARD_DIST=%UO_SHARD_SRC%\Distribution"

REM  The dev shard's owner account. On a headless boot the shard makes sure
REM  this account exists and has owner access, which is what lets the world be
REM  generated and administered from the client -- ModernUO takes its commands
REM  in game, not at the console. Local dev shard only; not a credential.
if not defined UO_SHARD_OWNER       set "UO_SHARD_OWNER=guoprobe"
if not defined UO_SHARD_OWNER_PASSWORD set "UO_SHARD_OWNER_PASSWORD=guoprobe"

REM  Accounts for scripted clients that run beside the owner (multi_client.bat):
REM  the shard refuses a second character from one account, and "[go" takes
REM  staff access. Made on a headless boot with game master access; each one's
REM  password is its name. Comma-separated. Local dev shard only.
if not defined UO_SHARD_GM_ACCOUNTS  set "UO_SHARD_GM_ACCOUNTS=guoeffects,guohighlight,guosweep"

REM  How far from a player the shard bothers to send items and mobiles. UO's
REM  own answer is 18 tiles, which was a little more than a 640x480 screen and
REM  is a fraction of a modern one: the client draws map art some 70 tiles out,
REM  so doors, signs, decoration and NPCs stop dead in a circle while the
REM  terrain carries on, and things at its edge appear and vanish as you walk.
REM  72 covers a 4K window. Lower it if the shard struggles; 18 is what a
REM  production shard sends, and is what you want if you are checking parity.
if not defined UO_SHARD_UPDATE_RANGE set "UO_SHARD_UPDATE_RANGE=72"

REM --- Android (optional) -------------------------------------------------
REM  Only launchers\android\*.bat and tools\android read these. See
REM  tools\android\README.md for the one-time setup and ADR-0017 for why.
REM  The Android SDK root (the folder holding platform-tools\, build-tools\,
REM  platforms\). Godot's own default on Windows is the value below.
if not defined UO_ANDROID_SDK       set "UO_ANDROID_SDK=%LOCALAPPDATA%\Android\Sdk"
REM  A JDK 17. Godot reads it from its editor settings, not from JAVA_HOME,
REM  so the tool copies this value there. Leave it as JAVA_HOME if that is
REM  already a JDK 17.
if not defined UO_ANDROID_JDK       set "UO_ANDROID_JDK=%JAVA_HOME%"
REM  The debug keystore a debug APK is signed with. Not a secret: the
REM  standard Android debug key, generated on demand by
REM  `python tools\android\run.py keystore` with the standard credentials.
if not defined UO_ANDROID_KEYSTORE  set "UO_ANDROID_KEYSTORE=%APPDATA%\Godot\keystores\debug.keystore"
if not defined UO_ANDROID_KEYSTORE_USER set "UO_ANDROID_KEYSTORE_USER=androiddebugkey"
if not defined UO_ANDROID_KEYSTORE_PASSWORD set "UO_ANDROID_KEYSTORE_PASSWORD=android"
REM  The application id, and the adb serial of the device to use when more
REM  than one is attached (leave empty for the only one).
if not defined UO_ANDROID_PACKAGE   set "UO_ANDROID_PACKAGE=org.guo.client"
if not defined UO_ANDROID_DEVICE    set "UO_ANDROID_DEVICE="
REM  Where the UO client data lives ON THE DEVICE: the app's own external
REM  files folder, which adb, a file manager and the app can all reach with
REM  no permission prompt. Push your install there with
REM  `python tools\android\run.py push`. It is deleted with the app.
if not defined UO_ANDROID_CLIENT_DATA set "UO_ANDROID_CLIENT_DATA=/sdcard/Android/data/%UO_ANDROID_PACKAGE%/files/uo"
REM  A dual-screen device (AYN Thor): the SurfaceFlinger id of the second
REM  display, for `screencap -d`. Leave empty and the tools read it from
REM  `dumpsys display` (the FLAG_PRESENTATION display's uniqueId). Only the
REM  dual_probe launcher and the doctor use it; the client finds the display
REM  itself. See docs\architecture\ADR-0009-second-display.md.
if not defined UO_ANDROID_SECOND_DISPLAY set "UO_ANDROID_SECOND_DISPLAY="
REM  The shard account an exported APK logs in as (baked in as --account;
REM  the password defaults to the account name). Empty = none baked in, the
REM  login screen as always. Set it in config.local.bat, never here.
if not defined UO_ANDROID_ACCOUNT   set "UO_ANDROID_ACCOUNT="

REM  The app icons (Windows and Android) are built from design\brand\
REM  guo-sigil.png, committed in this repository: python tools\brand\run.py.
REM  Nothing to configure.
REM --- Web (optional) -----------------------------------------------------
REM  Only launchers\web\*.bat and tools\web read this. The port the local
REM  web server (tools\web\run.py serve) listens on; it serves build\web with
REM  the cross-origin isolation headers a Godot web export needs.
if not defined UO_WEB_PORT          set "UO_WEB_PORT=8060"
REM  The WebSocket bridge the web client reaches the shard through
REM  (tools\ws_bridge, launchers\web\ws_bridge.bat): ws://127.0.0.1:<this>
REM  relays to UO_SHARD_HOST:UO_SHARD_PORT. Upstream's test proxy uses 2594.
if not defined UO_WS_BRIDGE_PORT    set "UO_WS_BRIDGE_PORT=2594"
REM  LAN mode: 1 serves the page and the bridge to phones on this network
REM  (https and wss, on this PC's private address only; tools\web\README.md,
REM  "Play on a phone"). Off by default and never on here: set it in
REM  config.local.bat. UO_WEB_LAN_HOST picks the address when the PC has
REM  several; empty finds it. Never commit an address.
if not defined UO_WEB_LAN           set "UO_WEB_LAN=0"
if not defined UO_WEB_LAN_HOST      set "UO_WEB_LAN_HOST="
REM  The Godot build that can export C# to the web: a community 4.7.2 mono
REM  build kept apart from the pinned engine, with its own .NET SDK, in
REM  tools\godot_web (gitignored; see its README). Only tools\web uses it.
if not defined UO_WEB_GODOT         set "UO_WEB_GODOT=%UO_ROOT%\tools\godot_web\Godot_v4.7.2-stable_mono_web_export_win64\Godot_v4.7.2-stable_mono_web_export_win64_console.exe"

REM --- Steam Deck (optional) ----------------------------------------------
REM  Only launchers\steamdeck\*.bat and tools\steamdeck read these. See
REM  docs\steamdeck.md for the one-time setup and ADR-0018 for why. The
REM  Deck is reached over ssh; nothing here is a credential, but the host,
REM  the key path and the known-hosts file are YOUR network, so set them in
REM  config.local.bat and leave these empty.
REM  The Deck's address on your LAN (an IP or a name ssh resolves).
if not defined UO_DECK_HOST         set "UO_DECK_HOST="
REM  The ssh user. SteamOS has exactly one: deck.
if not defined UO_DECK_USER         set "UO_DECK_USER=deck"
REM  The private key whose .pub is in the Deck's ~/.ssh/authorized_keys.
REM  Empty = ssh's own default keys (~/.ssh/id_ed25519 and friends).
if not defined UO_DECK_SSH_KEY      set "UO_DECK_SSH_KEY="
REM  A known_hosts file to pin the Deck's host key in. Empty = ssh's default
REM  (~/.ssh/known_hosts), with a new key accepted on first contact.
if not defined UO_DECK_KNOWN_HOSTS  set "UO_DECK_KNOWN_HOSTS="
REM  Where the exported build is installed ON THE DECK. `push` writes the
REM  executable, the .pck, the .NET data folder and guo.sh there.
if not defined UO_DECK_INSTALL_DIR  set "UO_DECK_INSTALL_DIR=~/GUO"
REM  Where the UO client data lives ON THE DECK (the folder holding the
REM  .uop / .mul files). Copy your install there yourself, from Desktop
REM  mode or with scp; the tools never push it. An SD card path looks like
REM  /run/media/deck/<card>/UO.
if not defined UO_DECK_CLIENT_DATA  set "UO_DECK_CLIENT_DATA=~/UO"
REM  The shard account guo.sh passes as --account (the password defaults to
REM  the account name). Empty = none. Set it in config.local.bat, never here.
if not defined UO_DECK_ACCOUNT      set "UO_DECK_ACCOUNT="

REM --- Python -------------------------------------------------------------
if not defined UO_PYTHON            set "UO_PYTHON=python"

REM --- Logging ------------------------------------------------------------
REM  DEBUG | INFO | WARN | ERROR
if not defined UO_LOG_LEVEL         set "UO_LOG_LEVEL=INFO"

REM --- GUO Asset Store ----------------------------------------------------
if not defined UO_STORE_DIR         set "UO_STORE_DIR=build/store_cdn"
if not defined UO_STORE_URL         set "UO_STORE_URL=http://127.0.0.1:18865"
REM  A signed catalogue (ADR-0026): the store folder gets a guo/store-index@2
REM  index signed with this key. Keep the key out of the repo (config.local.bat).
REM  UO_STORE_BASE_URL is where the store folder is served, for absolute pack URLs.
if not defined UO_STORE_SIGNING_KEY set "UO_STORE_SIGNING_KEY="
if not defined UO_STORE_CATALOGUE_ID set "UO_STORE_CATALOGUE_ID=local"
if not defined UO_STORE_CATALOGUE_TITLE set "UO_STORE_CATALOGUE_TITLE=Local GUO packs"
if not defined UO_STORE_BASE_URL    set "UO_STORE_BASE_URL="
