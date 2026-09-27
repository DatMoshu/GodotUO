# Second-screen UI: OpenMW-DS comparison and GUO proposal

2026-09-27. Research and design proposal; no runtime UI changes in this task.

## Recommendation

Keep classic floating gumps and add an optional tabbed companion layout. Provide a screen-transfer gem for eligible floating gumps. Prefer pinch to change their size; show a resize grip only in Arrange UI mode. These ideas fit together if moving, resizing, and normal play have clear boundaries. They become cluttered if every gump permanently exposes several tiny handles or if touching inventory accidentally resizes it.

## What OpenMW-DS contributes

OpenMW-DS describes a lower-screen companion with HUD, inventory, spells, stats, and journal tabs, favorites, touch/controller navigation, and individually optional replacements for game menus. Its replacements forward operations to the original engine. Its map views can exchange screens and retain their own pan/zoom state. These are project claims, not tests performed here. [Project README](https://github.com/Josh-Daniels/OpenMW-DS)

The useful pattern for GUO is optional presentation over existing actions, plus a consistent navigation scheme. Do not assume its map zoom is evidence of arbitrary legacy-window scaling. Its release notes also describe device-specific focus fixes, reinforcing the need to test controller input immediately after touching the lower display. [Release history](https://github.com/Josh-Daniels/OpenMW-DS/releases)

UO has arbitrary server-authored gumps and a live multiplayer world. A modern presentation cannot safely infer the meaning of every server button, replace unknown dialogs, or pause combat while browsing tabs. Start with client-owned views and keep unknown server gumps available in their original form.

## What GUO has today

| Feature | Source-level finding |
| --- | --- |
| Second display | DualScreen uses a virtual canvas extension; shelf coordinates start at MainWidth. Android Presentation shows that part; a desktop simulator is available. |
| Automatic placement | Shelve/SlotFor handles the player's paperdoll/backpack, status, journal, and optionally other containers, skills, and spellbooks. This is not a universal per-gump transfer button. |
| Settings | DualScreenSettings and DualWelcomeGump include enabled categories and a whole-shelf scale setting: automatic or integer 1–3. |
| Per-gump switch gem | No generic implementation found in the audited Gump/Control/dual-screen paths. Treat as proposed. |
| Scaling | Screen/shelf scaling exists. ContainerGump has specialized scaling and inverse hit testing. Gump/Control has no general per-instance scale transform. Resizing a journal's bounds is different from scaling its contents. |
| Pinch | TouchInput.UpdatePinch and Magnify call ZoomBy, which emits Ctrl+wheel for world zoom. They do not assign a scale to the gump beneath the fingers. |
| Action bar | Six navigation buttons plus optional six-macro row. During targeting, Self/Cancel replace Chat/Options. |

Code references: [DualScreen](../godot/GUO/src/Platform/Android/DualScreen.cs), [settings](../godot/GUO/src/Platform/Android/DualScreenSettings.cs), [Gump](../godot/GUO/src/Game/UI/Gumps/Gump.cs), [Control](../godot/GUO/src/Game/UI/Controls/Control.cs), [ContainerGump](../godot/GUO/src/Game/UI/Gumps/ContainerGump.cs), [TouchInput](../godot/GUO/src/Input/Touch/TouchInput.cs), [TouchGumpBar](../godot/GUO/src/Input/Touch/TouchGumpBar.cs).

The wiki's statement that welcome/settings work is on another branch is stale relative to these current source files. Source presence alone does not establish device validation.

## Two layouts sharing one state

**Classic shelf:** keep floating gumps, existing art, and saved positions. A selected gump can move to the other screen or change size. Preserve this as a complete fallback.

**Companion tabs:** prototype five primary destinations: Overview, Inventory, Actions, Character, Journal. Put a map preview on Overview with an expanded map view. Actions contains spell/skill/macro favorites. Keep a small configurable action strip and the active target/Cancel visible outside the tab content. Tabs must not move combat-critical buttons unexpectedly.

Modern panels should read the same client state and call existing GameActions/MacroManager paths. Avoid opening duplicate interactive copies of the same inventory or issuing duplicate server requests. For each supported view, choose Classic or Companion presentation; unknown shard gumps remain classic. Opening a tab does not pause the world.

Start with Journal and Character, then Inventory after item moves and quantities are reliable. Do not replace trading, crafting, or arbitrary server forms until their behavior is explicitly covered. Single-screen mode should offer the same tabs as an overlay/sheet; disconnecting the second display must bring every reachable view back.

## Screen-transfer gem

Use a small two-screen symbol or gem with an arrow indicating destination. Give it a generous invisible hit area and a label such as “Move to lower screen.” Physical top/bottom can differ across hardware, so persist logical display roles rather than display IDs.

Tap transfers the existing gump instance, preserves its contents/focus where valid, and clamps it within the destination's usable area. Remember position and preferred scale separately for each screen. Show the inverse action after transfer. A controller-accessible window menu exposes the same command.

Only show this on eligible windows while two displays are active. Exclude the world viewport, fixed HUD, and transient popups; handle modal dialogs and their children as a group. Do not transfer during an item drag or active resize. Store an explicit user placement override so automatic shelving cannot immediately send it back.

Do not promise that every shard gump supports transfer until scissoring, modal ownership, text input, and server responses are tested. Unknown gumps retain a reliable main-screen fallback.

## Pinch-to-scale contract

Yes, pinch-to-scale is a good fit for floating gumps, provided it has predictable ownership:

1. Both contacts must begin on the same display and the same topmost eligible gump. Capture that gump for the gesture; moving across its edge must not switch to world zoom.
2. A pinch beginning on exposed world controls world zoom. Mixed world/gump contacts or contacts on different displays do neither. A map's interior pinch zooms map content; scaling the map window uses Arrange UI or its size menu.
3. Recognize the second contact before committing a pending click/drag. If an item is already held or a movement gesture is committed, do not reinterpret it as resizing. Suppress stray click, use-item, target, and long-press events when the pinch ends.
4. Anchor scaling at the pinch centroid so the gump does not jump. Limit size to usable display bounds with a minimum readable scale. Keep the transfer gem and Reset reachable, including oversized-gump cases.
5. Preserve nearest-neighbour sampling for classic art. Start with crisp integer scale stops; arbitrary fractional scaling produces uneven pixel widths even without blur. Modern panels should reflow and resize text instead of stretching a screenshot. Test useful size choices on the actual lower panel before finalizing presets.
6. Add a visible Size menu with smaller/larger/reset and a Lock size option. Pinch cannot be the only way to change size; controller users need the same function.
7. Store scale per supported gump identity and screen role, separate from content zoom, shelf scale, and global DPI. Never resize all backpacks because the user pinched one window unless they explicitly choose a shared default.

TouchInput currently has one shared primary/secondary state. DualScreen offsets lower-display finger IDs by 32, but distinct IDs alone do not enforce same-display gesture ownership. Add display identity to gesture capture. Also prevent Android magnify events and raw two-contact handling from applying the same zoom twice.

### Why generic scaling needs real implementation work

Rendering, hit testing, clipping, tooltips, drag offsets, drop destinations, and keyboard/IME positioning must agree on the same transform. For gump-local point `q`, displayed point is `origin + scale × q`; incoming coordinates must use the inverse before the legacy control sees them. Account for each display's logical-to-physical transform as well.

Keep original gump coordinates and server reply IDs unchanged. A compositor wrapper or scoped renderer transform plus inverse input adapter is a possible approach; audit custom Draw/Contains code before selecting one. Scaling an offscreen texture alone does not solve input. Global ContainerScale is not a substitute for an independent scale on any gump.

Use one resize affordance, not several competing ones: pinch during play where supported, an optional grip in Arrange UI, and Size/Reset in the window menu. Do not add a permanently floating dot beside every gump.

## Action-bar and gesture test matrix

The existing [TouchProbe](../godot/GUO/src/Bootstrap/TouchProbe.cs) checks navigation/gestures and macro-row opening, War/Peace execution, hiding, and reopening. It does not establish successful execution of all six macros. [DualProbe](../godot/GUO/src/Bootstrap/DualProbe.cs) checks shelf placement/presentation and FPS; it does not establish transfer gems or scaled hit testing.

| Action | Required fixture and observable result |
| --- | --- |
| Next target | Two known eligible mobiles; repeated taps select the intended candidates; empty scene is harmless. |
| Attack last | Known test opponent on local shard; one tap produces one intended attack; missing/stale target is handled. |
| Last target | Pending spell/item cursor and known target; correct target receives it; no unintended cast or attack without a cursor. |
| Last object | Known reusable test object; one use per tap, including rapid taps and stale serial. |
| Bandage self | Bandages and injured test character; correct self-use and server feedback; test empty supply, full health, and legacy/current macro paths. |
| War/Peace | Mode changes once; server state and row visibility agree; manually hidden row stays hidden. |
| Navigation/Self/Cancel | Each navigation gump opens; target-mode labels match behavior; cancellation closes the pending target without closing unrelated gumps. |
| Screen transfer | Both directions; preserved instance/state; no auto-shelve bounce; keyboard focus, reopen, reconnect, display removal. |
| Pinch scale | Both screens, multiple scales, edge clamping, overlapping gumps; click/item drop after scaling lands correctly; world/map pinch stays separate. |
| Controller after touch | Lower-display tap followed immediately by movement, tab navigation, confirmation, and cancel; no focus loss or duplicate event. |

Run macros first on desktop against test fixtures, then on-device on both display roles, and finally with a controller. Measure lower-screen input latency and frame time under several open gumps; past shelf FPS results do not establish the cost of per-gump render targets or modern panels.

## Validation during this research

- `dotnet build godot/GUO/GUO.csproj --no-restore`: passed, 0 warnings, 0 errors after access to the existing NuGet configuration was allowed.
- Local dev shard listener found on port 2593. ADB reports one authorized device and one unauthorized device; `org.guo.client` is installed on the authorized device. This is connectivity evidence, not a gameplay test.
- Initial sandboxed touch probe could not access the normal client home directory. Its stalled process was closed. An unrestricted attempt exited during asset loading without a probe verdict.
- Retry of `launchers/dev/touch_probe.bat --host 127.0.0.1`: **exit 0, 24/24 checks passed**. This exercises synthetic desktop touch, world pinch zoom, backpack opening, War/Peace execution, and macro-row visibility. Log: `build/screenshots/touch_research_2026-09-27_retry.log`; screenshot: `build/screenshots/touch_probe.png`. It does not establish on-device behavior or the other five macro actions. No physical-device gameplay was exercised in this task.
- No new gems, tabbed UI, or per-gump scaling implemented or claimed tested here.

## Recommended sequence

First finish the six-button macro test coverage and dual-screen input checks. Next prototype Companion Journal/Character and screen transfer on a small allowlist. Then add per-gump scaling to one simple gump with transform tests before containers and arbitrary server gumps. Adopt pinch only after it passes hit testing and gesture-conflict checks; retain Classic and Reset layout throughout.
