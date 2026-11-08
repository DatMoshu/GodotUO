# Provenance of the DFI shard content patches

Covers `0007-dfi-loot.patch` through `0024-dfi-conpvp-arena.patch`.

## What they are

Server-side ModernUO patches. Like ModernUO they are licensed GPL-3 (see
[`LICENSE`](LICENSE)). They compile into the shard; nothing here ships in the
GUO client.

## Where they come from

They are ported from the published custom scripts of two defunct RunUO shards:

- **DFI Network**, a DefianceUO-based shard
- **kopia 2012**

Patch 0023 also ports the Solaris BoardGames scripts that DFI Network ran.
Spawn layouts come from those shards' spawner exports and from staff-placed
setups recovered from their world saves.

Ported files cite their origin in the file header or in their patch's section
of [`../README.md`](../README.md), with the original script path where one is
known. Test files and world-placement generators are new code written for this
port. [`../UPSTREAM.md`](../UPSTREAM.md) has the verdict for each patch.

## What they do not contain

No retail client data and no artwork. The patches are C# source and JSON data.
They refer to client art only by its numeric ids, as any UO server does.

## Basis for use, and takedown

We believe this use is permitted under the open-source terms those scripts were
distributed under. That is our reading, not a statement from the original
authors.

**If you wrote any of this and object, it will be removed on request.** Please
open an issue on this repository's GitHub page.

## Credit

Ported to ModernUO by Servietapoe.
