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

## Live (needs a shard that registers page facets; NOT RUN in milestone 1)

1. Write a pack: `paging_doctor.bat --fixture <dir outside the checkout>`;
   set `GUO_LABS_PAGING` to its `pages.json`.
2. Shard: register facets 32 and 33 with the same files and enable a
   crossing between them (the proof-v7 crossing patch, generalised).
3. Log in with a scratch account, walk across the seam both ways several
   times. Expect `[paging] facet N ready` in the client log before each map
   change, `-paging` showing at most the cap resident, client and server
   coordinates matching, no bounce.
4. Corrupt one page file, re-enter it: the journal names the problem and
   the doctor; `-paging verify` lists it.
5. Edit the manifest version, `-paging reload`, re-enter: the page rebuilds.

Save captures and logs under the central GUO evidence run, never the checkout.
