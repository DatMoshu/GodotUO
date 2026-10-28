# Mobile frame inspector (L4)

Owner request, 2026-10-28: add mobile debug gizmos to review troubleshooting values
each frame. This is the scoped implementation decision: read-only Labs diagnostics,
off by default, no player profile field, no game pause, no new rendering mode.

Build Debug with `-p:GuoLabs=true`, then use `-mobiledebug` in the client chat,
or **Modern Options → Labs: Mobile frame inspector**. The doctor is
`launchers/dev/mobile_debug_doctor.bat` (paired shell launcher available).
No additional dependency beyond the configured Godot .NET engine and .NET SDK.

Choose a mobile in the separate inspector window. The game displays its tile
anchor (cyan cross), interpolated feet (yellow circle), and offset (orange line).
The window samples after the game process step, including steps with no observed
draw. It reports coordinates, offsets, queue length, direction, animation index,
selected equipment frames, movement-packet destination and arrival age, focus/idle
settings and pending atlas pages. No observed draw can mean culling or throttling;
it does not prove a rendering error. Packet age is not network latency.

Freeze stops capture. Step buttons and the slider inspect the retained 300-frame
history; Live resumes. Frozen markers use historical screen coordinates while the
game continues moving. Export JSON saves the retained values under the configured
external `UO_BUILD_DIR/mobile_debug`; it never exports credentials or raw packets.
Exports contain character serials and positions and should stay private.

The native inspector window keeps its clicks out of gameplay input. Closing it
releases its history and markers; leaving the world also closes it. Switching
mobile clears history so values from different actors cannot be confused.
No texture sampling/filtering, renderer order, packets or gameplay state change.
The existing debug trace forwards observations independently of file logging;
no disk logging or GPU readback is enabled by this feature.

Release and exported builds exclude the folder and its registration. Default
Debug builds contain no references to this feature. Contract/visual probe:
`godot-console --path <project> res://src/Labs/MobileDebug/MobileDebugProbe.tscn`.
Set `GUO_MOBILE_DEBUG_PROOF` to an absolute PNG path for its synthetic UI capture.
Scenario: `tools/scenarios/client/mobile_debug.md`.
