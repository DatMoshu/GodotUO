# ADR-0026: Federated pack catalogues, signed indexes, and server adapters

## Status

Accepted — 2026-10-02 (the owner's decision).

## Date

2026-10-02

## Decision Makers

The owner, relayed by the director (GUO-Director, 2026-10-02). The director
took over the asset store from Codex the same day and wrote this record.

## Summary

The GUO pack format, the client's installer and the pack tools stay in this
repository, public and BSD-2-Clause. Pack **discovery** is centralised. There
is one official catalogue that the client knows by default, reviewed in the open
in its own public repository. Pack **hosting** is not centralised: any HTTPS
host can serve the files, including a community member's site or the owner's
own server. Anyone can run a catalogue, public or private, and a shard can name
the packs it needs.

A catalogue signs its index. The client verifies the signature, and the index
pins every pack's SHA-256. A pack is then the same pack wherever its bytes come
from. Packs carry data, never server code. Each server backend gets its own
small adapter that reads one neutral server-content file, so no pack is bound
by a server's licence.

## Context

ADR-0019 built a store with one configurable address. Pack URLs had to stay at
that address, and HTTPS was the only proof of who published a pack. That was
enough for one local store. It is not enough for what the store has become:

- Packs now carry art, animation, maps, server items, creatures and scripts
  (Codex, `asset_pack_ecosystem.md`). Their provenance matters more.
- The community already hosts UO assets elsewhere, and the owner wants those
  hosts to be first-class rather than competitors.
- Shards will want their own catalogues and their own required packs.
- The owner has a dedicated storage server (RAID 10, about 11 TB usable) for
  hosting.

## Decision

### 1. Three layers

| Layer | Where it lives | Licence and visibility |
|---|---|---|
| Format, client installer, pack tools, catalogue tools, server adapters | this repository | BSD-2-Clause, public |
| The official catalogue: one entry per pack, reviewed by pull request | its own public repository (`GodotUO-packs`) | entry files CC0; packs keep their own licences |
| Hosting the pack files | any HTTPS host: the owner's server, GitHub release assets, a community site | the host's choice |

A hosted service with accounts and an upload API, if one is ever built, is a
fourth layer and may be private. It would hold operational secrets and policy,
not the protocol. Nothing in the client depends on it.

### 2. Signed catalogue index, `guo/store-index@2`

A catalogue publishes `index.json` and a detached signature `index.json.sig`.
The signature is Ed25519 (RFC 8032) over the exact bytes of `index.json`. The
index names its catalogue, a monotonic `sequence`, an `expires` time and its
packs. Each pack entry gives the manifest, the ZIP's `sha256` and `size`, and
one or more absolute `urls`. Those URLs may be on any host and are tried in
order. Integrity comes from the signed hash, not the host. A client refuses:

- a bad signature;
- an index whose `sequence` is lower than one it has already seen from that
  catalogue (rollback protection);
- an expired index, for a catalogue that sets `expires`;
- a plain-HTTP URL that is not loopback or a private LAN address.

Version 1 indexes (unsigned, same-origin URLs) keep working for a local or
LAN store, and the client labels them **Unsigned**.

Ed25519 is verified in managed C# with `System.Numerics.BigInteger`
(`src/Store/Ed25519.cs`), not the platform's crypto. .NET's browser build
has no signature APIs, and a portable verifier behaves the same on Windows,
Linux, Android and the web export. Python signs and verifies with a matching
pure implementation (`tools/asset_store/ed25519.py`). Both are tested against
the RFC 8032 vectors and against each other.

### 3. Trust

- The client ships the official catalogue's public key or keys. Shipping a
  new client is how a key is rotated.
- A user adds any other catalogue by URL. On first fetch the client shows the
  key's fingerprint and the catalogue's name, and the user approves it. The
  key is then pinned to that catalogue. A changed key is refused until the
  user approves it again.
- A private catalogue is an unlisted URL: on a LAN, behind the owner's own
  access rules, or a shard's. Catalogue-level authentication (a bearer token
  per catalogue) is reserved in the format and not implemented in this pass.
- The Store window shows which catalogue lists a pack and where it was
  downloaded from. A pack listed by several catalogues is one pack when the
  hashes match.

### 4. Shard content

A shard names the packs it needs with a **shard content descriptor**
(`guo/shard-content@1`), served over HTTPS next to its catalogue. The
descriptor lists the catalogues and the deployment lock (exact pack versions,
hashes and numeric ID bindings). A server-list entry carries its URL as
`content`. Before connecting, the client fetches it, offers to install what
is missing, and selects that lock **for that shard only**. Changing the
mounted content still needs a client restart (ADR-0019 Amendment 4's mount
happens at archive load). Per-shard selection replaces the installation-wide
selection.

### 5. Server adapters, and why packs avoid the GPL

Packs never contain server code. The store's server export writes one neutral
file, `guo/server-content@1`: items, tiledata, map blocks, regions,
decorations, loot and creatures, with numeric IDs already bound. Each backend
has an adapter that reads that file:

| Backend | Adapter | Licence position |
|---|---|---|
| ModernUO (GPL-3.0) | the C# bridge in `tools/editor_shard/bridge`, loaded by the server | BSD-2-Clause source, GPL-compatible. A build distributed with ModernUO is distributed under GPL-3.0 terms, and its source is public here. |
| ServUO, RunUO (GPL-2.0) | a C# script package, same shape as the ModernUO bridge | as above |
| POL | an eScript package that reads the export, or one generated from it | check POL's licence before shipping |
| Sphere, UOX3 and others | generated script files | per server |

Packs are data read by these adapters, so a pack's licence is its own. The
adapters are the only code that touches a server, and each is kept small.
ModernUO comes first, end to end. The others follow once it is proven, as
listed in `docs/store/server_backends.md`.

### 6. Art overrides are allowed, for original art

ADR-0019 kept art overrides disabled. The v2 content packs mount art, gumps,
animations, sounds, music, fonts and cliloc text through explicit numeric
bindings in a lock (ADR-0019 Amendment 4, `asset_pack_ecosystem.md`). That is
now allowed for **original** work only, under the content policy in
`docs/store/content_policy.md`. Art extracted from the client or derived from
it is refused whatever its licence field says. That includes recolours,
edits, upscales and image-to-image generations from client art.

### 7. Hosting the official mirror

The official catalogue's files are mirrored on the owner's server: static
files, read-only to the web server, behind TLS. Exposure, router and DNS
choices are in `docs/store/hosting.md`. A mirror is a plain HTTPS host, so a
community site mirrors the same way.

### 8. Administration from Godot

Publishing, catalogue review, mirror health and deploying a lock to a shard
become windows in the GUO editor and the client's admin tools. They call the
same tools the command line does. The command line stays complete.

## Alternatives

- **A private store repository.** The client is public and BSD, so secrecy
  would hide nothing an attacker needs. It would cost the trust of shard
  owners, who need to see what the client downloads and runs. Rejected.
- **One central host for every pack.** Duplicates what community sites
  already do and makes the project carry every byte. Rejected in favour of
  central discovery and any host.
- **Publisher signatures on each pack.** Useful later. In this pass the
  catalogue vouches for the hashes it lists. The format reserves a
  `signatures` field on pack entries.
- **ECDSA through .NET's crypto.** Not available on the browser build and
  differs by platform. Rejected for the managed Ed25519 verifier.

## Consequences

- One more public repository to moderate (the catalogue). Moderation is pull
  request review.
- The official signing key needs the same custody as the Android keystore: a
  GitHub Actions secret for the catalogue repository, and an offline backup.
- The client gains a catalogue list, an approval prompt for new catalogue
  keys, and per-shard content selection.
- Each server backend beyond ModernUO is new adapter work, listed and
  sequenced but not started.

## ADR Dependencies

ADR-0019 (asset store), ADR-0014 (world objects and the shard bridge),
ADR-0022 (authoring UO data files).

## Engine Compatibility

Godot 4.7.2 mono, .NET 8. No engine feature, no GDExtension. The Ed25519
verifier uses only `System.Numerics` and `System.Security.Cryptography.SHA512`.
The browser build has `SHA512` (it is a hash). If it is missing there, the
verifier carries its own.

## GDD Requirements Addressed

None. This is infrastructure.

## Validation

- **Done, 2026-10-02:** `python tools/asset_store/test_catalogue.py`, 9
  tests, in CI.
  - The RFC 8032 vectors pass in C# and Python. The Python signer matches
    the `cryptography` package, and the C# verifier verifies Python's
    signatures on random messages.
  - The real C# client, against a signed catalogue served locally:
    - it asks for approval of a new key;
    - it installs a pack whose first URL is dead from the mirror that serves
      the listed bytes;
    - it refuses a tampered index, a rolled-back index and a changed key.
  - The existing v1 suites (`test_store`, `test_corpus`, `smoke.py`,
    `test_content`, the script tests) still pass.
- **Done, 2026-10-02: ModernUO end to end** (`python tools/shard_content/run.py prove`
  on the editor's private shard):
  - `deploy` installed `sample-content-combined` from a signed catalogue served
    on this computer, wrote the lock, the ModernUO export and the descriptor.
    The restarted shard's bridge logged the deployment's `identity_hash`.
  - The pregame probe played a server entry naming the descriptor through the
    Servers screen: the note, Play, the question with the catalogue key's
    fingerprint, the install, and the restart with the session (36/36).
  - A client started with that session mounted the lock, logged in, and the
    shard's `[GUOPackItem` put the pack's item in the backpack. The client
    knows it by the art slot the lock gave it (`0x1771`) and draws the pack's
    art there.
  - `test_shard_content.py` (8 tests, in CI) covers deploy and install without
    a shard. It refuses a different catalogue key, a signed catalogue claimed
    unsigned, an unsigned remote catalogue, a lock that no longer matches, and a
    lock that leaves client components without a slot.
  - Found during the proof: a lock that leaves client components unbound used
    to crash the client at boot. Deploy and install now refuse it. A shard
    session whose packs fail to mount is dropped, and GUO starts on the
    player's own files (checked by hand on the failing lock).
- **Not yet:** the descriptor's catalogues on HTTPS hosts (only loopback has
  been run); the dev shard loading an export (only the private copy has); a
  shard whose content changes while a player has the old lock (the player gets
  NeedsContent again, untested).
