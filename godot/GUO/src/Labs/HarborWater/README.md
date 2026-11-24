# Harbor water experiment

Build the optional fixed-step solver with a Rust toolchain and the platform linker. On Windows use cargo build --release --manifest-path tools/harbor_water/Cargo.toml and place guo_harbor_water.dll beside the Labs Debug GUO.dll. Build the client with dotnet build godot/GUO/GUO.csproj -p:GuoLabs=true. Set GUO_RUST_RIPPLES=1 for the probe or local client session. The managed solver handles an absent or incompatible library. Release clients omit the native call. The water-row inputs, two time buffers, boundaries and 20 Hz tick are owned by WaterRipples. The native step takes no ownership of either buffer.

Run the existing WaterRippleProbe.tscn from the Labs build to exercise the texture upload and renderer. The native kernel has no third-party crate dependencies.

Set GUO_HARBOR_POSE=1 to enable whole-pixel cosmetic heave in Lighting water with ripples enabled. Hull corners sample two shared swells and the live ripple field. Use launchers/dev/harbor_water_doctor.bat or its shell twin to verify the native entry point. See tools/scenarios/client/harbor_water.md for the occupied boat capture.
