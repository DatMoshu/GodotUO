# ModernUO: what GUO changes, and what goes upstream

Every change GUO makes to ModernUO, and every ModernUO bug GUO has found, with
a verdict: **upstream** (worth offering to modernuo/ModernUO) or **ours**
(GUO's dev shard needs it; ModernUO would not want it). Nothing here has been
submitted. The owner reviews the patches first and then sends one bundle.

Pinned at `d4531cd94` (`UO_SHARD_REF` in `launchers\_shared\config.bat`).
`python tools/modernuo/test_upstream.py` checks that this list names every
patch file and that every patch applies cleanly to the pin.

**The rule.** Every story that changes ModernUO, a patch file here or a
backend's patch set, says "MUO patch" in its STORY_DONE and its commit, and
updates this list in the same commit. Other backends get their own list
beside their tool (`tools/<backend>/UPSTREAM.md`) the same way.

## Two folders

| Folder | What | Applied by |
|---|---|---|
| `patches/` | What the dev shard runs: GUO's environment variables, GUO comments | `launchers\shard\fetch.bat`, `tools/muo_shard` |
| `upstream/` | The upstream-ready versions: ModernUO's settings and naming, old defaults unchanged, no GUO wording | nothing; they wait for the bundle |

An upstream version is written against the pin on its own, not on top of the
GUO patch. When upstream adopts one, the pin moves past it and the GUO patch
is retired (as `0004-multi-tile-enumerator` was).

## The list

| # | Change | Verdict | Upstream-ready version |
|---|---|---|---|
| 0001 | Headless owner account | **split**: owner creation upstream, the rest ours | `upstream/0001-headless-owner-account.patch` |
| 0002 | Settable update range | **upstream** | `upstream/0002-update-range-setting.patch` |
| 0003 | Felucca in spring | **ours** | none |
| MV1 | Owner promotion needs the owner password | **ours** (until SF2 lands it, part of 0001) | none |
| 0004 | Multi tile enumerator | **upstreamed**, retired | reported #2682, fixed in #2685 |
| issue | `MultiData.LoadUOP` and uncompressed entries | **upstream** (issue, no patch) | `upstream/issue-multidata-loaduop-uncompressed.md` |

### 0001: headless owner account

GUO patch: `patches/0001-headless-owner-account.patch`. SF1 changed it (no
default passwords, GM accounts take `UO_SHARD_GM_PASSWORD`, the configured
passwords are re-applied at each headless boot).

Headless ModernUO (stdin redirected, so every scripted or service run) throws
at the first-boot owner prompt. The GUO patch does four things when headless:

1. creates the owner account from `UO_SHARD_OWNER` / `UO_SHARD_OWNER_PASSWORD`;
2. raises an existing account of that name, and its characters, to owner;
3. creates or raises the `UO_SHARD_GM_ACCOUNTS` game master accounts;
4. sets those accounts' passwords to the configured ones at every boot.

**Verdict, split.** Part 1 is a real gap upstream: a headless server cannot
make its first owner account at all. Parts 2 to 4 are GUO's dev-shard
conveniences and would be wrong on a public shard. Raising whoever holds a name
to owner hands the shard to the first stranger to log in under it, and staff
accounts and password resets from the environment are a deployment choice, not
a server feature.

The upstream version, `upstream/0001-headless-owner-account.patch`, keeps
ModernUO's own condition (no accounts at all) and only replaces the console
prompt when headless: it creates the owner from `MODERNUO_OWNER_USERNAME` and
`MODERNUO_OWNER_PASSWORD` (named like ModernUO's existing `MODERNUO_*`
variables), and with either unset it logs how to set them and creates nothing,
as answering "n" at the prompt does. No promotion of an existing account, no
game master accounts, no password changes. Interactive startup is unchanged.

### 0002: settable update range

GUO patch: `patches/0002-settable-update-range.patch`. ModernUO hard-codes
the 18-tile update range (README, "Update range").

**Verdict: upstream.** Any client with a window larger than 640x480 sees items
and mobiles stop in a circle while the map carries on; a server setting fixes
it and costs nothing when unset.

The upstream version, `upstream/0002-update-range-setting.patch`:

- reads `network.updateRange` from `modernuo.json` in `Core.Setup`, after the
  configuration loads, with `ServerConfiguration.GetSetting` (absent, the key
  is not written and the range stays 18), clamped to 5-200 with a warning;
- sets `GlobalMaxUpdateRange` to the range plus 6, ModernUO's 18/24 spacing;
- routes the literal 18 in `Item`, the 22 in `BaseMulti` (range + 4) and the
  0xC8 reply through `Core.GlobalUpdateRange`;
- also routes `HouseFoundation`'s 18 and 24 (custom houses) through
  `GlobalUpdateRange` and `GlobalMaxUpdateRange`, which the GUO patch misses;
- adds no byte-order marks. The GUO patch adds one to `BaseMulti.cs`, `Item.cs`
  and `IncomingPlayerPackets.cs`, which upstream would refuse.

Built against the pin (`UOContent` Release, 0 warnings) and
`Server.Tests` `PlayerPacket*` pass (38).

**Open, GUO patch:** custom houses on the dev shard still stop at 24 tiles with
a raised range, and the BOMs are still in the GUO patch. Either is a story of
its own (a MUO patch), not fixed here.

### 0003: Felucca in spring

GUO patch: `patches/0003-felucca-spring.patch`. Felucca's season 4
(Desolation) becomes 0 (spring) in `map-definitions.json`.

**Verdict: ours.** Desolation on Felucca is OSI's choice and ModernUO matches
OSI. GUO wants green trees in its screenshots; a shard owner who does too edits
the same JSON.

### MV1: owner promotion needs the owner password

On `work/muo-shard-mv1` (`c55fa593`), not yet on main; SF2 brings it. It
changes the GUO 0001 so that an existing account is raised to owner only if
it already holds `UO_SHARD_OWNER_PASSWORD`; until it lands, the dev shard's
0001 raises any account that has the owner's name (part 2 above). A guard on
GUO's own convenience, so **ours**; the upstream 0001 has no promotion to
guard. SF2 updates this row when it merges.

### 0004: multi tile enumerator (retired)

Tile lookups stopped at a multi with no tile at the point. Reported as
modernuo/ModernUO#2682, fixed upstream in #2685 (`d4531cd94`); the pin moved
there and the patch was deleted.

### Issue: `MultiData.LoadUOP` never reads an uncompressed entry

For an entry with compression flag 0, `LoadUOP` parses `buffer` without
reading the entry, so it parses the previous entry's bytes and fails at boot.
A stock install never hits it (every stock entry is compressed); an authored
multi written uncompressed did, on 2026-09-27. Still present at the pin.

**Verdict: upstream, as an issue.** The text, with the one-line fix, is
`upstream/issue-multidata-loaduop-uncompressed.md`. GUO carries no patch:
`tools/uodata_write` writes multi entries compressed.

## The bundle, when it is sent

1. The owner reviews `upstream/` and this list.
2. Re-check against ModernUO's current main (`git apply --check`); refresh any
   patch that drifted.
3. One PR per patch (upstream reviews them separately), plus the issue.
4. Record the PR and issue numbers in the table above; when one merges and the
   pin moves past it, retire the GUO patch.
