# client.labs.map_paging — L4 map paging (ADR-0046)

Requires a Debug `GuoLabs=true` build. Doctor: `launchers/dev/paging_doctor`.

## Contract (no shard)

```text
launchers\dev\paging_doctor.bat --probe
```

Expected: `[paging] PASS` from the dotnet core tests and from
`res://src/Labs/Paging/PagingProbe.tscn`. The smoke runs the core tests on
every run and the probe when `GuoLabs=true`. Covered:

- Manifest: facets 32 and 79 accepted; 0, 5, 31, 80, 126, 127, 255, -1
  refused; duplicate facet/page ID, bad dimensions, files leaving the pack,
  bad hashes, unknown neighbours, wrong kind/format refused.
- Verifier: changed byte, truncated map, short staidx, ragged statics, staidx
  pointing past statics and a missing file are each refused.
- Residency: cap evicts least recently used, never the current page;
  re-entry does not reload; revised content dropped (current page rebuilt on
  re-entry); a file replaced under a resident page refused; late neighbour
  results from an older generation discarded; cap clamped to 1–5.
- Loader (real install): inert with `GUO_LABS_PAGING` unset; facet 0 indexes
  exactly as the stock loader; page index built from the page files; eviction
  closes files; truncated and unknown pages refused.

## Off state

1. Default build (`GuoLabs` unset): Labs sources are not compiled, and the
   seams in `World.MapIndex`, `World.Clear`, `WorldMapGump.ChangeMap` and the objects watch
   are inside `#if GUO_LABS`. A deterministic default build of `GUO.dll` is
   byte-identical with and without them.
2. `launchers\dev\smoke.bat` passes with `GuoLabs` and `GUO_LABS_PAGING` unset.
3. Labs build with `GUO_LABS_PAGING` unset: the probe's first check — the
   seam returns before touching the client and leaves the facet alone.

## Live crossing on a private fixture shard

Run 2026-10-28 (evidence run `20261028_guo-worker-paging_l4-milestone1`,
`data/crossing_report.json`): a private loopback copy of the dev shard with
facets 32/33 added to its `Data/map-definitions.json`, the fixture pack first
in its `dataDirectories`, a fresh owner account from the headless boot and one
standard `Teleporter` per side on lane y=128. Four crossings in one login,
32→33→32→33→32: client facet and position matched the landing each time, no
bounce, `[paging] facet N ready` before every map change, at most 2 pages
resident (cap 2). The driver is kept with the evidence, not in the repository;
never point it at the shared dev shard.

## Milestone 2 on the private fixture shard

Run 2026-10-28 (same evidence run, `data/m2/data/m2_report.json`, 20 checks,
all PASS). The client runs with cap 2. The pages are full size, 7168x4096:

- **Real content:** two pages, facets 32/33 from the connected-pages v3 export.
  Crossed both ways on one of its seams, landing where the export says.
- **Synthetic content:** a 2x2 grid on facets 40–43, flat land with cardinal
  neighbours. Crossings E, S, W, N close a corner loop through all four pages
  within the cap (`-paging check` OK; the evicted pages' files are closed).

Also covered:

- **Evicted page (attempt 6):** a world-map free view on evicted page 41 did
  not crash, but the world map threw a NullReferenceException and showed no
  map: a defect, fixed below.
- **Blocked landing:** the crossing is refused, and client and server agree
  on position afterwards.
- **Followers:** a following dog crosses with the player; a ridden horse
  stays mounted.
- **Revised manifest:** after `-paging reload`, the revised pages are rebuilt
  on entry.
- **Corrupt page (one byte appended mid-session):** readiness is refused. The
  client disconnects with the doctor's name and builds no map, while the server
  had already moved the player.
- **Restarts:** a fresh client logs in onto the page. A kicked client
  reconnects onto it (the auto-reconnect needs the login saved, as upstream).
  After a shard restart the player is still on the page.

## World map on page facets (m2 follow-up)

Run 2026-10-28, `data/m2/attempt9/data/worldmap_report.json`, 11 checks, all
PASS. Driver: `run_m2_worldmap.py`, kept with the evidence.

- The world map renders the current page (40), a resident page that is not
  current (real 33) and an evicted page (real 32), which is loaded on demand
  within cap 2 and unpinned when the render ends. `-paging check` OK after.
- Crossing to 41 while evicted page 42 renders: 42 stays pinned (resident
  41,42), the render completes, then the cap applies again.
- A corrupt page (43): the journal says it is not loaded and why, the gump
  keeps its map and play continues.
- No `error loading worldmap` in the run. Attempts 7 and 8 are kept: 7 hit
  the empty statics file of the old fixture, and 8 crossed after the render
  had already ended, so it did not test the overlap.

## Pinned-page lifecycle, teardown and soak (m2 follow-up 3)

Run 2026-10-28 with the driver `run_m3.py`, kept with the evidence.
`data/m2/attempt16/data/m3_report.json`: every case passed except the soak,
which the client's 15-minute objects-watch limit cut short. The soak was
rerun on its own in attempt17. Shake-outs are kept as attempts 11–15.

- **Revision while a render holds a page, then a crossing (real page 33):**
  the entry keeps the previous copy. The log order is: view, reload, entered
  on the previous copy, render ended. Nothing is unloaded mid-render.
- **Then a view:** a second view of revised page 41 is refused while its
  render runs. The stale copy goes when the render ends, and the next view
  loads v1b.
- **Queued views 42, 40, 42, 43 with crossings:** residency went to 3, which
  is the bound at cap 2 (43 was refused for lack of room). All pins were
  released, then `-paging check` was OK.
- **Disconnect (shard kick, auto-reconnect), logout (fresh client) and quit
  (fresh client), each while a render runs:** no crash. The world is cleared
  with the rendered page left to its render, which releases it. No pin is
  left, and the quit exits 0.
- **Stale current page (real 33, revised with a sand patch):** the world and
  the minimap show the old content, and the world map draws the previous
  copy. After re-entry, the world and minimap switch together. The two world
  map renders differ only inside the patch, and all of it changed
  (`attempt16/data/worldmap_stale_vs_revised.json`).
- **Soak, 32 minutes at cap 2** (attempt17, `data/soak_samples.json`): 355
  crossings and 182 offscreen world-map renders, sampled every 30 s.
  - The live heap after GC goes from 198 to 225 MiB in the first minute,
    stays flat for about 20 minutes, then rises to 234 MiB in the last 12.
  - Private memory stays at about 2.2–2.3 GiB.
  - The working set falls, as the OS trims it.
  - This is a trend, not a budget.
- **Popup:** the refusal popup is shorter and fits the box (see the m2 fix1
  still in `attempt18/regression`).

Each `facet N ready` line logs load time, working set and managed heap; these
are observations only.

Not covered: the world's other real seams and pages, other players on a page,
and a live map-patch shard (refused before the swap; probe only). The driver
lives with the evidence, not in the repository; never point it at the shared
dev shard.

Save captures and logs under the central GUO evidence run, never the checkout.
