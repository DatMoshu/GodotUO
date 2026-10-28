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

1. Default build (`GuoLabs` unset): Labs sources are not compiled and the
   `World.MapIndex` seam is inside `#if GUO_LABS`. A deterministic default
   build of `GUO.dll` is byte-identical with and without the seam.
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

- **Evicted page:** a world-map free view on an evicted page (41) neither
  crashes nor reloads it. The upstream world map logs its own load error and
  shows no map (observation, not a fix).
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

Each `facet N ready` line logs load time, working set and managed heap; these
are observations only.

Not covered: the world's other real seams and pages, other players on a page,
and a live map-patch shard (refused before the swap; probe only). The driver
lives with the evidence, not in the repository; never point it at the shared
dev shard.

Save captures and logs under the central GUO evidence run, never the checkout.
