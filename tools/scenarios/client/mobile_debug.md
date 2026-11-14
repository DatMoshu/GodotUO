# client.mobile_debug — Labs frame inspector

1. Run `mobile_debug_doctor`, build Debug with GuoLabs=true, log into the dev shard.
2. Open `-mobiledebug` or the Labs Options action. Select self, then a moving peer.
3. Confirm the cyan anchor/yellow interpolated-feet marker tracks the selected
   mobile at zoom 1 and another zoom, including an offset game viewport.
4. Confirm received movement ages/destinations, queued steps, animation and
   equipment frames change. No file trace is required.
5. Freeze, walk in the game, and step backward/forward: retained values and markers
   stay historical; gameplay continues. Live resumes, bounded to 300 snapshots.
6. Select another mobile: prior history and packet state clear. Export JSON to
   external UO_BUILD_DIR and confirm only the selected actor's snapshots are saved.
7. Close/reopen; log out. No markers/window remain after close or logout.
8. Default Debug and Release builds: no command/Options row or sampling hooks.

Synthetic smoke checks history capacity, defensive copies, freeze/seek/resume,
clear, and actual inspector UI construction. It does not replace live alignment
or network validation; recordings are diagnostics, not performance benchmarks.
