# GUO Device Test Manager

The developer UI for launching Android test environments, changing posture and
rotation, deploying a build, and collecting reproducible evidence.

## Start

Run `launchers\android\device_manager.bat` or `python tools/android/device_manager.py`.
The former Visual Device Lab entry points redirect to the manager.

Requirements: Python with Tk, Android SDK emulator/platform-tools/command-line tools,
a JDK usable by `avdmanager`, and the installed system image
`system-images;android-35;google_apis;x86_64`. Point `ANDROID_HOME` at a nondefault SDK.
Set `JAVA_HOME` (or configure `UO_ANDROID_JDK` in the shared launcher config).
The manager creates missing named AVDs using SDK definitions without overwriting
existing AVDs. It does not install system images or accept SDK licenses.

1. Search/select a device or coverage family.
2. **Launch / apply profile** creates/boots its AVD and applies geometry. Only the
   selected profile is active for capture; selecting a different row does not
   silently change the current emulator.
3. Use **APK / data folders** to select an x86_64-compatible debug APK and your UO
   installation. **Install + run GUO** deploys it and copies data on first use.
   The current default is `build/android/GUO-debug.apk`.
4. For the local development shard, keep the server running on host port 2593.
   The manager forwards that port; the APK controls login/server configuration.
   Existing account/character preferences are preserved.
5. Exercise rotations and, on foldable SDK profiles, flat/tabletop/book/closed-cover
   events. Use GUO's in-game mode button for Tent and manual layout overrides.
6. Capture screenshots or Start recording / Stop + save. Use Save diagnostics to
   collect filtered Godot/Android crash logs. Stop emulator preserves its data.

Preparing another profile stops GUO only on other recognized manager emulators,
so a single shard account does not compete across clients. Physical USB devices
and unrelated AVDs are not touched. Switching profiles or stopping an emulator is
blocked during recording. Multiple emulator windows may remain open; stop unused
ones to release host memory.

## Coverage catalog

| Pixel 9 Pro Fold | SDK hardware profile | SDK defaults |
| Pixel 9 | SDK hardware profile | SDK defaults |
| Pixel 9 Pro | SDK hardware profile | SDK defaults |
| Pixel 9 Pro XL | SDK hardware profile | SDK defaults |
| Pixel 10 Pro | SDK hardware profile | SDK defaults |
| Pixel 10 Pro XL | SDK hardware profile | SDK defaults |
| Pixel 10 Pro Fold | SDK hardware profile | SDK defaults |
| Galaxy S25 Ultra | Display-size simulation | 1440×3120 / 560 test dpi |
| OnePlus 13 | Display-size simulation | 1440×3168 / 560 test dpi |
| Generic phone baseline | Display-size simulation | 1080×2400 / 420 test dpi |
| Pixel Tablet | SDK hardware profile | SDK defaults |
| AYN Odin 2 / Mini · Retroid Pocket 5 | Display-size simulation | 1080×1920 / 320 test dpi |
| Anbernic RG 406H | Display-size simulation | 720×960 / 240 test dpi |
| Anbernic RG Cube | Display-size simulation | 720×720 / 240 test dpi |
| AYN Thor · lower display | Display-size simulation | 1080×1240 / 320 test dpi |

The catalog is `tools/android/device_profiles.json`. Native SDK hardware profiles
run the same Android 35 Google APIs image; they are not vendor ROM images or
performance emulators. Pixel 10 entries use the SDK's Pixel 10 geometry, not a
claim to emulate its factory OS. Samsung and OnePlus entries are resolution
presets with explicit test densities. Density is Android logical dpi, not physical
panel PPI; repeat testing with accessibility/display settings on real devices.

Odin 2, Odin 2 Mini and Retroid Pocket 5 share a 1920×1080 geometry family at a
chosen density. This deduplicates layout coverage, not physical screen size,
controller mappings, GPU behavior or firmware. RG 406H adds 4:3, RG Cube adds 1:1,
and Thor adds its 1240×1080 lower-screen shape. Thor is **not** a full two-display
simulation. Linux-only retro handhelds are not Android APK targets and are excluded.

The listed override sizes are in natural portrait coordinates; handheld profiles
start at rotation 1, so a 1080×1920 override is displayed as 1920×1080.
Sources for each named geometry are linked in the catalog and the UI's Profile source button.

## Evidence

`build/device-tests` stores timestamped PNGs, MP4s, diagnostics, and adjacent JSON
files containing the exact profile, observed size/density/rotation, code revision,
and manager-triggered transition history. Events changed using emulator side-panel
controls are not in the manager history. Local APK/data paths live in the ignored
`settings.json`; no game data is committed.

Recordings are silent H.264, maximum three minutes, using a fixed 1280×1280 canvas
so aspect changes can fit during transitions. Separate takes per orientation give
cleaner framing. Click Stop + save even after the three-minute limit to pull the
clip. If the manager is killed while recording, the bounded recording remains on
that emulator's `/data/local/tmp/` and can be recovered using adb.

## Documentation media

The mobile README and gallery now use original device photographs with explicit
credits and licenses (`design/device-layouts/photos/CREDITS.md`). Their original
screen contents are retained. GUO emulator captures and wireframes are separate.
The rejected generated renders are absent from the shareable gallery and ZIP.
An appropriately licensed Thor/Odin/Retroid photo has not been added; those entries
use coverage text and the existing wireframes instead of an invented device photo.

## Verification

`python -m unittest discover -s tools/android -p test_device_manager.py` checks
profile identity, shared-AVD capture guards, recording lifecycle, PID reuse safety,
fold capability gating and evidence metadata. All 15 profile definitions were
validated and their 11 backing AVDs provisioned on the development machine.

Runtime checks passed for Pixel 10 Pro rotation, screenshot and MP4 capture,
and RG Cube square geometry, screenshot and MP4 capture. These checks used
the Android system UI, not GUO gameplay on every new profile. Ten manager
unit tests pass. Physical controllers and vendor firmware still require hardware.
