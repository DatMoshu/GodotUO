# F08b/c — palette range operations

Layer: L3 editor, `#if TOOLS`; manual, no AI toggle or extra dependency doctor.

Run `python tools/editor_smoke/run.py --reload --out <external-folder>` and
inspect `assets.hue_range_*` before and after reload. Run the authorized native
proof with `--windowed --assets-workbench --no-build --out <external-folder>`.
Use external build/evidence folders under the shared GUO evidence policy.

1. Open Hues, expand Palette operations, and choose indices 0 to 31. Select
   Shift RGB, enter R +8, G -8, B +16 and apply. Undo must restore all 32 raw
   values at once. Save/reopen must retain the result, name and raw table bounds.
2. Reverse indices entered in descending order. It must reverse the same
   inclusive range, preserving all 16 bits and leaving outside entries intact.
3. Use black/white endpoints at 0/3: Spread yields interiors `3DEF,7FFF`;
   Exponential yields `0001,1084`. Both endpoints remain unchanged. Adjacent
   endpoints have no interior and do not change the palette.
4. Use equal full-white endpoints with Exponential. The reference's overflow
   must refuse the entire edit and retain pending fields, with a suggestion to
   choose different endpoints or Spread. Invalid hex drafts also refuse edits.
5. Verify expanded range controls, channel inputs, apply action and pinned save
   footer at 1024×768, 1366×900 and 1920×1080. Extra fields scroll in the inspector.

RGB operations deliberately write 15-bit values like Fiddler. Reverse preserves
all 16 bits. No native writer, staging, insertion or deletion is added.
