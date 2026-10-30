# Local lighting clock (L4)

Owner-requested testing control: [ADR](../../../../../docs/architecture/ADR-local-lighting-clock.md). Requires a Debug build with `GuoLabs=true`. Standard and export builds exclude this directory and its profile/UI hooks.

Build: `dotnet build godot/GUO/GUO.csproj -t:Rebuild -p:GuoLabs=true`. Prerequisite: the existing pinned .NET Godot installation; `launchers/pipeline/00_bootstrap.bat` supplies it. No new dependency or service.

In UO+, enable **Lighting speed mode**, check **Local test time (ignore shard clock)**, select an hour from 0 to 23, then **Apply**. This freezes local ambient brightness, sun/moon direction and window-light eligibility at that hour. Weather still follows the shard. Disable the checkbox and Apply to restore shard lighting. Defaults stages off/12:00; Cancel discards edits.

Run `LightingClockProbe.tscn` headlessly in the Labs build to verify precedence, unchanged server stamps, clamps/persistence and the real staged controls. Use an isolated workspace and `--scratch-profile --no-focus`. It neither logs in nor saves a player profile. Scenario: `tools/scenarios/client/local_lighting_clock.md`.
