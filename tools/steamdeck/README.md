# tools/steamdeck -- GUO on a Steam Deck

Exports the client as Godot's Linux x86_64 build, pushes it onto a Deck over
ssh, starts it in the Deck's Desktop-mode session, photographs the screen
and smoke-tests it. The how-to for a person setting up their own Deck
(enabling ssh, the key, `config.local.bat`, where the UO data goes, Steam
for Game mode, the controls) is [docs/steamdeck.md](../../docs/steamdeck.md);
the reasoning is [ADR-0018](../../docs/architecture/ADR-0018-steam-deck-target.md).

```
launchers\steamdeck\doctor.bat      what is missing here and on the Deck, with the fix
launchers\steamdeck\export.bat      build\steamdeck\GUO.x86_64 + GUO.pck + data_GUO_linuxbsd_x86_64\
launchers\steamdeck\push.bat        copy it to UO_DECK_INSTALL_DIR on the Deck, write guo.sh
launchers\steamdeck\run.bat         start guo.sh there (a user systemd unit; --wait N follows guo.log)
launchers\steamdeck\screenshot.bat  build\steamdeck\screenshot.png
launchers\steamdeck\smoke.bat       export + push + run --login-probe-stay + wait + screenshot
```

Everything is `python tools\steamdeck\run.py <command>`; `templates`,
`preset`, `stop`, `log` and `shortcut` have no launcher.

## Files

| | |
|---|---|
| `run.py` | the tool; reads `launchers\_shared\config.bat` through `tools\guo\config.py` |
| `export_presets.template.cfg` | the Linux preset, rendered into the gitignored `godot\GUO\export_presets.cfg` |

## What the Deck taught the tool

- SteamOS sets logind's `KillUserProcesses=True`: a process started from an
  ssh session dies with the session, `nohup` and `setsid` or not. The client
  is started with `systemd-run --user --unit=guo-client`, which the user
  manager owns and which already sees the desktop session's `DISPLAY`,
  `WAYLAND_DISPLAY` and `XAUTHORITY`.
- The panel sleeps within minutes; KWin will not photograph a sleeping
  output (`spectacle` returns 0 and writes nothing). `kscreen-doctor --dpms on`
  first, always.
- Without the session's `XAUTHORITY` Godot's X11 driver is refused and Godot
  falls back to Wayland on its own; the client runs either way.
- Git for Windows has no `rsync`; `push` streams a tar over ssh instead
  (172 MB in 4 s on a cable).
