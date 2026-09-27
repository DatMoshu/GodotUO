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
