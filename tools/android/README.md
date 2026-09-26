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
(a phone has no launcher to pass flags): always `--play --client-data <device
path>`, plus whatever `export.bat --args "..."` adds. The smoke build adds
`--login-probe-stay`.

## The smoke

`smoke.bat` exports a build with `--login-probe-stay`, installs it, clears
logcat, starts `org.guo.client/com.godot.game.GodotApp`, and polls
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

Recorded on 2026-09-26 on the development machine, which has **no Android
SDK** (only `adb` from a bare platform-tools folder) and a phone attached
that had not yet accepted USB debugging (`unauthorized`).

Run, with the result:

| What | Result |
|---|---|
| `dotnet build godot\GUO\GUO.csproj` (desktop) | 0 errors, unchanged behaviour |
| `dotnet publish -c ExportDebug -r android-arm64 --self-contained true -p:GodotTargetPlatform=android` (the .NET half of an export, what `doctor --publish` runs) | succeeds only with the `GuoPluginHost` guard in `GUO.csproj`; without it `NETSDK1032` from `tools\plugin_host`. Output has no `plugin_host` folder. |
| `launchers\dev\touch_probe.bat` equivalent (desktop, dev shard) | 18/18 checks: tap, focus, long-press right-click on the login screen; hold-to-walk (character moved), double-tap paperdoll, pinch -> Ctrl+wheel with the camera zoom changing, gump bar -> backpack, long-press closed the paperdoll |
| `python tools\android\run.py doctor` | ran; listed 11 missing things with fixes (no SDK, no templates unpacked, no keystore, editor settings unset, device unauthorized). Its output is in the ADR. |

Written but **not** run, because the machine cannot: `templates` (the tpz
unpack), `keystore`, `settings`, `export` (the Godot half), `install`, `run`,
`push`, `smoke`. No APK has been produced and nothing has run on a phone. The
first person with an SDK should run `doctor.bat` until it is clean, then
`smoke.bat`, and put the result in this table.
