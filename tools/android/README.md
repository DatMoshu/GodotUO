# tools/android -- GUO on an Android device

Exports the client as a debug APK, installs it, runs it and smoke-tests it.
The decisions behind it (why ARM64 only, why the data lives where it does,
why the plugin host is compiled out, how touch maps onto the mouse) are in
[ADR-0007](../../docs/architecture/ADR-0007-android-target.md). This file is
the how-to.

```
launchers\android\doctor.bat     what is missing on this machine, with the fix
launchers\android\export.bat     debug APK -> build\android\GUO-debug.apk
launchers\android\install.bat    adb install it
launchers\android\run.bat        start it, stream its logcat
launchers\android\smoke.bat      export + install + run + wait for the login
                                 gump + pull screenshot and log; non-zero on failure
launchers\dev\touch_probe.bat    the touch layer, checked on the desktop (no device)
```

Everything is `python tools\android\run.py <command>` underneath; the extra
commands (`templates`, `keystore`, `settings`, `preset`, `push`, `logcat`) are
one-time setup steps and have no launcher.

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
address: `export.bat --args "--host <shard-lan-ip>"` (the shard must listen
wide, `UO_SHARD_BIND=0.0.0.0`). The smoke build adds `--login-probe-stay`.

Two project-side facts the export depends on: Godot 4.7's prebuilt Android
template is built for **net9.0**, so `GUO.csproj` switches its target
framework to net9.0 when `GodotTargetPlatform` is `android` (the desktop
stays on net8.0), and the export refuses to run without
`textures/vram_compression/import_etc2_astc=true` in `project.godot`.

The launcher icon is GUO's (`godot\GUO\icon.svg`), rasterised once by
`python tools\android\icons.py` into `godot\GUO\android_icons\` (legacy
192, adaptive foreground/background/monochrome 432); the preset template
points at those PNGs. Rerun it if the SVG changes.

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

## What has actually been run, and what has only been written

Recorded on 2026-09-26. The first pass was written on a machine with no
Android SDK; the same day the SDK was installed (cmdline-tools,
build-tools 35.0.1, platform 35, JDK 17 from Adoptium) and the tool run end
to end against an AYN Thor (Android 13, 1080x1920, 369 dpi, adb serial
`<thor-serial>`).

| What | Result |
|---|---|
| `dotnet build godot\GUO\GUO.csproj` (desktop) | 0 errors, unchanged behaviour |
| `launchers\dev\touch_probe.bat` equivalent (desktop, dev shard) | 18/18 checks: tap, focus, long-press right-click on the login screen; hold-to-walk, double-tap paperdoll, pinch -> Ctrl+wheel, gump bar -> backpack, long-press closed the paperdoll |
| `run.py doctor` | clean once the SDK, templates, keystore and editor settings were in place |
| `run.py templates`, `keystore`, `settings` | ran; 27 template files unpacked, debug keystore created |
| `run.py export --args "--host <shard-lan-ip>"` | debug APK, 105 MB (self-contained .NET), signed |
| `run.py push` | 341 top-level files, 2.3 GB, sizes match the install. The launcher's subfolders (`Data`, `Music`, ...) fail with `secure_mkdirs failed`: adb cannot create subfolders under scoped storage. The client needs none of them. |
| `run.py install` | Success |
| Launch on the Thor | **the login screen renders**, at a whole-number 2x, touch layer on; screenshot `build\android\thor_login_2026-09-26.png` |

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

Logged in from the device the same evening, driven by adb because the soft
keyboard does not appear (a tap focuses a text field but nothing asks Android
for the keyboard; `DisplayServer.VirtualKeyboardShow` on focus is the fix to
raise next). At screen scale 2 on a 1920x1080 window the login gump's fields
sit at these physical points:

```
adb -s <thor-serial> shell input tap 645 597 ; input text guoprobe    account
adb -s <thor-serial> shell input tap 645 693 ; input text guoprobe    password
adb -s <thor-serial> shell input tap 640 757                          Login
adb -s <thor-serial> shell input tap 525 243 ; input tap 1235 910     shard row, next
```

That reached the shard list over the LAN, then the world: the probe character
stood in town with its paperdoll open and the touch gump bar along the bottom
(`build\android\thor_world_2026-09-26.png`).

Not yet run: `smoke.bat` as a whole (its pieces all ran), and any play beyond
arriving in the world. Known: the login gump sits at the top
left of the 1920x1080 window with grey to its right, as the desktop draws it
into a 640x480 window that a phone cannot shrink to; centring it is a
mobile-only change to raise separately.

