# Map paging (L4)

Owner scope: Moshu via chief, 2026-10-28 (mail 19516/19517/19528). Decision
draft: [ADR-0046](../../../../../docs/architecture/ADR-0046-map-paging-lab.md).
Manifest format: [data_formats §49](../../../../../docs/data_formats.md).

Pages of a large world keep permanent identities as native facets 32–79 and
are loaded lazily from a local, verified page pack. Only the current page and
a few recently visited ones stay indexed; neighbours are verified in the
background but not loaded. This is page travel on the standard map-change
packet, not seamless rendering across a seam, and not network streaming.

Off by default. It compiles only into a Debug build with `GuoLabs=true` and
does nothing unless `GUO_LABS_PAGING` names a page manifest:

```text
dotnet build godot/GUO/GUO.csproj -c Debug -p:GuoLabs=true
set GUO_LABS_PAGING=D:\somewhere\outside\the\checkout\pages.json
set GUO_LABS_PAGING_RESIDENT=3          (optional, 1-5)
launchers\game\play.bat
```

When the shard first moves the player to a page facet, the client replaces
its map loader with `PagedMapLoader` (stock facets load exactly as before),
verifies the page's files against the manifest and builds its block index
before the new map exists. A page that cannot be used is refused: the client
logs the reason, disconnects and shows a login popup naming this doctor. It
never builds a map for a facet the server did not choose, so there is no
playable wrong-facet state.

The swap is refused before it replaces anything when the stock loader carries
applied shard map patches (mapdif/stadif blocks moved off their original
address or count), a custom `MapsLayouts`, or upstream's UltimaLive loader;
paging cannot carry those across yet.

In game: `-paging` (status), `-paging verify` (check every page now),
`-paging reload` (reread the manifest; revised resident pages are dropped,
the current one is rebuilt when next entered), `-paging check` (residency
invariants: current page resident, within the cap, open files match),
`-paging worldmap [facet]` (open the world map, then point its free view at
a facet; a test hook).

The world map works on page facets. Before `WorldMapGump.ChangeMap` asks the
stock loader for a page facet, paging makes it readable: a resident page is
used as is, an evicted one is verified and loaded within the cap (evicting a
page that is neither current nor pinned), and the page is pinned until the
gump's render task ends, so a crossing mid-render never evicts it (the cap
may be exceeded by the pinned page until then). A page that cannot be
loaded (failed verification, or every resident page current or pinned) is
named in the journal with the reason and this doctor; the gump keeps the map
it shows.

## Dependency doctor

No software, service or model beyond the repository's .NET SDK and pinned
mono Godot:

```text
launchers\dev\paging_doctor.bat            tests, manifest check, Labs build
launchers\dev\paging_doctor.bat --probe    ...and the headless loader probe (needs UO_CLIENT_DATA)
launchers\dev\paging_doctor.bat --fixture <dir>   write a synthetic two-page pack (flat land, facets 32-33)
```

Failure messages in the client and the probe name this doctor. The fixture is
synthetic test data, never map content; keep it outside the checkout.

## Code

| File | Role |
|---|---|
| `PageCatalog.cs` | Manifest parsing and validation (engine-free) |
| `PageVerifier.cs` | File sizes, static index bounds, SHA-256 (engine-free) |
| `PagingSession.cs` | Residency: cap, LRU, generation, version invalidation, neighbour prefetch (engine-free) |
| `PageFixture.cs` | Synthetic packs for tests, doctor and probe |
| `PagedMapLoader.cs` | MapLoader widened to 80 facets; loads/unloads one page |
| `PagingLab.cs` | The `World.MapIndex` hook, loader swap, `-paging` command |
| `PagingProbe.tscn` | Headless loader contract against the real install |

`tools/paging_tests` compiles the engine-free files with dotnet; the smoke runs
it on every run and the probe when `GuoLabs=true`.

## Limits

- Needs a shard that already sends page facet IDs; the dev shard does not
  register facets 32–79. A private fixture copy did (scenario, live runs).
- MUL pages only; no UOP page archives.
- The free-view menu lists 80 facets while paging is active; markers are
  not page-aware. Upstream's world map reads its map field late in its
  render task, so switching facets while a render runs can mix two facets
  in that render (upstream race, not fixed here).
- An empty statics file breaks upstream's world map (no reader for a
  zero-length file); real data never has one, and the synthetic fixture
  writes one marker static per page.
- Applied map patches are refused, not carried over: no paging on a shard
  that sends map patches. Proven by the probe only (the test install is UOP,
  which loads no difs).
- Readiness is bounded by count; load time and memory are logged on each
  `facet N ready` line as observations, not budgets.

Scenario: [`tools/scenarios/client/map_paging_lab.md`](../../../../../tools/scenarios/client/map_paging_lab.md).
