# AYN Thor controller layout detection

GUO needs to distinguish the Thor's physical face buttons from the logical
buttons Android delivers when the quick-settings controller style changes.
Native gamepad dispatch is not implemented in `GodotInput.Handle` yet.

## Expected translation (requires physical-button verification)

| Physical Thor button | Standard / Thor logical button | Xbox logical button |
| --- | --- | --- |
| A (right) | A | B |
| B (bottom) | B | A |
| X (top) | X | Y |
| Y (left) | Y | X |

Keep physical bindings and printed button prompts consistent by resolving the
active layout before translating logical input. Do not apply this translation
globally to external controllers.

## Read-only device inspection, 2026-09-27

The connected device reports model `AYN Thor`:

- System setting `temp_abxy_layout_mode`: `1`.
- System setting `abxy_layout_mode`: absent (`null`).
- System property `persist.sys.gamepad.type`: `0`.
- Active controller key layout: `/system/usr/keylayout/Vendor_2020_Product_0111.kl`.
- Controller vendor/product: `2020:0111`.
- The quick-settings list contains the `controllerStyle` tile.

The user confirmed that **Standard** is selected and is their everyday preferred
layout. A subsequent read reproduced all the values above, establishing a
Standard-mode baseline on this device. The tile also offers **Xbox** and
**Disconnected**.

These remain candidate detection signals, **not a verified numeric mode enum**:
Xbox and Disconnected have not been measured, so we do not yet know which values
actually distinguish the modes. ADB readability also does not prove that the
exported GUO application can read a value through Android APIs.

The installed `0111` and `0112` key-layout files both map scan codes `0x130`,
`0x131`, `0x133`, and `0x134` to Android A, B, X, and Y respectively. Reading
these files alone therefore does not establish the active face-button swap.

## Verification and integration requirements

1. Record the visible Standard/Thor or Xbox tile label and the candidate values,
   toggle once, then repeat. Include the disabled state if offered.
2. In each mode, press physical A, B, X, and Y and capture the actual Godot button
   events. Verify axes and triggers separately.
3. Verify mode reads from the exported application's Java bridge. Prefer a
   readable Android setting or device identity validated against the toggle;
   do not assume a vendor property is an application-accessible API.
4. Refresh on application resume and controller connection changes; also detect
   mode changes while running. Clear held actions when the layout changes.
5. Represent unavailable/unrecognized mode as Unknown. Offer a manual layout
   choice or physical-button calibration instead of silently assuming Xbox.

Godot documents that `Input.get_joy_info()` is empty on Android, so that API
alone cannot supply this vendor setting:
[Godot Input reference](https://docs.godotengine.org/en/latest/classes/class_input.html#class-input-method-get-joy-info).

## Measured, 2026-09-27 (steps 1–3)

Taken on the AYN Thor (`ro.product.model` = `AYN Thor`) by cycling the
**Controller style** quick-settings tile (Standard → Disconnect → XBox →
Standard) and pressing each physical button with GUO's `--gamepad-trace`
build running. Traces: `build/android/evidence/pad_std.log`,
`pad_xbox.log` (local, not committed).

### What each mode changes

| Signal | Standard | XBox | Disconnect |
|---|---|---|---|
| Tile label | `Standard` | `XBox` | `Disconnect` |
| `settings system temp_abxy_layout_mode` | `1` | `0` | `2` |
| `settings system flip_button_layout` | `0` | `1` | `0` |
| `settings system no_create_gamepad_button_layout` | `0` | `0` | `1` |
| `getprop persist.sys.gamepad.type` | `0` | `1` | `2` |
| Android input device | `Odin Controller` | `Xbox Wireless Controller` | none |
| vendor:product, key layout | `2020:0111`, `Vendor_2020_Product_0111.kl` | `2020:0112`, `Vendor_2020_Product_0112.kl` | — |
| **Godot `Input.GetJoyName`** | `Odin Controller` | `Xbox Series X Controller` | no joypad |
| Godot `IsJoyKnown` (SDL mapping) | false | true | — |
| Godot `GetJoyGuid` | hex of the name (`4f64696e…`) | hex of the name (`58626f78…`) | — |

`abxy_layout_mode` stays `null` in every mode. `GetJoyInfo` is empty, as
Godot documents. The mode is **readable by the exported app** through the
joypad's name alone, with no settings or property access: it is a device
reconnect (a new joypad event) whenever the tile changes. `OS.GetModelName()`
tells the Thor's built-in pad in XBox mode from any other Xbox pad.

### Physical button → Godot button

| Physical (printed) | Position | Standard | XBox |
|---|---|---|---|
| A | right | `A` (0) | `B` (1) |
| B | bottom | `B` (1) | `A` (0) |
| X | top | `X` (2) | `Y` (3) |
| Y | left | `Y` (3) | `X` (2) |
| D-pad up / right / down / left | | `DpadUp` (11) / `DpadRight` (14) / `DpadDown` (12) / `DpadLeft` (13) | same |
| L1 / R1 | | `LeftShoulder` (9) / `RightShoulder` (10) | same |
| L2 / R2 | | axis `TriggerLeft` (4) / `TriggerRight` (5), 0 to 1 | same (plus a `Misc1` (15) press seen once with L2) |
| Stick clicks | | `LeftStick` (7) / `RightStick` (8) | same |
| Left stick up | | axis `LeftY` (1) −1.0 | — (not tilted in the XBox pass) |
| Right stick "up" | | axis `RightY` (3) +1.0 (sign as observed) | — |
| Start / Select | | `Start` (6) / `Back` (4) | not pressed |

So the tile is exactly the documented swap: Standard delivers the printed
label, XBox delivers the position (south = `A`). Only the face buttons
differ; the D-pad and shoulders are the same in both modes.
