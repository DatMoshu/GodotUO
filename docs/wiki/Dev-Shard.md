# Dev Shard

The client needs a server to talk to. The dev shard is a local
[ModernUO](https://github.com/modernuo/ModernUO) instance running against
the same UO install the client reads, listening on `127.0.0.1:2593` as
**GUO Dev**. It is a development dependency: nothing in `godot\GUO\` knows it
exists. Source of truth: `tools\modernuo\README.md`.

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
| The owner (`UO_SHARD_OWNER` / `UO_SHARD_OWNER_PASSWORD` in `config.bat`) | Raised to owner on a headless boot by patch 0001. `playtest.bat`, `populate.bat` and the `session` lane of `multi_client.bat` log in as it. Change both values on any shard others can reach. |
| The trade partner | The second client `playtest.bat` starts to trade with; the reason `maxAccountsPerIP` is above 1. |
| `guoeffects`, `guohighlight`, `guosweep` (`UO_SHARD_GM_ACCOUNTS`) | Created with game master access on a headless boot, one per `multi_client.bat` lane, because the shard refuses a second character from one account and `[go` takes staff access. `world_parity` plays as the last of them. |

Passwords are not documented here; the headless boot's rule for them is in
`tools\modernuo\README.md`, and they are for a shard on loopback only.

## What is committed, and why

The checkout is gitignored. What is committed reproduces it:

| | |
|---|---|
| `patches\` | Three small dev-only changes: the owner and GM accounts made on a headless boot, a settable update range, and spring on Felucca. See `tools/modernuo/README.md`. |
| `config\modernuo.template.json` | The full server configuration with `@UO_CLIENT_DATA@`, `@UO_SHARD_NAME@`, `@UO_SHARD_PORT@` as placeholders; `expansion.json` pins Endless Journey, which a 7.0.x client expects. |
| `configure.py` | Fills the templates into `src\Distribution\Configuration\` on first run, through `tools\guo\config.py`. Writes only files that do not exist. |

Bugs we find in ModernUO are reported upstream rather than patched for long:
our multi-tile fix became ModernUO's own (#2682, fixed in #2685), so the pin
moved past it and the patch was deleted.

## Reaching it from a device

The shard binds to loopback by default. For a phone on your LAN, run it
bound to an address the phone can reach (`tools\android\README.md` describes
the setting) and pass `--host <that address>` to the client. Do not put that
address in a committed file; it belongs in `config.local.bat` or on the
command line.

## The editor's private instance

The editor's live tier and the world export use a second, private ModernUO
instance on port 2594 (`tools\editor_shard`), so editing the map never
disturbs whoever is playing on the dev shard. See [Editor](Editor.md).
