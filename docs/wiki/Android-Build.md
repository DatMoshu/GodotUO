# Android Build

GUO exports as an **ARM64 debug APK** for any Android 12 or newer device,
without Gradle or the NDK. Source of truth: ADR-0017 (Accepted) and
`tools\android\README.md`, whose run table records what actually ran on a
device. The first device run is commit "Android: first run on a device";
everything below has been exercised on one handheld with two screens, so a
second device is the next real test.

```bat
launchers\android\doctor.bat     REM what is missing, and the fix for each
launchers\android\export.bat     REM a debug APK, headless, into build\android
launchers\android\install.bat    REM adb install onto the attached device
launchers\android\run.bat        REM start it and stream its logcat
launchers\android\smoke.bat      REM export, install, run, wait for the login gump, pull a screenshot and a log
launchers\android\dual_probe.bat REM the second-screen probe, see Dual Screen
```

Underneath: `python tools\android\run.py <doctor|templates|keystore|settings|preset|export|install|run|logcat|push|smoke|dual_probe|displays>`.

## What the export needs

| Part | Where it comes from | Setting |
|---|---|---|
| JDK 17 | your install | `UO_ANDROID_JDK` (default `%JAVA_HOME%`) |
| Android SDK with `platform-tools` and `build-tools` | Android Studio or the command-line tools | `UO_ANDROID_SDK` |
| Mono export templates for 4.7.2 | `run.py templates` downloads them into `%APPDATA%\Godot\export_templates\4.7.2.stable.mono` | none |
| A debug keystore | `run.py keystore` makes one with the standard `androiddebugkey` / `android` | `UO_ANDROID_KEYSTORE*` |
| .NET SDK 9 | the Android target of Godot 4.7's template is `net9.0`; `GUO.csproj` switches to it when `GodotTargetPlatform` is `android` | none |
| adb and a device with USB debugging on | | `UO_ANDROID_DEVICE` (empty picks the single ready device) |

Godot reads the SDK, the JDK and the keystore from **its editor settings
file** and nowhere else (not `ANDROID_HOME`, not `JAVA_HOME`), so
`run.py settings` writes the values from `config.bat` into
`%APPDATA%\Godot\editor_settings-4.7.tres` (a `.bak` is kept), and `export`
does so before every export. The export preset is rendered from
`tools\android\export_presets.template.cfg` into the gitignored
`godot\GUO\export_presets.cfg`, which carries the keystore path and password;
that is why the preset is never committed. The preset excludes
`addons/guo_editor/*` so the editor add-on's sources are not packed.

## The client data on the device

The UO install is never inside the APK. `run.py push` copies it to the app's
external files folder, `UO_ANDROID_CLIENT_DATA`
(`/sdcard/Android/data/<package>/files/uo`), and the export bakes
`--play --client-data <that path>` into the APK's `command_line/extra_args`,
so the app starts straight into the login screen. Exports are silent unless
`--sound` is in the baked arguments.

`run.py run --args "--host <address of the PC running the shard>"` points the
device at a shard on your LAN. The shard must listen on an address the phone
can reach, not only loopback; see [Dev Shard](Dev-Shard.md).

## What the device forced

Recorded in ADR-0017 and the first-run commit, in case you hit them elsewhere:

- `project.godot` sets `import_etc2_astc`; the export refuses without it.
- No console colour in the logger on Android (`PlatformNotSupportedException`).
- Managed zlib on Android: the system `libz` binding aborts.
- The plugin host is a Windows-only build item; a publish for android-arm64
  failed in `tools\plugin_host` before any GUO code was built. Assistant
  plugins are not part of the Android build.
- The whole-number screen scale divides the OS scale out, so the client's own
  integer `ScreenScale` is used instead of Godot stretch. Pixel art stays
  unfiltered.
- Immersive mode only survives scene changes in `WindowMode.Fullscreen`, so a
  mobile OS keeps that mode through the login and game windows.
- The account field raises the e-mail keyboard and the password field the
  password keyboard: the two input types an Android IME must not autocorrect
  in. The IME's check mark arrives as Enter and logs in.

## Why it is landscape only

The export is locked to sensor-landscape. Portrait was tried on an Odin 2
Mini (2026-09-28) and turned down on one number:

| | Landscape | Portrait |
|---|---|---|
| Turning the screen | — | 95 ms, nothing lost |
| World in view (tiles across x down) | 15.3 x 8.6 | 11.5 x 20.4 |
| Command-bar button | 13.2 x 7 mm | **7.4 x 2.3 mm** |
| Paperdoll, backpack, Modern gumps | fit | fit |

The world works in portrait, and shows twice as far north and south. The
command bar does not: its buttons shrink to 2.3 mm tall, which a thumb
cannot hit, and the login screen stays where landscape put it, half off the
screen. Both are fixable, so portrait could come back as an option later;
the measurements and the probe that took them are in the repository's
`docs/android_portrait_spike.md` and ADR-0017.

## The smoke

`launchers\android\smoke.bat` exports, installs, launches, waits for the
login probe's line on logcat (`[GUO]` prefix, the same line the web smoke
waits for), pulls a screenshot and the log into `build\android\`. It exits 0
only when the login gump was drawn. `run.py displays` lists the device's
displays for [Dual Screen](Dual-Screen.md) work.

## What has been run, and what has not

From ADR-0017 and the README table: export, install, login by touch alone,
shard list, character selection, arriving in the world, hold-to-walk, pinch
zoom, zoom kept across relaunch, the soft keyboard typing an account letter
for letter, and the smoke exiting 0. All on one Android 13 handheld against
the dev shard over the LAN.

ADR-0017 still carries a "Written, not run" list from its first pass beside
its Accepted status; read the ADR's Validation section for what is actually
crossed off. Nothing has been run on a second device, on Android 12
specifically, or on a phone with one screen and no physical controls, so
treat those as **written, not verified**. CI publishes the .NET half of the
Android export on every push (`.github\workflows\build.yml`) but cannot run
the engine or export an APK, and says so in the workflow.

The touch layer and the per-platform profile that make a phone playable are
on the [Mobile UI](Mobile-UI.md) page.
