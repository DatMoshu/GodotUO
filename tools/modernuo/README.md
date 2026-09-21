# ModernUO — the local dev shard

The client needs a server to talk to. This is it: a local
[ModernUO](https://github.com/modernuo/ModernUO) shard, running against the
same UO install the client reads, listening on `127.0.0.1:2593` as **GUO Dev**.

It is a development dependency, not part of the port. Nothing in
`godot/GUO/` knows it exists; the client connects to `UO_SHARD_HOST` /
`UO_SHARD_PORT` and does not care what answers.

| | |
|---|---|
| **Upstream** | <https://github.com/modernuo/ModernUO.git> |
| **Licence** | GPL-2.0 (RunUO lineage). Not vendored, not redistributed. |
| **Pinned at** | `24bcfee55` — `0.15.6.178-12-g24bcfee55`, fetched 2026-09-21 |
| **Runtime** | .NET 10 SDK (ModernUO's own requirement, not the client's) |
| **Checkout** | `tools/modernuo/src/` — **gitignored**, ~1 GB with the build |

## Use it

```
launchers\shard\fetch.bat     clone + apply the patches   (once)
launchers\shard\build.bat     publish release win x64     (once, ~2 min)
launchers\shard\run.bat       run it                      (Ctrl-C to stop)
launchers\shard\populate.bat  generate the world          (once, ~10 min)
```

Then, in another terminal, `launchers\game\play.bat`.

Auto account creation is on, so the first login with any name and password
makes that account. The input probe (`launchers\dev\playtest.bat`) uses
`guoprobe` / `guoprobe`, which is also `UO_SHARD_OWNER`, and `guomate` for the
second client it starts to trade with.

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
config/     the server configuration, as templates
configure.py  fills the templates in from config.bat
```

### `patches/0001-headless-owner-account.patch`

`AccountPrompt.Initialize()` insists on an owner account at first boot and
asks for it at the console. `Core.Headless` is true whenever stdin is
redirected, which is every scripted run, and the prompt throws there instead
of asking.

The patch replaces the prompt, when headless, with the account named by
`UO_SHARD_OWNER` / `UO_SHARD_OWNER_PASSWORD`: created if it does not exist,
raised to owner if it does — which it usually does, because auto account
creation made it a player at the first login. ModernUO takes its
administration commands in game and not at the console, so without an owner
account the world cannot be generated at all.

Keep this list append-only and numbered. A patch that upstream adopts should
be deleted, not silently dropped from the set.

### `config/`

`modernuo.template.json` is the full server configuration with three values
left as placeholders — `@UO_CLIENT_DATA@`, `@UO_SHARD_NAME@`,
`@UO_SHARD_PORT@` — and `expansion.json` pins the expansion to **Endless
Journey** (Id 11), which is what a 7.0.x client expects.

`configure.py` writes both into `src/Distribution/Configuration/` on first run,
resolving the placeholders through `tools/guo/config.py` — the same
environment → `config.bat` → shared-config order everything else here uses.

It writes only files that do not exist. Edit the generated file to change a
setting on this machine; edit the template to change it for everyone, and say
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

`UO_SHARD_UPDATE_RANGE` in `launchers\_shared\config.bat` sets it; 72 covers
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
