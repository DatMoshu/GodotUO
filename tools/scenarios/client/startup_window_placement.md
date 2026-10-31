# Startup window placement

Launch the normal desktop client with a saved client origin at the top of the monitor, a disconnected-monitor position, or a partly off-screen right/bottom edge. After native sizing settles, the outer window including title bar must fit within one monitor's usable area. Invalid placement is centered on the monitor with the most overlap; a fully disconnected position returns to the primary monitor. Valid saved positions, including negative coordinates on secondary monitors, stay unchanged.

Explicit `--window-position` stays authoritative for scripted captures. Mobile and fullscreen/maximized placement stays with the platform. A window larger than the usable area retains its size and puts its title bar at the usable top-left rather than resizing native content. No artwork, login background, DPI scaling or server settings change.

Focused checks:

```
godot-console --headless --path godot/GUO res://src/Platform/WindowPlacementProbe.tscn
godot-console --path godot/GUO res://src/Platform/WindowPlacementProbe.tscn -- --native-window-placement-probe
```

The native check opens only its own non-focusing temporary window, reproduces an inaccessible title bar, measures decorated bounds before/after recovery, and exits. No game data, connection, player profile or input automation is involved. Optional `GUO_WINDOW_PLACEMENT_OUT` writes measurements outside the checkout. Full smoke remains required before commit.
