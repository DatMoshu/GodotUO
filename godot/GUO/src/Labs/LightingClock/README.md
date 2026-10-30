# Local lighting clock (L4)

Owner-requested testing control: [ADR](../../../../../docs/architecture/ADR-local-lighting-clock.md). Requires a Debug build with `GuoLabs=true`. Standard and export builds exclude this directory and its profile/UI hooks.

Build: `dotnet build godot/GUO/GUO.csproj -t:Rebuild -p:GuoLabs=true`. Prerequisite: the existing pinned .NET Godot installation; `launchers/pipeline/00_bootstrap.bat` supplies it. No new dependency or service.

In UO+, enable **Lighting speed mode**, check **Local test time (ignore shard clock)**, select an hour from 0 to 23, then **Apply**. This freezes local ambient brightness, sun/moon direction and window-light eligibility at that hour. Weather still follows the shard. Disable the checkbox and Apply to restore shard lighting. Defaults stages off/12:00; Cancel discards edits.

Run `LightingClockProbe.tscn` headlessly in the Labs build to verify precedence, unchanged server stamps, clamps/persistence and the real staged controls. Use an isolated workspace and `--scratch-profile --no-focus`. It neither logs in nor saves a player profile. Scenario: `tools/scenarios/client/local_lighting_clock.md`.

## Live clock and automatic cycle (owner request, 2026-10-30)

In UO+ choose **Open live clock (applies settings)** after enabling Lighting speed mode. This opens a small independent panel. **Local override** enables the local lighting clock. Scrubbing the time slider changes lighting immediately, including minutes; no Apply needed in this panel. The time readout updates continuously. **Auto day / night cycle** runs the clock, or pauses at its current time when unchecked. Speed is simulated seconds per real second, 1x..1440x. A full day is 24 hours at1x,24minutes at60x,1minute at1440x. Speed changes and pause/resume preserve current time; the timeline wraps midnight using monotonic elapsed time. Scrubbing while running seeks and keeps running.

Closing the live panel keeps its settings/cycle. In-game close saves the selected time and controls to the current local profile, once; no packet or server/world/account changes. Disabling Local override immediately restores shard lighting. Parent Lighting speed mode remains required. Existing staged UO+ Cancel behavior stays unchanged; live-panel controls explicitly take effect immediately. The auto flag defaults off and speed defaults60x. This remains Debug Labs only, excluded from exports.
