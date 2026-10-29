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
| `kopia-faction-extras.json` | 13 | staff-placed spawners in kopia's 2018 save |
| `dfi-custom-monsters.json` | 12 | the custom monsters from `0008`, placed for this shard |

The custom monsters' spots are new: DFI's live placements did not survive,
and only the Stalker's hideouts are in its code. Each sits one tile from a
layout spawner in the right area (the importer would replace a same-type
spawner on the same tile; a test guards it): Stalker at its Ilshenar
hideouts, a vampire lair in the Yew graveyard, stealth ambushers in Despise
and Covetous, the Sphynx near Compassion, the Valorite Dragon in Destard,
the Chameleon in Ice and young ones in the Yew woods, the Zealot of Mondain
in Wrong, Opticon's Pet in Fire, and the Harvester and Royalty Defender in
the Green Acres boss arena. Undying (killable only by a hue 2121 weapon
nothing makes yet) and the GM event QuestMobile are not spawned.
`[DfiSpawns` reports each custom spawner's spawned count.

kopia's free-items room (27 spawners handing out deeds, ethereals and a bank
check) was dropped at the owner's request; `[DfiSpawns` deletes any spawner
still named `kopia-free-items`, and what it spawned.

`[DfiSpawns` (Administrator) deletes the classic monster and wildlife
spawners those layouts replace, by the guids in ModernUO's own spawn files,
and imports the DFI files. Vendors, townspeople, reagents, sea life and the
Mondain's Legacy areas stay. It is safe to run again. Spawn types that wait on
unported DFI systems (PriestOfMondain, tokens) were left out
of the files; ModernUO's renames (ToxicElemental as AcidElemental, crops as
Farmable*) were applied. Delays keep DFI's values, up to the month-long
artifact spawns. `DfiSpawnTests.cs` and ModernUO's `AllSpawnFilesLoadTests`
load every file.

### `patches/0011-dfi-rares.patch`

DFI's stealable rares: 9 town rares (dried onions, herbs and flowers,
horseshoes, arrow shafts, four wires) a thief takes from 80 Stealing (sure
at 100), and 22 Doom rares with an artifact rarity (tarot, skull candle,
ruined painting, hanging armour, a reversed backpack and more) from 100
(sure at 150); plus DFI's dungeon decoration (hay, rocks, a closed barrel)
and an eatable locked fruit basket. A rare is fixed on the ground until
stolen; the spawner that placed it puts a new one back after DFI's delay.
DFI never connected its rares to the Stealing skill; one branch in
`Skills/Stealing.cs` does, at the owner's request.

0010's layouts place the Doom rares where DFI's 2008 export did. DFI kept
the town rares in shop containers that did not survive, so
`dfi-town-rares.json` puts them in Britain's blacksmith, bowyer and
herbalist, with DFI's delays. `[DfiSpawns` reports the rares on the ground
and how many rare spawners hold one. `DfiRareTests.cs` covers every rare
and the steal rules.

### `patches/0012-dfi-cursed-cave.patch`

DFI's Cursed Cave quest zone, from `Scripts/Custom/Engines/Quest System/CursedCave`
and its summoning altar engine. Two regions on Felucca: levels 1 and 2, a pair
of walled halls at 6026,266 (107 by 108), and level 3, a cave at 5124,1412
(246 by 101). Both stand on the stock map. Inside, players cannot cast and the
bard skills fail, as on DFI.

- **Creatures:** the cursed, cursed archers, mages and necromancers (ModernUO
  has no necromancer AI; the mage AI casts), cursed warriors that rise again
  as cursed skeletons, cursed guardians that drop the cave key, four named
  townsfolk that may drop their trade's sturdy tool, Deva on her cursed
  steed, who calls for help below half health, and Hephaestus, the god of
  fire. A player who dies in the cave rises as a cursed copy carrying their
  gear, five at most per player.
- **Quest:** Warren the Guard gives "Cursed Cave": six rounds of kills inside
  the cave, then a reward bag that can hold the Longsword of Justice or the
  Bow of Hephaestus. He also takes ten cave rares for a reward bag that may
  hold the Harvester of the Ghost.
- **Artifacts:** the three talking weapons level from 1 to 10 on the cave's
  kills.
- **Rares:** 13, fixed on the ground and stolen from 100 Stealing, like
  RunUO's stealable artifacts.
- **Hephaestus' chamber:** a cave key opens it from outside and queues a
  summoning on its altar. Party members on level 3 are asked to follow.

None of DFI's saves kept the cave's placed fixtures, so `[CursedCave`
(Administrator) places GUO's layout, replacing any placed before:

- Warren and a dark gate north of Minoc Mines, where DFI's hint gump put the
  cave.
- Walk-on passages from the entrance to level 1 and from level 2 to level 3.
- Keyword doors between levels 1 and 2 (say "hephaestus").
- Level 3's south-west dead end sealed as the chamber: the magic door,
  blockers, the altar and an exit passage.

`dfi-cursed-cave.json` holds 40 spawners:

- Levels 1 and 2: DFI's 2008 layout. Its four spawners that 0010 kept with
  the cursed entries stripped move here from `felucca.json`.
- The rares: at the spots in DFI's stealable artifact list, with its delays.
  The mortar and pestle stands where the 2008 layout had it.
- Level 3: GUO's layout. DFI's files have no spawns there.

Run `[CursedCave`, then `[DfiSpawns`. DFI's blood-draining toolkit, the
blood pentagram and Elda's blood quest were switched off on DFI (their
double-click handlers return at once) and are not ported. The tent camp
multis are ported but not placed. `DfiCursedCaveTests.cs` covers the
creatures, items, rares, levelling, altar, doors, quest rounds, Warren's
rewards, the fixtures and the spawn file.

### `patches/0013-dfi-invasions.patch`

DFI's town invasions, from `Scripts/Custom/Engines/InvasionSystem`. DFI ran
one through an altar and spawners that staff placed by hand. Here
`[invasion` (GameMaster) runs them in the 14 main Felucca towns: Britain,
Minoc, Trinsic, Vesper, Yew, Moonglow, Skara Brae, Jhelom, Magincia, Cove,
Ocllo, Nujel'm, Serpent's Hold and Buccaneer's Den.

- **`[invasion`:** opens the control gump. It shows the default settings
  (change them and press Apply) and every town, with its invasion's level
  and kills and a Start or Stop button.
- **Typed forms:** `[invasion start <town> [preset]`, `[invasion stop <town|all>`,
  `[invasion status`, `[invasion presets` and `[invasion towns`. A town may be
  shortened ("serp", "skara").
- **`[invasion set <setting> <value>`:** changes the defaults and every
  running invasion. The settings are `preset`, `bosskills` (DFI's 840),
  `maxalive` (40), `radius` (25), `buffs` (on/off), `guards` (on/off) and
  `boss` (a creature type, or `default`). A running invasion keeps the
  preset it started with.

The flow is DFI's:

- Invaders get DFI's buffs: double hits, +5 damage, and more strength,
  dexterity, intelligence, skills, speed, fame and karma.
- The town is kept topped up to the alive limit every five seconds.
- Each fifth of the kill target raises the level and sends stronger
  creatures.
- At the target the boss rises at the town's centre. When it dies the town
  is saved, and the ten best killers see the top list two minutes later.
- Guards are off in an invaded town until it ends, unless `guards` is on.

There are seven presets: orcs (OgreLord), undead (Neira), daemons (Semidar),
reptiles (Rikktor), vermin (Barracoon), arachnids (Mephitis) and cursed
(Hephaestus, the Cursed Cave's creatures). Running invasions and the defaults
are saved with the world.

DFI sorted its top list from the fewest kills up and drew every row on one
line; the port lists the best first, one row each. `DfiInvasionTests.cs`
covers the presets, town names, settings, buffs, the command and a full run
to the boss.

### `patches/0014-dfi-quests.patch`

Three of DFI's quests with their artifacts, and the creatures and loot they
lean on. `[DfiQuests` (Administrator) places the quest givers and the
Plague's altar, replacing any placed before; `dfi-rare-plants.json` grows the
Elder Wizard's roots.

- **The Plague** (Zuleika): one in twenty new spawns in Felucca's dungeons
  rises plagued (green, double hits, stronger), the roll sitting after
  ModernUO's paragon roll in `BaseCreature.OnBeforeSpawn`. Plagued corpses
  hold poison fungus (DFI made players carve it out) and now and then a
  plague chest. Zuleika takes 200 fungus and sends the player, and any party
  member who accepts, to the Alternate Dimension: the spiral halls on Trammel
  at 6093,441. Its altar summons The Plague, who throws skulls that hatch
  plagued slimes and beasts. His artifacts: the Book of Lost Knowledge, the
  Gloves of the Lepracon, Grizzly's Courage and the Bow of Weakening, which
  levels on one of six creature kinds.
- **Slayer of evil!** (Nimre, Opticon's Midas quest): six rounds, one Doom
  creature each, ending at the Dark Father. The reward bag holds a 5,000 gold
  check and a treasure map, often the Midas Touch (a one-use +300 smithing
  hammer), and one time in ten Yeti's Pads, the Necronomicon, Behemoth or
  Dragon Clenching Claws.
- **Elder Wizard** (Gabrielle): four of each of four rare roots, at Cove's
  orc fort, the Fire Island temple, Yew's cemetery and the Compassion shrine,
  as her plant book says. Picking one may disturb a nest of snakes, spiders
  or serpents, a third of them plagued or elder (white, five times the
  hits). The reward is a reagent bag that any vendor selling reagents refills
  at their own prices when it is dropped on them.
- **Armor of the Sun**: six pieces with three or four properties each; the
  full set gives the legs 900 luck. A dug treasure chest may raise a Sun
  Guardian carrying a piece (the one line in `TreasureMap`), and champions
  slain in the Lost Lands carry one six times in ten.
- **Evil orc helm**: from orc brutes and orcish lords; +10 strength and -10
  to the wearer's higher of dexterity and intelligence.

Placements are GUO's where DFI's saves had none: Nimre stands where DFI's
last save had him, Gabrielle in Buccaneer's Den where DFI's hint gump put the
Elder Wizard, Zuleika beside her, and the Alternate Dimension gets a passage
back. DFI's elders never spawned on their own, and do not here. Gabrielle
sells ModernUO's mage stock. `DfiQuestTests.cs` covers every type, the
plague rules, Zuleika's count, all six Midas rounds, plant picking, the bag
refill, the Sun Armor set bonus and the evil orc helm.

### `patches/0015-dfi-hunting-bods.patch`

DFI's hunting bulk orders, from `Scripts/Custom/Engines/HuntingBods` and the
hunting branches DFI added to RunUO's bulk order deeds. The Hunter stands by
the fur traders in Britain and Trinsic (`dfi-hunters.json`), where DFI's hint
gump put him, sells hunting and bone-working goods, and gives contracts to
fighters with 60 in a fighting skill (archery, fencing, macing, swords,
magery, necromancy, taming or provocation).

- **Small contracts** ask for 10 to 20 kills of one creature. Target each
  corpse with the deed half a minute after the kill, if the corpse is yours
  to loot. Difficulty grows with skill: easy, medium, hard, very hard, from
  DFI's creature lists in `Data/Bulk Orders/Hunting`.
- **Large contracts** (80 skill) ask for a set, from orcs and ratmen up to
  Doom's creatures and wyrms, and take finished small contracts as crafting
  deeds do.
- **Rewards**: gold, fame and a bag of magic items, with carpets, banners,
  stretched hides, Powder of Temperament, pet skill balls or a treasure map;
  large contracts add the Necklace of Fortune, ModernUO's bone furniture,
  sometimes a power scroll, and at very hard one in ten the Warfork of Chaos,
  which levels on pixies, ki-rin and unicorns.
- A contract is offered an hour after the last, two at 80 skill and six at
  100, and right away once a contract is handed in.

DFI kept the level in the deed's material field; here it is its own field,
so the deed shows no material. Hunting contracts cannot go into a bulk order
book, which would hand them back as crafting deeds. `DfiHuntingBodTests.cs`
covers the decor, the creature lists, corpse targeting, rewards at every
level, large combining, the Hunter's offers and the warfork.

### `patches/0016-dfi-events.patch`

DFI's events, from `Scripts/Custom/Items/Containers/ItemChest`,
`Custom/Engines/EventRewardSystem`, `Custom/Items/Misc/LastManPentagram.cs`
and `Custom/Misc/CursedArtifactSystem.cs`. `[DfiEvents` places the pentagram
and the vendor; `[SpawnEIC` places the chests.

- **Treasure chests** (levels 1 to 7). `[SpawnEIC` turns each locked
  decorative chest in a dungeon into a treasure chest of the same look, its
  art setting the level. Picking one fills it with gold, reagents, scrolls,
  magic items and potions by level; level 7 adds the Cursed Cave's key and
  confusion blast potions. 20 to 40 minutes later it empties, locks and may
  trap itself again, and one time in ten calls a guard from the dungeon's
  own creatures.
- **Copper bars** are staff event prizes. `[CreateCopperBars` hands a game
  master up to 25 a day (administrators have no limit), logged with the
  event's name. The Event Reward Vendor by Britain's bank sells 36 rewards
  across game items, house decorations and fancy items, at DFI's prices:
  skill balls, temporary regeneration robes and cloaks, a 50-day ethereal
  horse, deeds (layered sash, personalisation, anti-bless, kill reset, name
  and sex change, pet and war horse bonding), fountains, statues, fires, a
  crystal pedestal, dyes, the magic sewing kit and the whispering rose.
- **The Last Man Pentagram** in Luna starts every day at 19:15 UTC. After a
  10-minute countdown, the first player to stand alone on it for 60 seconds
  wins. Hiding on it kills you, and everyone near it is attackable. The prize
  is a named statue, runic tools, ingots, gold, a minor or stealable
  artifact, scrolls, deeds or copper bars, and sometimes Barracoon. It stays
  cursed for 10 minutes and hues its carrier, who cannot bank it.
  `[CurseArtifact` does the same to any item.

The temporary items delete themselves when their days run out. DFI's
wedding deed, kill book and donation items are left out; the kill book's
share of pentagram prizes is copper bars. `DfiEventTests.cs` covers every
type, the addon deeds, the catalogue, buying, the daily limit, temporary
items, the sewing kit, the kill reset deed, chest locking and relocking,
loot at every level, every pentagram prize and the pentagram's controls.

### `patches/0017-dfi-creatures.patch`

From `Scripts/Custom/Mobiles/Monsters/SphynxFortuneGump.cs` and DFI's
`PlayerMobile` fortune code, `Custom/Opticon/Paladin AI`,
`Custom/Engines/Faction/Mobiles`, `Custom/Mobiles/Ethereal Mounts`,
`Custom/Items/Staff` and `Custom/Items/Misc/AncientMIB.cs`.

- **Sphynx fortunes.** Ask a sphynx for your fortune; for 5,000 gold you
  get one of DFI's twenty for a day: a resistance, luck, enhance potions,
  defend chance or mana regeneration, raised or lowered. Fortunes survive a
  restart. One call at the end of `AosAttributes.GetValue` adds the
  non-resistance ones. In Felucca a sphynx also drops a copper bar.
- **The Paladin** ("the Dark Knight") wears plate and the Holy Avenger, and
  fights with chivalry. It cleanses poison, closes wounds, lifts curses, and
  casts divine fury, enemy of one, consecrate weapon, holy light or dispel
  evil. One blow in ten deals a mortal wound, and pets and provoked creatures
  cannot hurt it. DFI's AI was a 936-line copy of RunUO's mage AI; this one
  is ModernUO's melee AI with the same spell choices.
- **Faction creatures**: a daemon (Shadowlords), an ogre lord (Minax), a
  silver serpent (True Britannians) and a wisp (Council of Mages), each
  peaceful to its own faction's players. Staff spawn them.
- **Items**: the ethereal skeletal steed; the ethereal war horse, which only
  faction members may ride; the counselor's staff, which only staff can
  wield; and the ancient message in a bottle and SOS.

`DfiCreatureTests.cs` covers every type, each fortune's attribute, the
fortune's resistance, its expiry, a reading, the Paladin's AI, the staff and
the ancient bottle.

### `patches/0018-dfi-town.patch`

DFI's townsfolk, stones and Riddler, from `Scripts/Custom/Mobiles/Townfolks and
Vendors/NPC`, `Custom/Items/Stones` and `Custom/Mobiles/Riddle Man`. Staff place
them; `[DfiTown` (0019) places the criminal banker in Buccaneer's Den.

- **Townsfolk**: the criminal banker, which does business with criminals (one
  `ServesCriminals` property on ModernUO's `Banker`); the priest of Mondain,
  who resurrects murderers and teaches the dark priest's skills; the
  necromancer and DFI's necromancer shop; the priest of evil, whose 1,000 gp
  resurrection (from the bank) also restores hits and mana; Haven's town
  crier, who walks young players through DFI's Haven; and the escortable
  prisoner noble and seeker.
- **Stones**: a gold withdrawal stone, a limited item stone (reagent bags or
  bandages), a limited reagent stone, mage and necro reagent withdraw stones
  (reagents from the bank, back as a bag), the pet bonding stone, a web stone
  and the weapon stone (five weapons, shields or bundles of ammunition per
  account when limited). Limits count per account until a restart, as DFI's
  did.
- **The Riddler** asks the first of up to ten riddles administrators set (talk
  to him as an administrator to edit them). The first correct answer wins the
  riddle's copper bars and a broadcast. He then vanishes for 20 to 30 hours
  and returns while riddles remain.

`DfiTownTests.cs` covers every type, the criminal banker, the necromancer's
shop, the full resurrection, each stone and the Riddler.

### `patches/0019-dfi-exchange.patch`

DFI's West Britain Exchange and vendor advertisements, from
`Scripts/Custom/Mobiles/Townfolks and Vendors/Vendor System` and `VendorAd`.
`[DfiTown` places the five exchange traders and an advertiser by Britain's
west bank.

- **The exchange** trades 42 commodities in five categories (blacksmith,
  magic, tailor, other crafts, archery), one trader each. A bid of at least
  25 units is paid from the bank up front, plus a 150 gp fee. An offer is a
  filled commodity deed of at least 25. Orders that meet trade at the bid's
  price, and unfilled orders lapse after five days. A cancelled or lapsed bid
  returns its money but not the fee; an offer returns its goods less 2.5%.
  Each trader shows the order book, the last trade and twenty days of
  figures, and a player's trades are shown at login.
- **Adverts**: a player vendor's owner pays 50 gp every 12 hours plus a
  price per use of their choosing (10 gp or more, half while online). Each
  account travels once per 12 hours on one payment, and higher prices rank
  higher. Advertisers announce a random advert every three minutes; near
  one, say "go", "show adds", "I wish to place a new advertisement" or "I
  wish to review my advertisements".

GUO's choices: what a trade buys, and the gold for what it sells, goes to the
bank (DFI put bought goods in the backpack, even an offline player's); DFI's
bar charts are figures; an order is edited by cancelling it and placing a new
one; an advert needs the vendor's owner (DFI had that check commented out).
`DfiExchangeTests.cs` covers every commodity, bids, offers, matching both
ways, cancelling, advert ranking, the per-account charge and the lapsing fee.

### `patches/0020-dfi-lost-lands.patch`

DFI's Lost Lands, from `Scripts/Custom/Items/Misc/LLTeleporter.cs`,
`Custom/Engines/LostLandsVendor`, `Custom/Mobiles/Townfolks and Vendors/NPC/
LLVarietyDealer`, `Custom/Mobiles/Monsters/LLDemonKnight.cs` and the Lost Lands
branches DFI added to RunUO's `BaseCreature`, `BaseChampion`, `Banker`,
`BaseVendor` and `PlayerMobile`. The Lost Lands are T2A Felucca and Khaldun.
`[DfiLostLands` places a gate on Britain's main street to Papua, one back
in Papua's bank, and Papun beside Papua's banker. The gates are red moongates.

- **The gate** takes only armor, weapons, jewelry, hats, spellbooks, runebooks
  and ethereals in (no clothing, no pets), and charges 1,000 gp from the bank
  per item either way, after a warning gump. On the way in it ends the
  insurance of everything carried and unblesses blessed hats.
- **Inside**: items cannot be insured; bankers will not open the bank box
  (balance, withdraw and deposit still work); shops charge ten times their
  price; a creature's gold becomes verite gems (one per 50, up to ten) and up
  to six magic items; champions leave a piece of the Armor of the Sun six
  times in ten.
- **Papun** sells verite gems for gold (200 gp each inside), buys them back
  for 50, and through his gump sells reagents, potions, bandages, ammunition,
  magic weapons and armor, pets, gold, a bulk order book, instruments and
  dragon ships for gems: 47 wares at three gems per 50 gp of DFI's price.
- **The Forgotten One** (`LLKnight`), a 25,000-hit dark knight staff spawn for
  events, leaves an artifact from a list of 37 (Doom's, the rare ones, DFI's
  quest artifacts) for certain in his first ten minutes, falling to 40% after
  an hour, else copper bars. He is announced every few minutes; in Khaldun he
  may vanish once the chance falls and nobody has hurt him.

DFI's saves kept no gate, so the Britain–Papua pair is GUO's choice. The gate
reads its direction from its target. `DfiLostLandsTests.cs` covers the zone,
prices, insurance, the gate's costs, refusals and passage, gems for gold, the
shop and the knight.

### `patches/0021-dfi-lottery-poker.patch`

DFI's lottery and video poker, from `Scripts/Custom/Engines/LotterySystem` and
`Custom/Engines/PokerSystem`. `[DfiGames` places a lottery master and the three
poker machines on Britain's main street, by the Lost Lands gate.

- **The lottery**: at a lottery master, pick four numbers from 1 to 49 per
  ticket, up to five tickets at 100 gp each from the bank. The drawing is
  every day at 17:00 UTC: 5,000 gp for two correct numbers, 30,000 for three,
  and the jackpot, 80% of the money in the system (never under a million),
  split between all four-number tickets. Winnings go to the bank and the
  report waits at the lottery master. Lottery masters advertise every minute.
- **Poker machines** (low 10-50 gp, medium 100-500, high 1,000-5,000 bets):
  five-card draw paid from the backpack or the bank. Hold any cards and draw
  once; a pair or better pays DFI's table times the bet, and a royal flush
  takes a third of the machine's gold. Winnings go to the bank, and machines
  under 50,000 gold refill every two hours.

GUO's choices: the poker hands are scored by one evaluator over DFI's nine
hands (DFI's checks tested sorted card positions); the drawing is at 17:00
UTC (DFI used the server's local time). `DfiGamesTests.cs` covers every
poker hand, dealing and holding, payouts, bets, buying, refunds and drawing
lottery tickets, and the jackpot split.

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
