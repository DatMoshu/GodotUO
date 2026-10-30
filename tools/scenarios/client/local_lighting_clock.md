# Local lighting test time

L4 Debug `GuoLabs=true` only; off by default. Open UO+, enable Lighting speed mode, check Local test time (ignore shard clock), choose 12 and Apply. Ambient brightness, window eligibility and sun direction use noon. Repeat at 0, 6, 18 and 23. The hour stays fixed locally; server time and weather remain unchanged.

Disable the override and Apply: shard lighting resumes immediately. Disable the parent Lighting speed mode: the override is inert. Defaults stages off/12 until Apply; Cancel discards edits.

Automated check after a Labs build:

```
godot-console --headless --path godot/GUO res://src/Labs/LightingClock/LightingClockProbe.tscn -- --scratch-profile --no-focus
```

Use an isolated `UO_WORKSPACE_DIR`. The probe checks 24 hours, off parity, immutable shard stamps, clamps/persistence and actual staged UI controls without connecting or saving a player profile. Standard builds must exclude the feature. Full smoke remains required before commit. Headless UI checks do not establish rendered visual acceptance; owner playtesting supplies that boundary without automated session interruption.
