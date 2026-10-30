# ADR: Local lighting clock for testing

Status: Moshu approved a local override that ignores server time for testing on 2026-10-30. This is L4: opt-in `GuoLabs=true` Debug builds only, excluded from standard and exported builds.

An off-by-default UO+ checkbox and an hour slider (0–23, default 12) select a frozen local lighting time. Apply commits staged changes; Defaults stages off/12:00; Cancel discards edits. The existing shared UO+ controls provide registration and controller navigation.

The selected hour drives speed-mode ambient brightness, sun/moon projection and window-light eligibility together. The received `ShardClockState` is never mutated. Disabling the override invokes the existing resolver and ambient path immediately; disabling Lighting speed mode also makes the override inert. No packet, simulation, weather or server changes occur.

Brightness uses `0.1 + 0.9 * max(0, sin((minute - 360) * pi / 720))`, where minute is the selected hour times 60. Thus midnight is 0.1 and noon is 1. Sun direction and length use the existing projection. This is a lighting preview approximation, with fixed 06:00 dawn and 18:00 dusk, rather than simulation time control.

Validation covers all 24 hours, off/parent parity, immutable server stamps, clamping and persistence, real staged UO+ Apply/Defaults/Cancel and standard-build exclusion. The headless probe uses an unsaved profile and stays disconnected. Full smoke remains required. Visual review belongs to owner playtesting; the running owner game is left alone during implementation.
