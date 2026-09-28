# tools/android -- GUO on an Android device

Exports the client as a debug APK, installs it, runs it and smoke-tests it.
The decisions behind it (why ARM64 only, why the data lives where it does,
why the plugin host is compiled out, how touch maps onto the mouse) are in
[ADR-0017](../../docs/architecture/ADR-0017-android-target.md). This file is
the how-to.

```
launchers\android\doctor.bat     what is missing on this machine, with the fix
launchers\android\export.bat     debug APK -> build\android\GUO-debug.apk
launchers\android\install.bat    adb install it
launchers\android\run.bat        start it, stream its logcat
launchers\android\smoke.bat      export + install + run + wait for the login
                                 gump + pull screenshot and log; non-zero on failure
launchers\android\dual_probe.bat export with --dual-probe + install + run + log in
                                 + wait for the second screen + photograph BOTH
                                 displays; non-zero on failure (ADR-0009)
launchers\android\portrait_probe.bat  C9 spike: export with --portrait-probe + run;
                                 turns the screen to portrait and back, photographs
                                 each state, writes build\android\portrait\ (the
                                 table and the photos); see docs\android_portrait_spike.md
launchers\dev\touch_probe.bat    the touch layer, checked on the desktop (no device)
```

Everything is `python tools\android\run.py <command>` underneath; the extra
commands (`templates`, `keystore`, `settings`, `preset`, `push`, `logcat`,
`displays`) are one-time setup steps and have no launcher.

## One-time setup

Godot 4.7's Android export reads three things from **its own editor settings
file** (`%APPDATA%\Godot\editor_settings-4.7.tres`) and from nowhere else: the
Android SDK folder, the JDK folder and the debug keystore. `ANDROID_HOME` and
`JAVA_HOME` are not consulted. The tool writes those three from
`launchers\_shared\config.bat` before every export, so you configure them once,
in the one file you are meant to edit:

```
UO_ANDROID_SDK        default %LOCALAPPDATA%\Android\Sdk
UO_ANDROID_JDK        default %JAVA_HOME%          (must be a JDK 17)
UO_ANDROID_KEYSTORE   default %APPDATA%\Godot\keystores\debug.keystore
UO_ANDROID_PACKAGE    default org.guo.client
UO_ANDROID_DEVICE     default empty = the only attached device
UO_ANDROID_CLIENT_DATA  default /sdcard/Android/data/<package>/files/uo
UO_ANDROID_ACCOUNT    default empty; when set, baked in as --account (config.local.bat only)
```

Then, in this order (`doctor.bat` tells you which are still missing):

1. **JDK 17.** Eclipse Temurin 17. Godot 4.7 will not build with 21+.
   Point `UO_ANDROID_JDK` at it (or have `JAVA_HOME` set).
2. **Android SDK.** Android Studio's SDK Manager, or the command-line tools,
   with `platform-tools`, `build-tools;35.0.1` and `platforms;android-35`.
   No NDK is needed: the preset does not use a Gradle build.
3. **Export templates** for the pinned engine: put
   `Godot_v4.7.2-stable_mono_export_templates.tpz` in `tools\godot\templates`
   (the same place `fetch_godot.bat` puts the engine) and run
   `python tools\android\run.py templates`. It unpacks into
   `%APPDATA%\Godot\export_templates\4.7.2.stable.mono`.
4. **Debug keystore:** `python tools\android\run.py keystore`. This is the
   standard `androiddebugkey/android` key every debug APK on every developer
   machine is signed with; it is not a secret and it never signs a release.
5. **Device:** USB debugging on, plug it in, accept the prompt on the phone.
   `adb devices` must say `device`, not `unauthorized`.
6. **UO data on the device:** `python tools\android\run.py push` copies your
   `UO_CLIENT_DATA` folder to `UO_ANDROID_CLIENT_DATA` (several GB; adb push
   `--sync` makes reruns cheap). The folder is the app's own external files
   directory: no storage permission, visible to adb, removed on uninstall.
   Never commit it, never share the APK with it inside.

`doctor.bat` re-checks all of this. Nothing in the tool needs Android Studio
itself open.

## What an export does

1. writes the SDK/JDK/keystore paths into Godot's editor settings (keeps a `.bak`)
2. generates `godot\GUO\GUO.sln` if the editor has not (the .NET export needs it)
3. renders `export_presets.template.cfg` -> `godot\GUO\export_presets.cfg`
   (gitignored; it carries the keystore path and password)
4. runs `godot-console --headless --path godot\GUO --export-debug Android <apk>`,
   which itself runs `dotnet publish -r android-arm64 -p:GodotTargetPlatform=android`
   and packs the result into the prebuilt `android_debug.apk` template

The client's command line is baked into the APK via `command_line/extra_args`
(a phone has no launcher to pass flags): always `-- --play --client-data
<device path>`, plus whatever `export.bat --args "..."` adds. The leading
`--` matters: `Main.cs` reads `OS.GetCmdlineUserArgs()`, which is only what
follows it. To reach the dev shard from the phone, bake in this PC's LAN
address: `export.bat --args "--host <pc-lan-ip>"` (the shard must listen
wide, `UO_SHARD_BIND=0.0.0.0`). The smoke build adds `--login-probe-stay`.

Every build the tool exports is **silent**: `device_args` bakes `--silent`
in, and `Main.cs` mutes the Master bus at start (log line `[GUO] audio
muted (--silent)`). A device run is either a tool driving the handheld or a
person trying a build, and neither wants the Britain theme over the
speaker. `export.bat --sound` (also `smoke.bat --sound`, `preset --sound`)
leaves it out and exports an audible build; that is the only way a player's
install hears anything for now. The mute is on the bus, not in the profile,
so the saved sound settings are untouched.

Two project-side facts the export depends on: Godot 4.7's prebuilt Android
template is built for **net9.0**, so `GUO.csproj` switches its target
framework to net9.0 when `GodotTargetPlatform` is `android` (the desktop
stays on net8.0), and the export refuses to run without
`textures/vram_compression/import_etc2_astc=true` in `project.godot`.

The launcher icon is the GUO sigil: the ornate gold-and-silver mark whose
master, `design\brand\guo-sigil.png` (1254 px RGBA on transparent), is
committed in this repository, so the build depends on nothing outside it.
The four Android PNGs in `godot\GUO\android_icons\` (legacy 192: the
sigil on a dark rounded slab; adaptive foreground 432 with the sigil
inside the 66% safe zone, on transparent; flat #14100C background 432;
monochrome 432 from the sigil's alpha) are built by `python
tools\brand\run.py` (`launchers\dev\brand_icons.bat`; `python
tools\android\icons.py` still works and calls it), together with the
project icon, the Windows executable icon and the boot splash, so every
platform shows the same mark and none shows the engine's. The preset
template points at those PNGs; Godot's Android template also uses the
adaptive foreground as the Android 12 splash-screen icon
(`res/drawable/splash_icon.webp` in the APK). Rerun the tool if the
master changes and commit what it wrote. See `tools\windows\README.md`
for the whole icon table and the evidence.

## The smoke

`smoke.bat` exports a build with `--login-probe-stay`, installs it, clears
logcat, starts `org.guo.client/com.godot.game.GodotAppLauncher` (4.7's exported
launcher; `GodotApp` itself is not exported and `am start` refuses it), and polls
`adb logcat -d` for the line `src/Bootstrap/LoginProbe.cs` prints once the
login gump has been drawn:

```
[GUO] login probe: ok login gump rendered after N frames; window WxH, screen scale S, dpi scale D, gump WxH at X,Y
```

On that line it takes `adb exec-out screencap` into `build\android\smoke.png`,
saves the log to `build\android\smoke_logcat.txt`, stops the app and exits 0.
A `FAIL` line, a `FATAL EXCEPTION`, the process dying, or the timeout
(default 240 s; first launch decodes a lot) exit 1.

## Playing on the dev shard from a device

The smoke only needs the login gump. Anything that goes into the world needs
the device to reach the shard running on this PC, and a login that works on
a handheld's screen:

- **Reach the shard over adb.** When the `ip` in the app's `settings.json`
  (in its files folder) is loopback, the device looks for the shard on its
  own 127.0.0.1. Forward that port to this PC first, and remove the forward
  when you are done:

  ```
  adb -s <device> reverse tcp:2593 tcp:2593
  adb -s <device> reverse --remove tcp:2593
  ```

  Use `push-stage --reverse PORT` to do the same while staging a build.
- **Let the client log itself in.** The probes' scripted login
  (`InputProbe.EnterTheWorld`) clicks the desktop login layout. On a
  handheld the login gump is centred and scaled somewhere else, so the
  clicks miss and the run stops at "never got into the world". Bake the
  account and `--autologin` into the export instead; the gallery waits for
  that login rather than clicking:

  ```
  python tools\android\run.py export --args "--account <account> --autologin --ui-gallery --screenshot-name gallery"
  ```

  With no `--password`, the password defaults to the account name, as the
  dev shard's test accounts use. The gallery writes to `user://screenshots`
  (`files/screenshots` in the app's data, readable with
  `adb exec-out run-as org.guo.client cat files/screenshots/<name>.png`).

## The touch layer on the desktop

`launchers\dev\touch_probe.bat` runs the client with `--play --touch-probe`
against the configured shard: the touch layer is switched on (with
`emulate_touch_from_mouse`, so a mouse works as a finger too) and
`src/Bootstrap/TouchProbe.cs` drives synthetic `InputEventScreenTouch` /
`InputEventScreenDrag` events through it, checking each gesture reached the
game as the intended mouse event and had the intended effect:

```
[GUO] touch check: ok   a tap became a left click -- tap -> left click
...
[GUO] touch probe: 18/18 checks passed
```

Play by hand with the layer on: `launchers\game\play.bat --touch`.

## Second display

A device with a second display Android will let an app present on (the AYN
Thor's lower screen; `DisplayManager.getDisplays(DISPLAY_CATEGORY_PRESENTATION)`
is the test) uses it from the moment the client is up: the welcome panel
before the player is in the world (the GUO sigil, the client version, the
shard, and the second screen's own settings), then the paperdoll, backpack,
status bar and journal as a shelf once in the world, with the world alone on
the main screen. Why and how is
[ADR-0009](../../docs/architecture/ADR-0009-second-display.md) and its
amendment; in short the second screen is a virtual extension of the client
window to the right, the gumps over there are ordinary upstream gumps, the
gumps that sit over there are drawn a second time into a target of the
second screen's size, and that target is pushed to an
`android.app.Presentation` through Godot's own Java bridge. No plugin, no
Gradle: the export is the same one as always.

- **Nothing to build.** `export.bat` already carries it; a device without a
  second display logs `[GUO] dual screen: no second display; nothing changes`
  and that is the end of it.
- **Dual from launch.** The presentation is shown as soon as the client
  runs (`[GUO] dual screen: active; ...` then `welcome panel WxH at x=...`
  on logcat) and dismissed at exit. Logging out brings the welcome panel
  back; nothing is hidden in between.
- **The settings**, on the welcome panel (touch) and under Options > General
  > "Gumps & Context" (only where a second display exists), applied live:
  use the second screen as a shelf (`DualScreenEnabled`); which kinds are
  shelved when opened -- paperdoll, backpack, status bar, journal, other
  gumps (skills, spellbook, containers) (`DualScreenShelve*`); the shelf's
  own pixel scale, 0 = the main screen's, else 1x-3x (`DualScreenScale`).
  They live in the profile (PlatformDefaults v7 turns the four on); before
  a profile is loaded the panel reads the newest saved profile.json, as the
  canvas background does, and an edit made on the login screen is carried
  into the profile the player then logs into. With the shelf off, the
  welcome panel stays up in the world too, so it can be turned back on from
  the lower screen. Swapping the screens is not offered: the world is drawn
  to the main window only, and going through the bitmap path would cost
  the frame rate the ADR protects.
- **The shelf's layout** is a fixed slot per kind, clamped to the shelf:
  paperdoll top left, status bar along the bottom, backpack top right,
  journal under the backpack and above the status bar, other gumps in the
  middle. A gump on the shelf is clamped back every frame (not while it is
  being dragged), so nothing runs off an edge whatever a saved position
  says. Only gumps whose middle is past the main window's edge are drawn
  there, so the top bar (wider than the Thor's main window) no longer
  spills a fragment onto the lower screen. At 620x540 the four do not fit
  without overlap (paperdoll 262x324, status 577x216, journal 345x298,
  backpack 230x204); the journal overlaps the lower part of the backpack,
  and a tap brings either to the front. Untick a kind to free the space.
- **Turning it off:** the "Use it as a shelf" checkbox on either panel, or
  `--args "--dual-off"` on an export (no presentation at all).
- **Doctor:** `doctor.bat` prints every display `dumpsys display` reports,
  with its size, rotation, `presentation` flag and SurfaceFlinger id, and
  which one the client will use. `python tools\android\run.py displays`
  prints just that list.
- **Probe:** `dual_probe.bat --args "--host <this PC's LAN address>"` exports
  a build with `--dual-probe` baked in, installs it, runs it, logs in as the
  probe account, opens the four gumps, waits for
  `[GUO] dual screen: ok` on logcat and photographs both panels
  (`adb exec-out screencap -p -d <id>`) into `build\android\dual_main.png`
  and `dual_second.png`, with the log in `dual_logcat.txt`. `--stay` leaves
  the app up afterwards; `--no-export` reuses `GUO-dual.apk`. The client also
  saves what it pushed (`guo_second.png` in its own screenshots folder); the
  screencap is the panel itself, which is the only proof the pixels arrived.
- **The id `screencap -d` wants** is the display's `uniqueId` in `dumpsys
  display` (`local:<id>`), the same number `dumpsys SurfaceFlinger
  --display-id` lists. The tools read it from there;
  `UO_ANDROID_SECOND_DISPLAY` in config.bat overrides it.
- **On the desktop** the same feature runs against a window standing in for
  the panel: `--play --dual-screen 1240x1080 --dual-probe` (or without the
  probe, to play with it); the mouse in that window is one finger on the
  second screen. `--login-probe` with `--dual-screen` photographs the
  welcome panel at the login screen (`<name>_second.png` beside the main
  frame): `launchers\dev\screenshot.bat --play --dual-screen 1240x1080
  --login-probe --screenshot-name dual_login`.

What the probe prints, from the Thor:

```
[GUO] dual screen: 1 presentation display(s)
[GUO] dual screen: display 4 "Screen-2" 1240x1080 rotation 1
[GUO] dual screen: active; second screen 1240x1080 is 620x540 at dpi scale 2.00, main window 960 wide
[GUO] dual screen: presentation shown on display 4, bitmap 620x540
[GUO] dual screen: shelf 4 gump(s) beyond x=1011; paperdoll at 1011,0 status at 1011,324; world 1011x539 in a 1011x539 window
[GUO] dual screen: gumps TopBarGump@0,0:1114x27 ContainerGump@1401,0:230x204 JournalGump@1286,242:345x298 StatusGumpModern@1011,324:577x216 PaperDollGump@1011,0:262x324 WorldViewportGump@-5,-5:1027x555
[GUO] dual screen: presented 53 frames during the on-measurement, last push 3.97 ms, touches taken 0
[GUO] dual screen: fps on=60.0 off=60.0
[GUO] dual screen: ok
```

## First run: the folder picker (G2a)

With no valid data, the client shows the first-run screen. On Android,
Choose folder opens the system folder picker (the Storage Access Framework),
through Godot's native OpenDir dialog. What that gives, as measured on the
Thor on 2026-09-28:

- **A tree URI, not a path:** `content://com.android.externalstorage.documents/tree/primary%3A<folder>`.
  Godot's `FileAccess` reads a file in it as `<treeUri>#<name>`.
- **Godot's `DirAccess` cannot open a tree URI** (`InvalidParameter`), and
  SAF names are case-sensitive: `Cliloc.enu`, `Skills.idx`. Probing the
  required list, which is lower-case, fails. So `Bootstrap/SafFolder.cs`
  lists the folder the way Android does, over JNI: the `AndroidRuntime`
  singleton's activity, `getContentResolver().query()` on
  `DocumentsContract.buildChildDocumentsUriUsingTree`. That lists 347 files
  in about 0.15 s.
- **JavaClassWrapper matches no overload when an argument is null.** A
  `query(uri, projection, null, null, null)` returned no cursor and threw no
  exception. Pass typed empty values (`""`, an empty `String[]`) instead;
  a documents provider ignores the selection and the sort anyway.
- **The client's readers need real paths**, so Continue copies the folder's
  files into the app's own `files/uo`. It skips the Windows client's
  programs, libraries and logs. Files are written as `.part` and renamed
  when whole. The copy was 2,848 MiB in about 5.5 min (about 8.5 MB/s). The
  app then releases its folder grant: it never reads the picked folder again.
- The picker will not open `Android/data`, so a test folder goes elsewhere
  (for example `/sdcard/GUO_saf_test`).
- `run.py export --no-client-data` builds without the data path, as a
  player's install has it. The client flag `--saf-forget` releases every
  folder grant the app holds.

## The emulator

`run.py export --abi x86_64` (or `both`) builds for the Android emulator,
which runs x86_64 images. The default is `arm64`, for the devices. Tried on
2026-09-28 with an Android 15 phone image (1080x2400) under WHPX, run
headless (`-no-window`):

- **Guest Vulkan does not work headless.** Both lavapipe and the host GPU
  gave a black screen with `Couldn't present to Vulkan queue`.
- **SwiftShader GLES does not work either:** with `-gpu swiftshader_indirect`
  Godot's compatibility shaders fail to link and the screen stays grey.
- **What works:** `-gpu host -feature -Vulkan`. Godot finds no Vulkan
  device, falls back to OpenGL ES 3.1 on the host GPU, and renders.
  `--rendering-driver` in the export args does nothing on Android, because
  the Java side picks the driver from the project settings.
- The first launch after a cold boot can be killed by an activity restart;
  launch again. A monkey launch
  (`adb shell monkey -p org.guo.client -c android.intent.category.LAUNCHER 1`)
  works where `am start` on the activity is refused.
- G2a on the emulator: the picker lists 341 files in 0.4 s. It copies 323
  files (2,438 MiB) in about 6.5 min, then logs in and holds 16.7 ms in the
  open field.

## What has actually been run, and what has only been written

Recorded on 2026-09-26. The first pass was written on a machine with no
Android SDK; the same day the SDK was installed (cmdline-tools,
build-tools 35.0.1, platform 35, JDK 17 from Adoptium) and the tool run end
to end against an AYN Thor (Android 13, 1080x1920, 369 dpi, adb serial
`<device-serial>`).

| What | Result |
|---|---|
| `dotnet build godot\GUO\GUO.csproj` (desktop) | 0 errors, unchanged behaviour |
| `launchers\dev\touch_probe.bat` equivalent (desktop, dev shard) | 18/18 checks: tap, focus, long-press right-click on the login screen; hold-to-walk, double-tap paperdoll, pinch -> Ctrl+wheel, gump bar -> backpack, long-press closed the paperdoll |
| `run.py doctor` | clean once the SDK, templates, keystore and editor settings were in place |
| `run.py templates`, `keystore`, `settings` | ran; 27 template files unpacked, debug keystore created |
| `run.py export --args "--host <pc-lan-ip>"` | debug APK, 105 MB (self-contained .NET), signed |
| `run.py push` | 341 top-level files, 2.3 GB, sizes match the install. The launcher's subfolders (`Data`, `Music`, ...) fail with `secure_mkdirs failed`: adb cannot create subfolders under scoped storage. The client needs none of them. |
| `run.py install` | Success |
| Launch on the Thor | **the login screen renders**, at a whole-number 2x, touch layer on; screenshot `build\android\thor_login_2026-09-26.png` |
| `run.py doctor` / `displays` (second display) | display 0 "Built-in Screen" 1920x1080 rotation 1; display 4 "Screen-2" 1240x1080 rotation 1 **presentation**, SurfaceFlinger <display-id> |
| `run.py dual_probe --args "--host <pc-lan-ip>"` (package `org.guo.dual`) | first run: `FAIL never got into the world` -- the input probe aimed in client pixels while the device draws at 2x, and the touch layer swallowed its clicks; fixed (`InputProbe.PointerScale`, the layer stepped aside for the login, as the touch probe does) |
| same, second and third runs | **`[GUO] dual screen: ok`**: presentation shown on display 4, 620x540 bitmap at 2x, four gumps on the shelf, 53 frames pushed in 180, last push 4.0-4.7 ms, **fps on=60.0 off=60.0**; both panels photographed: `build\android\thor_dual_main_2026-09-26.png` (the world alone, top bar and gump bar) and `thor_dual_second_2026-09-26.png` (paperdoll, backpack, status, journal, pixel-perfect at 2x) |
| a tap on the second panel (`adb shell input -d 4 tap 430 164`, the paperdoll's OPTIONS button) | logged `first touch, finger 32 down at window 2328,156 (client 1227,82)` and **opened the options gump on the main screen**: `thor_dual_main_after_tap_2026-09-26.png` |
| desktop simulator (`--dual-screen 1240x1080 --dual-probe`, dev shard) | ok; 4 gumps on the simulated screen, world fills the 3840x2054 main window; `desktop_dual_main_2026-09-26.png`, `desktop_dual_second_2026-09-26.png` |
| the options checkbox for the second screen | written; not exercised on a screen |

Four things broke on the device before that and were fixed the same day,
each marked `PORT DEVIATION` where it touches ported code:

1. the client saw no arguments (`No UO client data directory`): the preset
   lacked the `--` separator;
2. upstream's `Logger` sets `Console.ForegroundColor`, which Android's .NET
   throws on; skipped there;
3. upstream's zlib binding resolves the system `libz`, and marshalling
   `zlibVersion()` as a string frees a static pointer, which Android's
   tagged-pointer check aborts on; Android uses the managed zlib;
4. the touch layer's screen scale compounded with the OS scale (1.8 on the
   Thor) and the login screen overflowed the display; the picker now chooses
   the total and divides the OS factor out.

The same evening the device was played, not just launched. Everything below
was run on the Thor over the LAN against the dev shard (`--host
<pc-lan-ip>`), account `guoprobe`; the screenshots are in `build\android\`.

| What | Result |
|---|---|
| `run.py smoke` (`UO_ANDROID_DEVICE=<device-serial>`, since a second, unauthorized device was attached) | **exit 0**: `[GUO] login probe: ok login gump rendered after 1 frames; window 1920x1080, screen scale 1.1111112, dpi scale 2.00, gump 640x480 at 160,30`; `smoke.png`, `smoke_logcat.txt` |
| Landscape, no system bars | login and world both fullscreen: `u1_login.png`, `u1_world.png`. Before `KeepFullscreen` the status bar came back on every scene change |
| Login by touch alone | tap on the account field raised the soft keyboard (`t8_keyboard.png`); the login gump slid up so the field and the password field are above it (`t11_kb_pan.png`); typed through the IME (`t12_typed.png`); tap elsewhere hid it (`t13_kb_down.png`); password field masked (`t14_pw.png`) |
| Pre-game screens centred | login (`t7_login.png`, gump at 160,30 logical = 320,60 physical), shard list (`u4_shards.png`), character selection (`u4_chars.png`). The loading screen shares the same `GetGumpForStep` path; 16 burst screenshots at 0.3 s never caught it on the LAN |
| World fills the display | 960x540 logical at 2x, gump bar of six UO-art buttons along the bottom (`u3_world.png`) |
| Hold-to-walk | a swipe became held right button, the character walked (trace `hold -> right press`, `right release`) |
| Pinch | no effect at first: Godot forwarded no drags for a symmetric pinch. With `enable_pan_and_scale_gestures=true` and magnify accumulation: `t22_pinch_in.png` / `t23_pinch_out.png`, trace `pinch in/out -> ctrl+wheel`. Driven by a `sendevent` script on `/dev/input/event6` (`adb shell input` cannot do two fingers) |
| Zoom survives relaunch | force-stop, relaunch: world back at zoom 1.5; `profile.json` on the device holds `default_scale: 1.5000002`, `save_scale_after_close: true` |
| Raw-touch mapping | raw (30,1720) on the portrait panel landed on the Options bar button in landscape: landscape X = raw Y, landscape Y = 1080 - raw X |
| `run.py push` | rewritten: top-level files only, non-zero exit on any adb error |
| Soft keyboard input types | with the Default type the IME's word suggestions mangled the account name (`guoprobeoprob`, GUO-UI's run). The account field now raises the e-mail keyboard and the password field the password keyboard, the two types an Android IME must not suggest or correct in. Verified twice: account and password typed by tapping Gboard's keys (`k3_account.png`, `k3_password.png`), and account by `adb shell input text` (`k4_*`); both arrived letter for letter in the trace and logged in |
| Enter from the IME | the keyboard's check mark (IME action Done) is turned into an Enter key by Godot's own `GodotTextInputWrapper`, the keyboard hides, and the login gump's `OnKeyboardReturn` logs in (`k3_after_enter.png`: the shard list). `adb shell input keyevent KEYCODE_ENTER` never reaches the game: the single-line EditText Godot types through swallows a hardware Enter, so a script must tap the check mark (1775,917 on the Thor) instead |

Not verified on the device (the desktop touch probe covers them, 18/18):
drag-to-pick-up, targeting, double-tap on a world object. Known gap: the
in-game chat line is under the soft keyboard while typing; the world gump is
deliberately never slid up, so the typed text is only visible once the
keyboard hides.

Debugging aids that came out of this: `--touch-trace` (bake it in with
`export.bat --args "--touch-trace"`) prints one `[GUO] touch:` line per
gesture decision on logcat; `UO_ANDROID_DEVICE` picks the device when adb
sees more than one (an `unauthorized` one no longer counts).
