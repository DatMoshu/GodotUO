# Optional PlayerBots shard

Run GUO against a local ModernUO shard with the server-side PlayerBots from
[Klein187/uo-offline](https://github.com/Klein187/uo-offline). This is optional
development tooling, separate from the GUO client and the normal dev shard.
Shard owners can use the same pinned setup as a compatibility reference.

## Start on Windows

Requires Git, Python 3.12, .NET 10 SDK, GUO's normal engine setup, and your own
UO client data configured through `launchers/_shared/config.local.bat`.
The first setup/build needs internet access. No game data is downloaded or copied.

```bat
launchers\shard\playerbots.bat setup
launchers\shard\playerbots.bat build
launchers\shard\playerbots.bat smoke
launchers\shard\playerbots.bat run
```

Leave `run` open. In a second terminal, populate the new world's vendors,
doors, decorations and travel infrastructure once, then open the client:

```bat
launchers\shard\playerbots.bat populate
launchers\shard\playerbots.bat play
```

Log in with the configured dev owner account. On Felucca, use
`[go 1434 1697 0` to visit the Britain scenario. Other useful staff commands:

| Command | Purpose |
|---|---|
| `[BotPanel` | Upstream administration UI, including removal controls |
| `[BotSessions` | Current live count and population target |
| `[SpawnBot mage grandmaster` | Create one mage for interaction testing |
| `[BotInfo` | Inspect a targeted bot |
| `[GenerateBots` | Replace bot spawners from the profile's scenario JSON |
| `[Save` | Save the isolated world |

`populate` generates the ordinary shard world; it does not reset the bot scenario.
Use `status` to see the profile location and setup/build records. The Python entry
point (`python tools/playerbots/run.py <command>`) also works without the launcher;
setup, build and server smoke do not need Godot. Client launch/population do.

## Isolation and configuration

| Setting | Default |
|---|---|
| `UO_PLAYERBOTS_DIR` | `build/playerbots` in this checkout |
| `UO_PLAYERBOTS_PORT` | `2640` |
| Game listener | `127.0.0.1:2640` |
| Saves/configuration | `build/playerbots/server/Distribution` |

These settings follow the shared environment/local/default configuration rules.
The profile does not use `UO_SHARD_SRC`. Its client launcher supplies its own
endpoint for that invocation. The UDP ping listener is disabled to avoid a
conflict with another dev shard. All fetched code, compiled output, logs and
saves stay in the ignored profile directory; custom locations should also be
outside tracked source.

The port must differ from the dev shard (2593) and from the editor bridge (2595); setup refuses 2593 and `UO_SHARD_PORT`. Configuration is written only when absent. To change an existing profile's
port, change both `UO_PLAYERBOTS_PORT` and its `Configuration/modernuo.json`
listener. Ordinary GUO shard launchers remain unchanged.

The included `Data/PlayerBotSpawners.json` has five Britain spawners totaling
50 slots: bank sitters, travelers, shoppers, crafters and wanderers. Session
timing and available spawn locations affect the actual live count. Behaviors
are autonomous, so this is a repeatable starting layout, not deterministic replay.
The normal world generators must run before vendor/travel testing is meaningful.

Edit that JSON to change placements/counts, then use `[GenerateBots` and `[Save`
in this test world. The `guo.playerbots.population` setting in `modernuo.json`
controls the session target on restart (default 50, clamped to 1–1600).
Keep it consistent with your scenario's slots. Upstream's `[SetBotPopulation`
regenerates the world-wide fallback, whose fixed crowds can exceed its target;
use the explicit scenario for bounded development tests.

## Compatibility and provenance

Exact repository URLs and full commits are recorded in [upstream.json](upstream.json):

- UO Offline: `d73330ee0f40c0f9d29a173fd568ca64ea629624`.
- ModernUO: `d4531cd94b739613155225c234900de9f47d2c88`, the current GUO dev pin.
- Ruleset: GUO's Endless Journey configuration, not UO Offline's T2A installer.

Setup fetches source directly and never executes UO Offline's installer. It
preflights the complete patch set and stops on incompatibility. A reverse check
distinguishes already-applied patches from failed patches. Reruns preserve
configuration/scenarios and refuse to overwrite changed bot source or navigation
data. For a pin upgrade, use a fresh profile directory and migrate a backup only
after verifying compatibility; setup will not reset a modified checkout.

The normal GUO ModernUO patches are applied, plus upstream's bot housing,
secure trade, unlocked-door pathfinding, and null corpse equipment patches.
The T2A dungeon travel, mount stamina, and young-player moongate patches are
deliberately excluded. The small GUO overlay accepts the bots' older
`PathFollower.Follow(bool, int)` calls and delegates to ModernUO's current
`Follow(int)` API, which derives running from movement pace.

UO Offline declares MIT; its copyright and permission notice are copied to
`Distribution/Licenses/UO-Offline/LICENSE`. ModernUO and its patches retain
their GPL licensing. GUO's integration glue is BSD-2-Clause. The fetched source
retains all original notices; no third-party source or proprietary client data
is vendored in GUO. If distributing a modified server, include the corresponding
source and applicable notices/licenses, not just GUO's client license.

## Shard owners

The supported first step is this isolated ModernUO sandbox. Existing shards
should stage a backup on the pinned ModernUO revision before applying the same
patches, `playerbots/source/CustomBots`, GUO overlay, and the four data directories
listed in the manifest. Do not copy GUO's development account/configuration
templates into a public shard. This is not a ServUO/RunUO plugin and does not
imply compatibility with other ModernUO revisions or custom economies.

To stop using the profile, stop its server and launch the normal GUO shard/client.
No normal-shard rollback is needed. Keep the entire profile directory as a
backup, or remove it after confirming its saves are disposable. To reset bot
spawners in a running test world, use `[GenerateBots`; for a fully fresh world,
choose a new `UO_PLAYERBOTS_DIR`. Removing bot assemblies from an existing shard
save requires a server-specific migration; simply deleting source is not a
supported uninstall procedure.

## Verification

```bat
python tools/playerbots/test_playerbots.py
python tools/guo/test_config.py
launchers\shard\playerbots.bat smoke
```

Smoke starts a temporary server, waits up to 90 seconds for bot navigation
initialization and the game listener, checks TCP connectivity, then terminates
only that process. It refuses an occupied game port. It is a startup check,
not a save/restart, graphical parity, or connected-client load test. The log is
`build/playerbots/smoke.log`; use a disposable profile for this check.

Verified during integration: complete Release server build, idempotent setup,
five-spawner initialization, navigation cache build, and the loopback listener.
All 38 deployment/configuration checks, documentation lint, and GUO's full
`launchers\dev\smoke.bat` passed (after the fresh worktree's initial build/import).
The upstream navigation audit reports stale/missing links and travel connections
on the supplied data; those warnings are preserved. Full travel, trading,
combat, save/restart and visual parity remain playtest work. Bots have no client
connections, so keep GUO's real multi-client tests for networking coverage.
