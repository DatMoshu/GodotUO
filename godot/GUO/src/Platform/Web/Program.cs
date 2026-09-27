// The web export (tools/godot_web, ADR-0008) publishes the game assembly as a
// browser-wasm program, which needs an entry point. Godot starts the game
// itself; this one does nothing. Compiled only when GodotTargetPlatform is
// "web" (GUO.csproj).
{ }
