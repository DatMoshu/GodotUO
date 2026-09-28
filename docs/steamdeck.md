# GUO on a Steam Deck

The Deck is an x86_64 Linux PC (SteamOS 3: Arch, glibc, KDE Plasma in
Desktop mode, gamescope in Game mode), so GUO runs on it as Godot's own Linux
export of the .NET project. Nothing is compiled on the Deck. This page is the
how-to; the reasoning is in
[ADR-0018](architecture/ADR-0018-steam-deck-target.md).

```
launchers\steamdeck\doctor.bat      what is missing here and on the Deck, with the fix
launchers\steamdeck\export.bat      the Linux build -> build\steamdeck\GUO.x86_64 + GUO.pck + data_*\
launchers\steamdeck\push.bat        copy it to the Deck over ssh, write guo.sh there
launchers\steamdeck\run.bat         start it in the Deck's Desktop-mode session
launchers\steamdeck\screenshot.bat  photograph the Deck's screen -> build\steamdeck\screenshot.png
launchers\steamdeck\smoke.bat       export + push + run + wait for the login gump + screenshot
```

Everything is `python tools\steamdeck\run.py <command>` underneath; the
extra commands (`templates`, `preset`, `stop`, `log`, `shortcut`) are
one-time or occasional steps and have no launcher.

## 1. Enable ssh on the Deck (once)

The tools talk to the Deck over ssh, as user `deck`. SteamOS ships sshd but
leaves it off and the `deck` user without a password.

1. Hold the power button > **Switch to Desktop**.
2. Open **Konsole** (application menu > System).
3. Give the user a password (the Deck asks for it when you `sudo`):
   ```
   passwd
   ```
4. Start sshd now and on every boot:
   ```
   sudo systemctl enable --now sshd
   ```
5. Note the Deck's address: **Settings > Internet** on the Deck, or
   `ip -4 addr show` in Konsole. Give it a DHCP reservation on your router
   so it stays put; a Deck on Wi-Fi and a Deck on a USB ethernet dongle get
   different addresses.

Both `systemctl enable` and the key survive SteamOS updates (they live in
`/etc/systemd` and `/home`, which the read-only system image does not touch),
but a factory reset removes them.

## 2. Make a key on the PC and install it (once)

In PowerShell or Git Bash on the PC:

```
ssh-keygen -t ed25519 -f ~/.ssh/guo_deck -N ""
type ~/.ssh/guo_deck.pub | ssh deck@<deck-address> "mkdir -p ~/.ssh && cat >> ~/.ssh/authorized_keys && chmod 700 ~/.ssh && chmod 600 ~/.ssh/authorized_keys"
```

(The second line asks for the password you set in step 1. On Git Bash use
`cat` instead of `type`.) Check it:

```
ssh -i ~/.ssh/guo_deck deck@<deck-address> uname -m
```

which must print `x86_64` without asking for anything. The tools run ssh in
`BatchMode`, so a key that still prompts fails fast instead of hanging a
launcher.

## 3. Tell the project about your Deck

These values are your network, so they go in **`launchers\_shared\config.local.bat`**
(gitignored; copy `config.local.bat.example` if you have none) and never in
`config.bat`:

```bat
REM --- my Steam Deck ---
if not defined UO_DECK_HOST         set "UO_DECK_HOST=<deck-address>"
if not defined UO_DECK_SSH_KEY      set "UO_DECK_SSH_KEY=~/.ssh/guo_deck"
REM optional: pin the Deck's host key in its own file instead of ~/.ssh/known_hosts
REM if not defined UO_DECK_KNOWN_HOSTS  set "UO_DECK_KNOWN_HOSTS=~/.ssh/known_hosts_deck"
REM optional: where the build and the UO data live on the Deck (defaults shown)
REM if not defined UO_DECK_INSTALL_DIR  set "UO_DECK_INSTALL_DIR=~/GUO"
REM if not defined UO_DECK_CLIENT_DATA  set "UO_DECK_CLIENT_DATA=~/UO"
REM the shard guo.sh connects to; 127.0.0.1 would be the Deck itself
REM if not defined UO_SHARD_HOST        set "UO_SHARD_HOST=<this PC's LAN address>"
REM optional: the shard account guo.sh passes as --account (empty = none)
REM if not defined UO_DECK_ACCOUNT      set "UO_DECK_ACCOUNT=<your account>"
```

`UO_DECK_USER` defaults to `deck` and needs no line. Every key is documented
in `config.bat`, and `tools\guo\config.py` reads the same values, so a tool
run from an IDE sees what a launcher sees. Then:

```
launchers\steamdeck\doctor.bat
```

which checks the PC (dotnet, the pinned Godot, the Linux export template,
ssh) and the Deck (reachable, x86_64, Desktop mode, a screenshot tool, the
client data) and says the fix for each miss.

## 4. Put the UO client data on the Deck

The client reads your own Ultima Online install: the folder holding the
`.uop` / `.mul` / `.idx` files, the same one `UO_CLIENT_DATA` points at on the
PC. It is proprietary and it is large (2 to 4 GB), so **the tools never copy
it**; you do, once, to `UO_DECK_CLIENT_DATA` (default `~/UO`, i.e.
`/home/deck/UO`):

- From the PC, over ssh (slow on Wi-Fi, fine on a cable):
  ```
  scp -i ~/.ssh/guo_deck -r "C:\Path\To\Ultima Online Classic" deck@<deck-address>:UO
  ```
- Or from Desktop mode on the Deck: Dolphin > Network, or a USB stick, or
  Warpinator, into `/home/deck/UO`.
- Or onto the SD card: `/run/media/deck/<card-name>/UO`, and set
  `UO_DECK_CLIENT_DATA` to that path. The card is mounted in both modes.

`doctor` looks for `tiledata.mul` there. Only the files the client reads are
needed; see `docs\data_formats.md`.

## 5. Build, push, run

```
launchers\steamdeck\export.bat
launchers\steamdeck\push.bat --host <the shard's address>
launchers\steamdeck\run.bat --wait 60
```

`export` renders `tools\steamdeck\export_presets.template.cfg` into the
gitignored `godot\GUO\export_presets.cfg` and runs Godot headless; the .NET
part is a `dotnet publish -r linux-x64 --self-contained` the exporter does
itself, so the PC needs no Linux toolchain. The result is
`build\steamdeck\GUO.x86_64`, `GUO.pck` and `data_GUO_linuxbsd_x86_64\`
(the .NET runtime and the game assemblies), together about 150 MB.

The Linux export template comes from the same
`Godot_v4.7.2-stable_mono_export_templates.tpz` the Android and Windows
builds use. If `doctor` says it is missing: put the archive in
`tools\godot\templates` (download it from the Godot 4.7.2-stable release
page) and run `python tools\steamdeck\run.py templates`.

`push` copies the three onto the Deck (rsync over ssh if the PC has rsync,
a tar stream over ssh otherwise; the Deck side needs nothing) into
`UO_DECK_INSTALL_DIR` and writes **`guo.sh`** next to them:

```sh
exec ./GUO.x86_64 -- --play --client-data "$HOME/UO" --host <shard> --port 2593 "$@"
```

Everything after `--` is the client's own command line; `guo.sh` appends
whatever you pass it (`./guo.sh --sound`, `./guo.sh --login-probe-stay`).
The shard address is `UO_SHARD_HOST`, or `push --host`. `127.0.0.1` would be
the Deck itself, which is not where your shard is; `doctor` and `push` warn.

`run` starts `guo.sh` inside the Deck's Desktop-mode session and returns,
with the client's output in `guo.log` next to it. It is started as a
transient unit of the Deck's user systemd (`systemd-run --user
--unit=guo-client`), not as a child of the ssh session: SteamOS kills
everything an ssh session started the moment that session closes
(`KillUserProcesses=True`), `nohup` or not, and the user manager already
knows the desktop's `DISPLAY`, `WAYLAND_DISPLAY` and `XAUTHORITY`. The panel
is woken first (`kscreen-doctor --dpms on`); a sleeping Deck draws nothing.
`--wait N` follows the log for N seconds. `python tools\steamdeck\run.py stop`
stops the unit, `... log` prints the log. You can equally double-click
`guo.sh` in Dolphin or run it from Konsole; then it is an ordinary process.

## 6. Game mode: add it to Steam

Game mode is gamescope, a different compositor with no `:0` for an ssh
session to reach, so there the client is started by Steam, once you have
told Steam about it. From Desktop mode:

```
python tools\steamdeck\run.py shortcut
```

writes `~/.local/share/applications/guo.desktop` on the Deck (GUO appears in
the Plasma application menu) and prints the steps, which are Steam's usual
ones:

1. Steam (Desktop mode) > **Games > Add a Non-Steam Game to My Library...**
2. **Browse...**, file type **All Files**, pick `/home/deck/GUO/guo.sh`,
   **Add Selected Programs**.
3. Right-click the new entry > **Properties**: name it `GUO`; **Launch
   options** may carry client flags (`--sound`); under **Controller** pick
   the *Keyboard (WASD) and Mouse* layout or the *Mouse and Keyboard
   (Trackpad)* template.
4. **Return to Gaming Mode**; GUO is under Library > Non-Steam.

Steam's `shortcuts.vdf` is a binary file that Steam rewrites on exit, so the
tool does not edit it. Re-pushing a build does not touch the shortcut.

## 7. Controls

A controller works in GUO on every platform, on by default (ADR-0025;
Options > "Use a controller" turns it off). The D-pad and left stick walk,
A confirms, B cancels, X opens the window menu, the right stick moves the
pointer, and the button glyphs follow the pad. How the Deck's built-in
controls reach the client depends on Steam Input, and that has **not been
measured on a Deck yet**. The controller check below (S5) measures it.

The client is also mouse-driven, exactly as on the PC: left-click to target
and pick up, right-click-and-hold to walk, double-click to use, drag to
move items, the keyboard to type. The Android touch layer (ADR-0017) is not
compiled for Linux. With the mouse on the Deck:

- **Right trackpad** as the mouse, **R2** left-click, **L2** right-click
  (the Steam default *Keyboard (WASD) and Mouse* layout does this; in
  Desktop mode the trackpads are already a mouse).
- **Walk**: right-click-and-hold on the world, i.e. hold L2 while moving the
  right trackpad. The client accepts a held right button and walks toward
  the pointer, so this works, if slowly; map L2 to a toggle if you prefer.
- **Touch screen**: a tap is a left-click at the touched point (Plasma and
  gamescope both send it as a mouse event). There is no way to right-click
  by touch without the layout; use L2.
- **Text**: Steam + X opens the on-screen keyboard in Game mode; in Desktop
  mode the Maliit keyboard pops up on a text field, or use a Bluetooth one.
- The window is the client's own integer scale, not Godot's stretch
  (ADR-0017 section 4); on the Deck's 1280x800 panel that is a 1x client
  with the classic 640x480 login gump centred.

## 7a. The controller check (S5)

This measures what the Deck's built-in pad looks like to GUO, the way
[docs/thor-controller-layout.md](thor-controller-layout.md) did for the Thor.
It needs the Deck awake, in Desktop mode first, with the client data on it.

1. Build and start it with the pad trace:

   ```
   python tools\steamdeck\run.py export
   python tools\steamdeck\run.py push
   python tools\steamdeck\run.py run --args="--gamepad-trace --login-probe-stay" --wait 90
   ```

   When the pad is seen, `guo.log` has one line for it:
   `[GUO] gamepad: device N connected: "<name>" guid <guid> known <True|False>, layout <Labels|Swapped|Unknown>, glyphs <family> (model "...", board "Valve Jupiter")`.
   Jupiter is the LCD Deck and Galileo the OLED.
2. Press, in this order: **A** (bottom), **B** (right), **X** (left),
   **Y** (top), the D-pad up, right, down, left, **L1**, **R1**, **L2**,
   **R2**, **View**, **Menu**, the left stick in a circle, then the right
   stick. Each press logs `[GUO] gamepad: device N "<name>" button <index> (<Godot name>) down`,
   and each stick past half-way logs an `axis` line.
3. Read them back: `python tools\steamdeck\run.py log`.
4. Do it again in Game mode, from the Non-Steam shortcut, with the
   shortcut's **Controller** layout set to Steam's *Gamepad* template. Under
   Steam Input the pad can arrive under another name (Steam's virtual pad)
   rather than as `Steam Deck`.

Record for each mode: the connect line, and for each printed label the
Godot button it gave. Then:

- **Printed A gives Godot A (button 0)**, and so on for B, X and Y: the
  layout is `Labels`. `GamepadInput.Detect` already answers that for any
  pad SDL knows, so nothing changes.
- **Printed A gives Godot B**: the layout is `Swapped`, and `Detect` needs
  the Deck's name (and the Valve board) as the Thor's Xbox mode has.
- **The glyphs are not `SteamDeck`** (for example, Steam's virtual pad reads
  as `Xbox` or `Generic`): `InputMode.FamilyOf` needs that name, but only
  on a Valve board, because on a PC the same virtual pad can be any
  controller.
- **No connect line in Desktop mode**: Steam's desktop configuration holds
  the pad as a mouse and keyboard, so GUO gets no pad there. Game mode is
  the one that counts; say so in section 7.

## 8. The smoke

```
launchers\steamdeck\smoke.bat [--host <shard>] [--timeout 180] [--no-export] [--no-push]
```

exports, pushes, starts `guo.sh --login-probe-stay --silent`, waits for
`[GUO] login probe: ok` in `guo.log`, photographs the screen with the gump
showing (`build\steamdeck\smoke.png`), saves the log
(`build\steamdeck\smoke_guo.log`) and stops the client. It is the record of
"GUO runs on the Deck": non-zero if the gump never appears or the client
dies, and it needs Desktop mode and the client data on the Deck.

## Troubleshooting

| Symptom | Cause / fix |
|---|---|
| `doctor`: Deck over ssh MISS, "Permission denied (publickey)" | the key is not in the Deck's `authorized_keys`, or `UO_DECK_SSH_KEY` names the wrong file; redo step 2 |
| `doctor`: Deck over ssh MISS, connection timed out | wrong address, sshd not running (step 1), or the Deck is asleep: press its power button once |
| `run` starts, `guo.log` says `No UO client data directory` | step 4: nothing at `UO_DECK_CLIENT_DATA`, or the path in `guo.sh` is stale: `push` again |
| `run` returns but nothing appears on the Deck | the Deck is in Game mode: switch to Desktop, or use the Steam shortcut (section 6) |
| `screenshot` fails with "no image written" | Game mode (spectacle needs Plasma), or the screen is locked: unlock the Deck. A merely sleeping panel is woken by the tool itself |
| `run` says "could not start the client" | `guo.sh` is not there (`push` first), or the Deck has no user systemd session: log in once on the Deck itself |
| The client says the shard is unreachable | `guo.sh` points at `127.0.0.1`; `push --host <the shard's LAN address>` |
| `export` fails in `dotnet publish` | run `dotnet build godot\GUO\GUO.csproj` first and fix what it says; the export log is `build\steamdeck\export.log` |
