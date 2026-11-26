# CUO and GUO visual comparisons

The Python runner starts both clients on a test shard, replays a shared
plan, and captures full frames and live gumps. It can also attach to
existing clients or export a range of raw gump art IDs.

## Run a paired session

Build GUO with `launchers/dev/build.bat` and prepare the pinned ClassicUO
automation build with `python tools/cuo_automation/run.py proof --build`.
Install Pillow in the Python environment. Start a ModernUO test shard and
configure two staff accounts with existing characters. The runner uses
`UO_SHARD_OWNER` for CUO and the first entry in `UO_SHARD_GM_ACCOUNTS` for
GUO. Set their usual password variables in the environment or local config.

```powershell
python tools/visual_parity/run.py session --cuo-character TestCuo --guo-character TestGuo --out D:/runs/guo-parity
```

Both clients run without taking desktop focus. The default plan uses fresh
client settings, 1024 by 768 windows, and the ModernUO staff commands
`[globallight`, `[go`, and `[teleport`. Use `--shard-host` and
`--shard-port` for a different test shard. Use `--cuo-build` to select a
pinned injected build containing `cuo.exe`.

The default plan covers the eight world cursor directions, neutral targeting
and Escape, paperdoll close/reopen and drag, war and peace, the skills gump,
and both wheel directions. `actions.json` distinguishes asserted states,
dispatched input, skipped steps, and failures. Compare the before/after UI
bounds to inspect a drag. Scroll dispatch has no automatic displacement
check.

## Capture running clients

Start a GUO debug client and injected ClassicUO with their loopback MCP
servers enabled. Set `CUO_MCP_PORT`, `CUO_MCP_TOKEN`, `GUO_MCP_PORT`, and
`GUO_MCP_TOKEN` in the local environment, and use matching window sizes,
shard locations, and UI state.

```powershell
python tools/visual_parity/run.py capture --out D:/runs/one-frame
python tools/visual_parity/run.py drive --plan tools/visual_parity/matrix.plan.json --out D:/runs/replay
```

Each checkpoint includes the two original PNGs, a full-frame comparison,
and both UI inventories. It pairs visible gump instances by type and
position, clips their bounds to each viewport, and records unmatched
instances in `manifest.json`. The sheets are screenshots of each client's
renderer. World backgrounds and distinct character names or equipment can
change pixels inside a crop.

Use `--plan` for another fixture or layout, and `--size` for other window
dimensions. The coordinates in the default plan require its 1024 by 768
layout. `matrix.json` records additional fixtures for cursor hues, OS
cursor hotspots, resize, and mouse interactions.

## Export raw gump art

```powershell
python tools/visual_parity/run.py gump-ids --start 0 --end 255 --out D:/runs/gump-art
```

Build the reference tool once with `launchers/dev/build_guoasset.bat`.
`gump-ids` sends one batch to guoasset and exports each non-empty art ID
using the pinned ClassicUO loaders. Use `capture` for live CUO and GUO
gump comparisons. Keep images outside the checkout because they contain
proprietary client art.
