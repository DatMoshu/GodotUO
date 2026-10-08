# How We Built It

Short notes on how recent features were made: the problem, the approach, how
they were tested, and what went wrong on the way. Each section names the
commit that landed it on `main`. The binding decisions live in the ADRs under
`docs/architecture/`; this page tells the story behind them.

## 2026-10-04: overnight editor and map generator sprints

### Planet biome profiles for the map generator (73ff7f57)

**Problem.** The map validator checked every generated world against
Britannia's biome mix, so a desert or ice world always failed.

**Approach.** `BiomeDistributionRule` gained named profiles: `felucca` (the
old bands), `desert`, `ice` and `none`. The Map Validator pass has a
`BiomeProfile` setting. An unknown name is an error rather than a silent
fallback. Two smaller fixes came with it. Exporting a preset now keeps a
default-off pass off when its settings were tuned, so the exported preset
rebuilds the same map. A run's `preset.json` loads with `--preset`.

**Tested by.** Four profile tests and an export test, all 269 map generator
tests, and the editor smoke's map generator stage. The default map's hash is
unchanged, so existing presets give the same worlds.

### District pads (4be5927b)

**Problem.** Placing a city on a sloped desert needed a flat pad in a known
spot. The town finder only searched at random, and only on grass biomes.

**Approach.** No new pass was added. A new pass would shift every later
pass's random stream and change every preset's map. Two existing passes were
extended instead:

- **Town Sites** gained `Sites` (`x,y,w,h[,z]` entries placed without any
  randomness, at the median height of the footprint unless `z` is given) and
  `BuildableBiomesCsv`.
- **Town Roads** gained `PaintStreets`, `FlattenTarget` and `FlattenSkirt`,
  so it can flatten a bare pad with a sloped skirt instead of painting
  streets.

Entries that leave the map, touch water or are malformed are skipped, with a
warning.

**Tested by.** Five tests on a 64×64 ramp, where no area is naturally flat:
the median height, an explicit height and the warnings, desert as buildable,
a bare pad with its skirt, and unchanged defaults. All 274 map generator
tests pass, and the editor smoke hash is unchanged.

### AI hub hardening, S6 (bff43cc0)

**Problem.** The editor's AI dock can read the world and queue requests for
running agents. Nothing proved that only one of two watchers would take a
request, and a watcher restarted at a saved offset had no test.

**Approach.** Two tests were added to `tools/agent_queue`:

- Two real watcher processes race for one request. Exactly one wins and the
  other exits with "already taken".
- Reading replies resumes after an explicit offset.

The editor smoke now runs `jump_world` through the same tool host the chat
uses. A new tour segment shows `describe_cell` and `walkable` on a known
Britain wall and road.

**Tested by.** All 16 queue tests, then the full smoke and an editor reload,
with all AI checks passing before and after the reload.

### Server console in the Logs dock, S8 (3399b04c)

**Problem.** A shard started from the editor wrote its console nowhere you
could see it. The server also had to keep running through an editor reload.

**Approach.** The server is started inside a small shell wrapper that appends
both output streams to `servers/<id>/server.console.log` in the workspace.
On Windows that is `cmd` with the arguments passed through environment
variables, so quotes and percent signs survive. Elsewhere it is `/bin/sh`.
The Logs dock gained a **Server console** view, with secrets hidden as in
the other views.

**What went wrong.** The first full smoke failed on the second start of the
test server. Reading `Process.MainModule` right after the process starts
sometimes returns nothing on Windows, because the loader hasn't filled in the
module list yet. A stress run found it on the eighth start. The fix asks the
kernel for the image name instead: `QueryFullProcessImageName` on Windows,
and `/proc/<pid>/exe` on Linux and the Steam Deck. After the fix, 80 starts
in a row passed.

**Tested by.** The Logs smoke stage covers both streams, literal arguments,
redaction, a duplicate start, stop, restart and a natural exit. It ran in
the full smoke and across an editor reload, with the test server carried
over and stopped cleanly.

### Live layer on the World tab, S9 (0c76194a)

**Problem.** The World tab could show players and mobiles reported by the
shard bridge, but you couldn't filter them or tell which one was which.

**Approach.** The existing Live layer was extended in place:

- **Live players** and **Live mobiles** filters, in both the Layers menu and
  the map layers list.
- A hover tooltip with the name, serial and position. It is found by
  projecting each icon with the same maths the drawing uses.

**Tested by.**

- A smoke stage feeds scripted mobiles through a stub bridge and checks that
  they appear at the right cells, move, disappear and clear on disconnect,
  before and after a reload.
- A run against a private dev shard on its own ports recorded a creature
  moving one tile between two bridge polls. That run creates a spawner only
  when it is asked to, and removes it again afterwards.
