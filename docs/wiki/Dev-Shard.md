# Dev Shard

The client needs a server to talk to. The dev shard is a local
[ModernUO](https://github.com/modernuo/ModernUO) instance running against
the same UO install the client reads, listening on `127.0.0.1:2593` as
**GUO Dev**: loopback only, so no other machine can reach it unless you open
it on purpose ([below](#opening-it-to-the-lan)). It is a development
dependency: nothing in `godot\GUO\` knows it exists. Source of truth:
`tools\modernuo\README.md`.

| | |
|---|---|
| **Pinned at** | `d4531cd94` (2026-09-30), `UO_SHARD_REF` in `config.bat` |
| **Runtime** | .NET 10 SDK (ModernUO's requirement, not the client's) |
| **Checkout** | `tools\modernuo\src\`, gitignored, about 1 GB built |
| **Licence** | GPL-3.0. Not vendored: fetched at the pin. |

## Use it

```bat
launchers\shard\fetch.bat      REM clone at the pin and apply the patches   (once)
launchers\shard\build.bat      REM publish release win-x64                  (once, ~2 min)
launchers\shard\run.bat        REM run it; Ctrl-C stops it
launchers\shard\populate.bat   REM generate the world                       (once, ~10 min)
```

Then, in another terminal, `launchers\game\play.bat`.

Auto account creation is on: the first login with any name and password
creates that account. `accountHandler.maxAccountsPerIP` is **4** in the
template rather than ModernUO's 1, because the scripted runs put several
clients on one machine. Put it back to 1 on a shard anyone else can reach.

## Populating the world

A new ModernUO save is the real map with nobody on it: no creatures, vendors,
signs or doors. What fills it in are in-game commands, so `populate.bat` logs
the owner account in **with the client itself** and types them: the list
AdminGump's "Do everything" button runs, in its order (`[GenerateSpawners`,
`[Decorate`, door, sign, moongate and teleporter generation, `[Save`).
Spawners and decorations alone are not a world: a town generated without
`DoorGen` has empty doorways, which is how that was found. The shard saves
the result; delete `Saves\` and it has to be run again.

## Accounts

| Account | Role |
|---|---|
| The owner (`UO_SHARD_OWNER`, `guoprobe` by default) | Made or raised to owner on a headless boot by patch 0001. `playtest.bat`, `populate.bat` and the `session` lane of `multi_client.bat` log in as it. |
| The trade partner | The second client `playtest.bat` starts to trade with; the reason `maxAccountsPerIP` is above 1. |
| `guoeffects`, `guohighlight`, `guosweep` (`UO_SHARD_GM_ACCOUNTS`) | Created with game master access on a headless boot, one per `multi_client.bat` lane, because the shard refuses a second character from one account and `[go` takes staff access. `world_parity` plays as the last of them. |

### Where the passwords are

There is no default password. The first `launchers\shard\run.bat` generates
two random ones, for the owner (`UO_SHARD_OWNER_PASSWORD`) and for the game
master accounts (`UO_SHARD_GM_PASSWORD`, shared by all three), and writes them
to a file of your own:

```
%LOCALAPPDATA%\GUO\shard\secrets.bat      (or <UO_WORKSPACE_DIR>\shard\secrets.bat)
```

It is outside the repository (and `secrets.bat` is gitignored besides), never
printed in full, and never copied into a run folder. Every launcher reads it
after `config.bat`, so `playtest.bat`, `populate.bat`, `multi_client.bat` and
the tools log in with it without being told; open the file when you want to log
in by hand. At every boot the shard sets the owner and game master accounts to
these passwords, so a world made before them stops taking the old ones.

- **New passwords:** delete the file and run `launchers\shard\run.bat` again.
- **Your own password:** set `UO_SHARD_OWNER_PASSWORD` (or
  `UO_SHARD_GM_PASSWORD`) in `config.local.bat`; that wins over the file.

## What is committed, and why

The checkout is gitignored. What is committed reproduces it:

| | |
|---|---|
| `patches\` | Three small dev-only changes: the owner and GM accounts made on a headless boot, a settable update range, and spring on Felucca. See `tools/modernuo/README.md`. |
| `config\modernuo.template.json` | The full server configuration with `@UO_CLIENT_DATA@`, `@UO_SHARD_NAME@`, `@UO_SHARD_LISTENER@` as placeholders; `expansion.json` pins Endless Journey, which a 7.0.x client expects. |
| `configure.py` | Fills the templates into `src\Distribution\Configuration\` on first run, through `tools\guo\config.py`. Writes only files that do not exist, except the listener, which it sets to `UO_SHARD_BIND` at every run. Generates the passwords. |

Bugs we find in ModernUO are reported upstream rather than patched for long:
our multi-tile fix became ModernUO's own (#2682, fixed in #2685), so the pin
moved past it and the patch was deleted.

## Opening it to the LAN

The shard binds to `127.0.0.1` (`UO_SHARD_BIND` in `config.bat`), so only
this PC can connect. To let a phone or another PC on your network in, do it
on purpose, in your own `launchers\_shared\config.local.bat`:

```bat
set "UO_SHARD_BIND=0.0.0.0"
```

(or this PC's LAN address alone), then restart `launchers\shard\run.bat`; it
says when the shard is reachable from other machines. Pass
`--host <this PC's address>` to the client on the device
(`tools\android\README.md`). Anyone on that network can then reach the login
screen, so keep the generated passwords rather than setting simple ones, and
look at `accountHandler.enableAutoAccountCreation` and `maxAccountsPerIP` in
the generated `modernuo.json`. Remove the line and restart to close it again.
Do not put an address in a committed file.

## The editor's private instance

The editor's live tier and the world export use a second, private ModernUO
instance on port 2594 (`tools\editor_shard`), so editing the map never
disturbs whoever is playing on the dev shard. See [Editor](Editor.md).
