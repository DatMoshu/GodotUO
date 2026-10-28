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

Not yet run: a corrupted page or revised manifest during a live session,
blocked landings, other players, followers, reconnect and shard restart.

Save captures and logs under the central GUO evidence run, never the checkout.
