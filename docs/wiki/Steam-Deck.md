# Steam Deck

The Deck is an x86_64 Linux PC (SteamOS 3), so GUO runs on it as Godot's own
Linux export of the .NET project. Nothing is compiled on the Deck: the PC
exports, copies the build over ssh and starts it. The decision and its
evidence are in ADR-0018 (Accepted); the full how-to, with every command and
the troubleshooting table, is `docs/steamdeck.md`. This page is the short
version.

```bat
launchers\steamdeck\doctor.bat      REM what is missing here and on the Deck, with the fix
launchers\steamdeck\export.bat      REM the Linux build -> build\steamdeck\GUO.x86_64 + GUO.pck + data_*\
launchers\steamdeck\push.bat        REM copy it to the Deck over ssh, write guo.sh there
launchers\steamdeck\run.bat         REM start it in the Deck's Desktop-mode session
launchers\steamdeck\screenshot.bat  REM photograph the Deck's screen
launchers\steamdeck\smoke.bat       REM export + push + run + wait for the login gump + screenshot
```

Underneath: `python tools\steamdeck\run.py <command>`, plus `templates`,
`preset`, `stop`, `log` and `shortcut`, which have no launcher.

Prefer not to export it yourself? The release workflow builds the same thing
on GitHub: see [Download a build](Getting-Started.md#download-a-build).

## Setup (once)

1. **ssh on the Deck.** Desktop mode, Konsole: `passwd`, then
   `sudo systemctl enable --now sshd`. Note the Deck's address and give it a
   DHCP reservation so it stays put.
2. **A key on the PC**, installed in the Deck's `~/.ssh/authorized_keys`
   (`ssh-keygen -t ed25519 -f ~/.ssh/guo_deck`). The tools run ssh in batch
   mode, so a key that still prompts fails fast instead of hanging.
3. **Your values in `launchers\_shared\config.local.bat`**, never in
   `config.bat` (they are your network):

   ```bat
   if not defined UO_DECK_HOST     set "UO_DECK_HOST=<deck-address>"
   if not defined UO_DECK_SSH_KEY  set "UO_DECK_SSH_KEY=~/.ssh/guo_deck"
   if not defined UO_SHARD_HOST    set "UO_SHARD_HOST=<this PC's LAN address>"
   REM optional
   REM if not defined UO_DECK_CLIENT_DATA set "UO_DECK_CLIENT_DATA=~/UO"
   REM if not defined UO_DECK_ACCOUNT     set "UO_DECK_ACCOUNT=<your account>"
   ```

   `UO_DECK_ACCOUNT`, when set, is passed to the client as `--account`.
   Every key is documented in `config.bat`; see [Configuration](Configuration.md).
4. `launchers\steamdeck\doctor.bat` checks both ends and names the fix for
   each miss.

## Your UO data on the Deck

The client reads your own Ultima Online install, the folder with the
`.uop` / `.mul` / `.idx` files. It is proprietary and 2 to 4 GB, so **the
tools never copy it**: you put it at `UO_DECK_CLIENT_DATA` yourself, once
(default `~/UO`, that is `/home/deck/UO`):

- over ssh from the PC: `scp -r "<your UO folder>" deck@<deck-address>:UO`
  (fine on a cable, slow on Wi-Fi);
- from Desktop mode: a USB stick, Dolphin's network view or Warpinator;
- or on the SD card, `/run/media/deck/<card-name>/UO`, with
  `UO_DECK_CLIENT_DATA` set to that path. The card is mounted in both modes.

`doctor` looks for `tiledata.mul` there.

## Build, push, run

```bat
launchers\steamdeck\export.bat
launchers\steamdeck\push.bat --host <the shard's address>
launchers\steamdeck\run.bat --wait 60
```

`push` writes `guo.sh` next to the build:

```sh
exec ./GUO.x86_64 -- --play --client-data "$HOME/UO" --host <shard> --port 2593 "$@"
```

Anything you pass `guo.sh` goes to the client (`./guo.sh --sound`). Never
leave the host at `127.0.0.1`: on the Deck that is the Deck itself, not your
shard; `doctor` and `push` warn about it.

## The smoke

```bat
launchers\steamdeck\smoke.bat [--host <shard>] [--timeout 180] [--no-export] [--no-push]
```

Exports, pushes, starts the client with the login probe, waits for the login
gump, photographs the screen (`build\steamdeck\smoke.png`), keeps the log
(`build\steamdeck\smoke_guo.log`) and stops the client. Non-zero if the gump
never shows or the client dies. It needs Desktop mode and the client data on
the Deck. This is the record behind ADR-0018's Accepted status.

## Game mode: a Steam shortcut

Game mode runs gamescope, which an ssh session cannot draw into, so there the
client has to be started by Steam. From Desktop mode:

```bat
python tools\steamdeck\run.py shortcut
```

puts GUO in the Plasma application menu and prints Steam's usual steps:
**Games > Add a Non-Steam Game**, browse to `/home/deck/GUO/guo.sh` (file
type All Files), name it GUO, optionally add client flags as launch options,
and pick the *Keyboard (WASD) and Mouse* controller layout. Back in Gaming
Mode it is under Library > Non-Steam. The tool never edits Steam's
`shortcuts.vdf`, and re-pushing a build does not touch the shortcut.

Notes for Game mode:

- With the *Keyboard (WASD) and Mouse* layout the client is mouse-driven,
  as on the PC: right trackpad is the mouse, R2 left-click, L2 right-click;
  hold L2 and steer with the trackpad to walk.
- GUO does have a gamepad layer (`src/Input/Gamepad`, checked by
  `--gamepad-probe`): D-pad or left stick walks, A clicks at the pointer, B
  cancels, the right stick moves the pointer. Steam's keyboard-and-mouse
  layout hands the client keys and a mouse instead, so the layer stays idle.
  A gamepad layout has **not been tried on the Deck** yet; the Thor is the
  only device it has been run on.
- A tap on the touch screen is a left-click. There is no touch right-click
  without the controller layout.
- Steam + X opens the on-screen keyboard.
- `run` and `screenshot` only work in Desktop mode.

## Known: the 640x480 login window

The pre-game screens use upstream ClassicUO's fixed 640x480 window, so on
the Deck's 1280x800 panel the login gump sits in a small window at 1x (the
smoke log reports `window 640x480`), and the canvas background is not seen
behind it. The phone build centres these screens full-screen instead, but
doing the same on the Deck or the desktop would depart from upstream: it is
an open decision for the project owner, not a bug to fix in passing.
