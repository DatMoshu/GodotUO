# Two-client mobile synchronization test

Tools-layer diagnostic plus an L0 parity repair. Requires a running loopback
development shard, two configured GM accounts and the configured .NET Godot engine.
Use dedicated dev characters: the probe moves and unhides them, then attempts to
restore their starting map, position and hidden state. Forced termination cannot
restore state. It does not change equipment or server configuration.

Run `launchers/dev/mobile_sync.bat --stage <external-snapshot> --out <fresh-evidence-folder>`.
Both paths must be outside the checkout. Use the shared evidence runs area for
`--out`. Acquire the two project runtime slots before launching. The script copies
source (never client assets), builds/imports an isolated Debug project and launches
two clients with separate profiles. Existing clients are never terminated.
Idle FPS reduction is disabled in these profiles, and `--no-focus` prevents
window focus from changing the test pacing.

Default: Forward+ Depth World with `GUO_DW_REUSE=1`. `--renderer canvas` tests
the canvas renderer. `--laps 1` shortens the default two laps. Each lap alternates
mover/observer and exercises eight run directions in opposite pairs; later laps
reuse decoded animation frames. Phase barriers prevent one client outrunning its
observer. `--skip-build` reuses the frozen snapshot, retaining its source manifest.

Outputs: source hashes, settings, client logs, sampled PNG frames, per-client JSONL
and analysis.json. Rows include actual received movement updates, draw-selected
body/equipment frames, positions, interpolation offsets and pending atlas pages
at presentation. No packet payloads, chat or passwords are recorded. Character
serials and local account names can occur in diagnostics; keep evidence private.

Pass requires both clients to finish, observe peer movement with interpolation
and equipment submissions, and zero presentation frames with pending atlas pixels.
This detects the late-decode upload regression even when sampled PNGs miss the
one-frame flash. Submissions do not prove visible pixels; missing source animation
frames are reported separately. Capturing images perturbs timing: do not use this
test to claim FPS, latency, packet-loss rates, or complete visual parity.

Late-decoded peer equipment also gets a bounded CPU/GPU pixel comparison and an
exact-frame `*-late-<process-frame>.png` capture, correlated with JSONL. Use
`--negative-control` to omit only the late upload in the disposable snapshot:
the checkout is unchanged and that run is expected to fail. Source hashes reflect
the altered snapshot. Empty decoded source frames get separate trace rows.

The production fix uploads late-decoded sprites after drawing and before Godot
presentation. The frame-start upload remains for existing consumers. All trace
and probe calls compile out of Release. No new player setting or gameplay command.

Optional `--labs --gizmos` builds the L4 mobile inspector, selects the peer in each
client, and saves each inspector PNG plus its 300-frame JSON history. This opt-in
Labs build also exposes `-mobiledebug` and the shared Labs Options action for manual
inspection. See `godot/GUO/src/Labs/MobileDebug/README.md` and its doctor launcher.
