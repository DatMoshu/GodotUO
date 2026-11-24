# harbor_water

The optional Labs Rust ripple solver for the harbor water experiment (L4, `GuoLabs=true` Debug builds only),
and its doctor.

| File | What it is |
|---|---|
| `Cargo.toml`, `src/` | the Rust crate that builds `guo_harbor_water` (a native library exporting `guo_water_step_v1`) |
| `doctor.py` | checks that Cargo is installed and that the built library sits beside the Debug `GUO.dll` and exposes its entry point |

Run the doctor with `launchers\dev\harbor_water_doctor.bat` (or `.sh`). How to build and stage the library and how the
client uses it is in `godot/GUO/src/Labs/HarborWater/README.md`. Exported clients never carry it; without it the
managed ripple solver runs as usual.
