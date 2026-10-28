# ModernUO — the local dev shard

The client needs a server to talk to. This is it: a local
[ModernUO](https://github.com/modernuo/ModernUO) shard, running against the
same UO install the client reads, listening on `127.0.0.1:2593` as **GUO Dev**.
Loopback only: `UO_SHARD_BIND` opens it to the LAN, on purpose
(`docs/wiki/Dev-Shard.md`, "Opening it to the LAN").

It is a development dependency, not part of the port. Nothing in
`godot/GUO/` knows it exists; the client connects to `UO_SHARD_HOST` /
`UO_SHARD_PORT` and does not care what answers.

For a separate shard with UO Offline's PlayerBots, see
[the optional PlayerBots profile](../playerbots/README.md).
`launchers\shard\playerbots.bat` provides setup, build, smoke, run, populate and
play commands with its own saves and loopback port.

| | |
|---|---|
| **Upstream** | <https://github.com/modernuo/ModernUO.git> |
| **Licence** | GPL-3.0. Not vendored, not redistributed. The patches in `patches/` modify it and are GPL-3.0 too (see `patches/LICENSE`). |
| **Pinned at** | `d4531cd94` (2026-09-30): `UO_SHARD_REF` in `launchers\_shared\config.bat`. `fetch.bat` checks it out and moves an older checkout to it. |
| **Runtime** | .NET 10 SDK (ModernUO's own requirement, not the client's) |
| **Checkout** | `tools/modernuo/src/` — **gitignored**, ~1 GB with the build |

## Use it

```
launchers\shard\fetch.bat     clone at the pin + apply the patches   (once, and after the pin moves)
launchers\shard\build.bat     publish release win x64     (once, ~2 min)
launchers\shard\run.bat       run it                      (Ctrl-C to stop)
launchers\shard\populate.bat  generate the world          (once, ~10 min)
```

Then, in another terminal, `launchers\game\play.bat`.

Auto account creation is on, so the first login with any name and password
makes that account. The input probe (`launchers\dev\playtest.bat`) logs in as
`guoprobe`, which is also `UO_SHARD_OWNER`, and uses `guomate` for the second
client it starts to trade with.

### Passwords

Nothing ships a password. The first `run.bat` (`configure.py`) generates the
owner's (`UO_SHARD_OWNER_PASSWORD`) and the game master accounts' shared one
(`UO_SHARD_GM_PASSWORD`) into the per-user workspace,
`<UO_WORKSPACE_DIR>\shard\secrets.bat` (`%LOCALAPPDATA%\GUO` by default;
`tools/guo/shard_secrets.py`). It is a .bat of guarded `set` lines:
`common.bat` calls it after `config.bat`, `tools/guo/config.py` reads it, and
the environment or `config.local.bat` still win. Delete it for new ones.

The shard run launcher defers loading generated secrets until `configure.py`
finishes. This matters when it replaces an older password longer than the
login box's 16-character limit: the server must receive the newly saved value
on that same launch. Explicit environment and local configuration overrides
keep their usual precedence. Both launcher twins use this order.

`python tools/modernuo/test_launcher_secrets.py` exercises this migration,
first-run generation, repeat launches and override precedence in temporary
fixtures. It executes the Windows launcher prefix but stops before starting
any server; the shell-export check runs on every platform. The full smoke
includes these checks.

That second account is why `accountHandler.maxAccountsPerIP` is **4** in the
template rather than ModernUO's default of 1: two clients on one machine are
two accounts from one address, and the shard refuses the second with
`Account 'guomate' not created, ip already has 1 account`. On a shard anyone
else can reach, put it back to 1.

## Populating the world

A new ModernUO save is the real map with nobody on it: no creatures, no
vendors, no signs, no doors. What fills it in are in-game commands, so
`populate.bat` logs the owner account in with the client itself and types
them -- `[GenerateSpawners`, `[Decorate`, `[Save`. There is no console path to
this; ModernUO's commands live in the game.

Run it once against a new world. The shard saves the result, so it survives a
restart; delete `Saves/` and it has to be run again.

## What is tracked here, and why

The checkout is gitignored. What is committed is everything needed to
reproduce it:

```
patches/    the diffs applied on top of upstream, numbered, in order
upstream/   upstream-ready versions of the patches, and issue text, held for review
UPSTREAM.md every patch and issue, upstream or ours, and why
config/     the server configuration, as templates
configure.py  fills the templates in from config.bat
```

[`UPSTREAM.md`](UPSTREAM.md) is the tracked list of every change GUO makes to
ModernUO and every bug it has found there. Nothing is submitted upstream until
the owner has reviewed the bundle. A story that changes a patch says "MUO patch"
and updates the list in the same commit.

### `patches/0001-headless-owner-account.patch`

`AccountPrompt.Initialize()` insists on an owner account at first boot and
asks for it at the console. `Core.Headless` is true whenever stdin is
redirected, which is every scripted run, and the prompt throws there instead
of asking.

The patch replaces the prompt, when headless, with the account named by
`UO_SHARD_OWNER` / `UO_SHARD_OWNER_PASSWORD`: created if it does not exist,
raised to owner if it does — which it usually does, because auto account
creation made it a player at the first login. An existing account is raised
only if it holds `UO_SHARD_OWNER_PASSWORD`; otherwise the boot logs a warning
(without the password) and leaves it a player, so a stranger who logs in as
the owner's name first on an open port does not get the shard. A password
anyone can read in GUO's history (the old `guoprobe` default, or the account's
own name) makes and raises nothing either. ModernUO takes its
administration commands in game and not at the console, so without an owner
account the world cannot be generated at all. No password, no owner account.

The same boot makes every account in `UO_SHARD_GM_ACCOUNTS` (comma-separated,
password `UO_SHARD_GM_PASSWORD`; none set, none made) with game master
access, for `launchers\dev\multi_client.bat`:
the shard refuses a second character from one account, so four clients at once
need four accounts, and three of those clients type `[go`. The same rule
holds: an existing account below game master is raised only if it already
holds `UO_SHARD_GM_PASSWORD`.

At every headless boot the owner and game master accounts that already have
their access level are set to the configured passwords (when they do not
already match), so a world saved before the passwords were generated stops
accepting the old ones. `python tools/modernuo/test_account_prompt.py`
compiles the patched file against stand-ins and runs these rules.

### `patches/0002-settable-update-range.patch`

ModernUO hard-codes the 18-tile update range in three places. The patch
routes all three through `Core.GlobalUpdateRange`, read from
`UO_SHARD_UPDATE_RANGE` at boot, including the reply to the client's own
0xC8 request. Why, and what to set: the "Update range" section below.

### `patches/0003-felucca-spring.patch`

Felucca ships in season 4, Desolation: every tree bare, the look OSI gave
Felucca when Trammel split off. The dev shard's characters live on Felucca, so
every screenshot of the client showed a dead world. The patch sets Felucca to
season 0, spring, as Trammel already is. Both clients get the season from the
shard (packet 0xBC), so A/B comparisons are unaffected; only the art changes.
Set it back to 4 in `src/Distribution/Data/map-definitions.json` to test the
Desolation art.


### `patches/0004-guo-pad-dummies.patch`

Adds `Projects/UOContent/Custom/GuoPadDummies.cs`: `[GuoPadDummies` spawns
attack / use / loot / talk / context / pickpocket dummies that write
`GUO_PAD: …` journal lines the pad wheels probe can assert. The attack dummy
watches nearby players' `Combatant` and records a hit when engaged.

### `patches/0005-guo-pad-attack-record.patch`

Requires `0004-guo-pad-dummies.patch` (the `GuoAttackDummy` type). The
patch runners use full filenames: both `0005` patches are applied;
matching numeric prefixes do not replace one another.
The pad patches and editor-channel/system-message patches touch separate
code paths and are retained together.

`AttackReq` also calls `GuoAttackDummy.RecordAttack` so a war-mode attack
packet is journalled even when the first swing has not landed yet (the probe
asserts the binding, not a damage roll).
### `patches/0005-system-message-hook.patch`

`Mobile.SystemMessageSent`, a static hook every system message a mobile is
sent goes through, also when it has no client (AD3). The editor bridge reads
it to return a command's output to the Admin tab's Commands palette; without
the patch commands still run there, without their output. UPSTREAM.md has
the verdict (ours).

### `patches/0006-loopback-ban-exemption.patch`

Prevents a silent local readiness connection from automatically banning all
loopback clients for 15 minutes (NET-00c). Only behavioral ban contributions
from loopback are exempt; explicit bans and LAN/public protection still apply.
The automatic denylist is separate from the IP rate limiter, which already
exempts private addresses. Run
`python -m pytest tools/modernuo/test_loopback_bans.py -q -s` to reproduce the
old hold and check the patch without opening a socket or touching accounts.
See [UPSTREAM.md](UPSTREAM.md) for scope and deployment steps.

### `patches/0007-dfi-loot.patch`

The loot tables of the DFI Network shard (a DefianceUO-based RunUO server),
for the dev shard to match. The SE packs in `LootPack.cs`, the ones an
Endless Journey shard uses, take DFI's values: less gold from Rich upwards,
and more, stronger magic items from Average upwards (SuperBoss rolls 10 to 18
items, several at 33 to 60% minimum intensity). `Custom/DFI/Loot/DfiLoot.cs`
carries DFI's loot helpers and, through one call at the top of
`BaseCreature.OnDeath`, its death bonuses: a paragon drops one more loot pack
by fame and 1.5 times the gold, and a champion spawn monster has a 0.05% chance
of a +5 power scroll for a random looter in Felucca. DFI's plague and elder
bonuses are not ported (ModernUO has neither).

### `patches/0008-dfi-monsters.patch`

Requires `0007-dfi-loot.patch`. Custom monsters from DFI and from the
`kopia 2012` RunUO shard, under `Projects/UOContent/Custom/DFI/`, each added
with `[add <Name>`:

| Group | Monsters |
|---|---|
| Event boss | `QuestMobile` (GM-configured; `GiveRewardBag` gives each looter a "Reward bag for slaying ..." with 6 strong items, maps, SOS and a 5,000 check) |
| Vampires | `VampireKnight`, `VampireFighter`, `VampireNecro`, `MindlessZombie` |
| Stealth | `Stalker`, `NightStalker`, `Undying`, `DarkSentinel`, `HorrorDaemon`, `PoisonousRat`, `Doppelganger` |
| Others | `OrcChopper`, `OrcScout`, `Sphynx` |
| Donation dungeon | `Harvester`, `RoyaltyDefender` |
| Opticon/Kale | `ZealotOfMondain` (dies into `AbominationOfMondain`, carrying four extra loot packs), `OpticonWyrm`, `GrimReaper` |
| kopia 2012 | `ValoriteDragon`, `Chameleon`, `YChameleon` |

DFI's `AI_Stealth` becomes `StealthAI`, chosen through `ForcedAI`, so no AI
enum changes. `UOContent.Tests/Tests/Custom/DfiContentTests.cs` spawns every
monster and checks the SuperBoss tier, the reward bag, the Zealot's death,
the RoyaltyDefender's stages and Undying. Built on its own, the test project
needs `-p:SolutionDir=<src>\` to copy `Distribution/Data` beside it. Not yet ported: the Sphynx fortune reading and the copper bars
of DFI's event reward system.

### `patches/0009-dfi-player-items.patch`

Requires `0008-dfi-monsters.patch`. Player items from both shards, new files
under `Custom/DFI/Items/`, each added with `[add <Name>`:

| Group | Items |
|---|---|
| Gear and bags | `DonationShroud` (on kopia's `HoodedRobe`, an anonymity hood), `Emblem` (DFI's cursed Royal Emblem), `EquipBag` (ten 5-property jewels), `PvPBag` (16 stacks of 60,000 reagents, ammo and bandages) |
| Deeds | `ItemBlessDeed`, `SashLayerDeed`, `SpellChannelingDeed`, `SelfRepairDeed`, `TitleChangeDeed`, `PetResurrectionDeed`, `PetBondingDeed`, `WarHorseBondingDeed`, `SexChangeDeed`, `BeardRestylingDeed`, `ItemIDDeed`, `SpellbookDeed` (and the six `Full*Spellbook`s), `ArtifactDeed`, `ChampionArtifactDeed`, `MinorArtieDeed` |
| Balls | `SkillBall`, `DonationSkillBall`, `PetSkillBall` (DFI's +N balls), `TemplateSkillBall` (kopia's pick seven skills at 100), `StatBall` |
| Shrink | `ShrinkPotion`, `EverlastingShrinkPotion`, `Shrinkbag`, `ShrinkStone`, `HitchingPostEastDeed`, `HitchingPostSouthDeed`, the `[Shrink` command |
| Runic tools | hammers, saws, fletcher and tinker tools in eight metals, three sewing kits, their bags, `BagOfCraft`, `RunicStone`, `CraftingStone` |

Where both shards had a deed, it takes DFI's checks and kopia's blessed look.
kopia's `SkillBall` is `TemplateSkillBall` here, beside DFI's. ModernUO's own
`NameChangeDeed` stands in for kopia's. Gumps keep their choices and order but
are laid out afresh; known quirks of the originals are kept and commented.
`DfiItemTests.cs` constructs every item and checks the bags, deeds, balls,
shrinking and the Emblem.

### `patches/0010-dfi-spawns.patch`

Requires `0009-dfi-player-items.patch`. DFI Network's world spawns, as
ModernUO spawner JSON under `Distribution/Data/Spawns/dfi/`:

| File | Spawners | From |
|---|---:|---|
| `felucca.json`, `ilshenar.json`, `malas.json` | 1,398 | DFI's 2008 ESpawner exports (`*-ESS.xml`) |
| `dfi-boss-arena.json` | 13 | staff-placed spawners in DFI's 2018 save (Green Acres) |
| `kopia-free-items.json`, `kopia-faction-extras.json` | 40 | staff-placed spawners in kopia's 2018 save |

`[DfiSpawns` (Administrator) deletes the classic monster and wildlife
spawners those layouts replace, by the guids in ModernUO's own spawn files,
and imports the DFI files. Vendors, townspeople, reagents, sea life and the
Mondain's Legacy areas stay. It is safe to run again. Spawn types that wait on
unported DFI systems (Cursed Cave, PriestOfMondain, stealable rares, tokens)
were left out of the files; ModernUO's renames (ToxicElemental as
AcidElemental, crops as Farmable*) were applied. Delays keep DFI's values,
up to the month-long rare spawns. `DfiSpawnTests.cs` and ModernUO's
`AllSpawnFilesLoadTests` load every file.

### Retired

`0004-multi-tile-enumerator` fixed tile lookups that stopped at a multi with no
tile at the point and skipped the multis after it. Reported as
modernuo/ModernUO#2682 and fixed upstream in #2685 (`d4531cd94`), so the pin
moved there and the patch is gone. A checkout that still has it applied: run
`git -C tools/modernuo/src checkout -- Projects/Server/Maps/Map.StaticTileEnumerator.cs`,
then `fetch.bat`.

Keep this list append-only and numbered. A patch that upstream adopts should
be deleted, not silently dropped from the set.

### Upstream issues to report (not patched here)

- **`MultiData.LoadUOP` never reads an uncompressed entry.** An authored
  multi written uncompressed fails the boot; stock entries are all compressed,
  and `tools/uodata_write` writes compressed, so GUO needs no patch. The issue
  text is in `upstream/issue-multidata-loaduop-uncompressed.md`; see
  [`UPSTREAM.md`](UPSTREAM.md).

### `config/`

`modernuo.template.json` is the full server configuration with three values
left as placeholders — `@UO_CLIENT_DATA@`, `@UO_SHARD_NAME@`,
`@UO_SHARD_LISTENER@` (`UO_SHARD_BIND:UO_SHARD_PORT`, loopback by default)
— and `expansion.json` pins the expansion to **Endless
Journey** (Id 11), which is what a 7.0.x client expects.

`configure.py` writes both into `src/Distribution/Configuration/` on first run,
resolving the placeholders through `tools/guo/config.py` — the same
environment → `config.local.bat` → `config.bat` → shared-config order
everything else here uses.

It writes only files that do not exist, with one exception: the listener in
an existing `modernuo.json` is set to `UO_SHARD_BIND` at every run, so a file
from before the shard was closed by default is closed too. Edit the generated
file to change any other setting on this machine; edit the template to change it for everyone, and say
so in the commit.

Without this, ModernUO asks for its data directory and expansion at a console
prompt on first boot, and refuses to prompt when stdin is redirected:

```
HeadlessConsoleInputException: Interactive console input required but the
server is headless (stdin is not a TTY)
```

## Update range

UO servers tell a client about items and mobiles within 18 tiles, a number
chosen when the client was 640x480. This one draws map art some 70 tiles out,
so everything the shard owns -- doors, signs, decoration, NPCs -- used to stop
dead in a circle while the terrain carried on, and objects at its edge
appeared as you walked up and were dropped again a tile later.

`UO_SHARD_UPDATE_RANGE` in `launchers\_shared\config.local.bat` sets it; 72 covers
a 4K window. Patch `0002` routes ModernUO's three hard-coded copies of 18
through `Core.GlobalUpdateRange` so the one setting reaches all of them,
including the reply to the client's own 0xC8 request -- the client uses that
reply to decide when to forget an object, so the two numbers have to agree.

Unset, it is 18 and the shard behaves as a production one, which is what you
want when checking parity.

## Gotchas

**Clone it fully.** ModernUO versions itself with Nerdbank.GitVersioning,
which walks the history. A shallow clone fails the build with *"Shallow clone
lacks the objects required to calculate version height"*; `git fetch
--unshallow` fixes an existing one.

**The world is saved.** `src/Distribution/Saves/` and `Backups/` hold the
shard's state, including the characters the probe makes. Delete `Saves/` for a
clean world; that is also how to get rid of a character whose name the probe
has taken.
