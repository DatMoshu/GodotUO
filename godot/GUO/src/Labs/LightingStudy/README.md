# Realtime lighting study

This L4 experiment uses GUO's native surface buffer and existing directional
shadow mask. It adds a cool deep-night fill, with half the lift under cover.
Daylight shades wall faces using the existing lamp normals and the direction
toward the sun. The diffuse contribution retains half the light removed by the
original cast-shadow mask. Native artwork, sampling and additive lamps still
flow through their existing passes.

Build with `dotnet build godot/GUO/GUO.csproj -p:GuoLabs=true` and set
`GUO_LIGHTING_STUDY=1` before launching the client. Enable Lighting speed mode
in UO+ on the Depth World renderer. Clear the environment variable and restart
to compare with the baseline. The experiment defaults off and is compiled only
in Debug Labs builds. It has no persistent profile settings.

For the engine and client-data dependency check, run
`launchers\pipeline\01_verify_client_data.bat` from a configured checkout and
resolve the pinned .NET Godot console through the launcher config. The GPU
capture requires a RenderingDevice. See the
[offline capture scenario](../../../../../tools/scenarios/client/local_lighting_clock.md).
