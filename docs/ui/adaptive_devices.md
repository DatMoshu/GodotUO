# Adaptive Android devices

Implemented in `DeviceLayout.cs`, `AdaptiveLayout.cs`, `FoldObserver.cs` and the existing `DualScreen` panel path. The shareable visual package is `design/device-layouts/index.html`.

## Layout policy

| State | World | Companion and controls |
|---|---|---|
| Phone / closed cover | Upper two thirds of the active window | Companion below, virtual movement left, actions and pointer right |
| Open flat Fold | Upper half | Companion below between virtual controls |
| Tabletop | Above the reported horizontal hinge | Companion below the hinge, controls at the outer sides |
| Book | Left of the reported vertical hinge | Companion right; movement below the world and actions below the companion |
| Tent / stand | Active facing display only | Manual Tent mode; with an attached pad, full world and companion drawer; otherwise compact touch layout |
| Tablet, landscape or portrait | Upper two thirds | Lower companion and controls; tablet threshold is 600 dp, not raw pixels |
| Thor | Existing main display | Existing physical secondary-display shelf and hardware controller path; no duplicate virtual controls |

Auto uses AndroidX `FoldingFeature` bounds, separation, occlusion and half-open state. A horizontal separating feature selects Tabletop; a vertical one selects Book. A nonseparating flat fold selects Flat. Without a fold feature, window size and density choose tablet, near-square Flat or Phone. Bounds outside the current window are ignored while a rotation is settling.

The mode button cycles Auto, Phone, Flat, Tabletop, Book, Tent and Tablet. It saves locally in `user://device-layout.cfg`. Manual tabletop and book supply a central split if no real hinge is reported. An actual separating/occluding hinge always takes precedence. There is no universal Android tent event and no attempt to force rear-display or simultaneous inner/outer-screen sessions. Only the OS-selected active window is used. Do not bend an inward-only device backwards to imitate the generic tent concept.

## Inputs and transitions

- Virtual movement and pointer pads feed the existing gamepad path. A clicks/confirms, B cancels, X opens the window menu and Y toggles the command bar. Character, Bag and Journal buttons open existing interactive GUO gumps in the companion.
- An enabled, connected physical gamepad suppresses duplicate virtual controls. Disabling physical controller input restores touchscreen controls; printed touch labels do not inherit the hardware button-swap setting. Thor keeps its existing display/control path.
- Geometry changes cancel touch gestures and release controller directions before reflow. A 180 ms settling interval avoids layout chatter; invalid shrinking-window geometry updates immediately. Focus loss and pause release held virtual controls.
- Pane geometry keeps an 8 dp buffer beside reported hinges. The world camera fits its pane rather than enforcing the desktop 640×480 minimum. Main-screen gump fitting uses the world pane. Compact shelf gumps are scaled down to fit their available area.
- Android permits sensor rotation. Automatic art scale fits both width and height, and a login step is re-centered without replacing it.
- In-process fold and rotation events retain the current world session. This is not a guarantee against Android killing/recreating the process: existing profile-save and reconnect behavior still applies.

## Packaging

Android exports now use Gradle to package `androidx.window:window-java:1.3.0` through `addons/guo_posture`. The Android source template comes from the same pinned Godot mono export templates as the client. `tools/android/run.py` installs it once and marks the generated tree with `.gdignore`; it preserves local Gradle customizations. The first build needs dependency downloads. No client data is embedded in the APK.

If AndroidX is unavailable, the client logs a window-size fallback; automatic real-hinge behavior must not be claimed for that build. The runtime log should say `AndroidX posture listener active` for a correctly packaged build.

## Validation

On 2026-10-01 the x86_64 debug APK built, installed and ran in the visible Fold emulator. AndroidX registered successfully. Native device-state and hinge events exercised Flat → Tabletop → Book → closed-cover Phone → Flat, with rotation and no process restart during those transitions. The geometry suite passed 20,198 assertions and the export suite passed all 18 tests.

The emulator previously selected `asdfdsaf`, the character unseen in a dark dungeon. Selecting the existing `Guoprobe` character produced a visible player; holding the virtual movement pad moved it from (5431, 1153) to (5423, 1153). The original character's world/server condition was not repaired. This is emulator validation, not certification on physical Fold, tablet or Thor hardware.

To drive the emulator, hold near an edge of the left Move area (the center is the dead zone); release to stop. The right Pointer area moves the cursor. Use A to click at the cursor, B to cancel, X for window actions, and Y for the command bar. Character, Bag and Journal open companion windows. Tap the Auto/posture button to cycle manual layouts; return it to Auto for native hinge behavior.

Run `dotnet run --project tools/android/layout_tests` for deterministic geometry checks. It tests phone/tablet density, all manual modes, rotated dimensions, real off-center hinges, full occlusion and controller layouts. Optional output path writes the same geometry used by the wireframe generator.

Run `python -m unittest discover -s tools/android -p "test_*.py"` for export checks, including repeatable source-template installation and archive traversal rejection.

For desktop layout development use `--touch --adaptive-layout`; ordinary desktop touch probes retain their previous layout. Android enables adaptive layout automatically when its one-screen panel is enabled.

The gallery uses real, attributed hardware photographs with their original screen contents, separate from an actual GUO emulator capture and implementation wireframes. It contains no generated device renders. Photos do not establish GUO validation on the pictured models. New evidence is collected by GUO Device Test Manager under `build/device-tests/`. Physical Thor/tablet/tent hardware regression checks remain necessary.

References: [Android fold-aware layouts](https://developer.android.com/develop/adaptive-apps/guides/foldables/make-your-app-fold-aware), [foldable app continuity](https://developer.android.com/develop/adaptive-apps/guides/foldables/learn-about-foldables), [Godot Android plugin/export dependencies](https://docs.godotengine.org/en/4.7/tutorials/platform/android/android_plugin.html).
