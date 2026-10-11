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

## Dry run

Every command takes `--dry-run`, and every launcher passes it through
(`launchers\steamdeck\smoke.bat --dry-run`). It prints each command line it
would run -- the Godot export, every `ssh`, `scp` and `rsync`, the tar
streams, the `systemd-run` start, the screenshot, `guo.sh` as it would be
written, smoke's whole sequence in order -- and each file it would write or
delete, and does none of it: no export, no ssh connection, no file written.
Commands are quoted as a POSIX shell takes them.

It needs no Deck and no configuration. With `UO_DECK_HOST` unset the address
shows as `<UO_DECK_HOST>` and the run says so; an unset key or known-hosts
file is said plainly too (ssh's defaults apply). Missing local pieces (the
Godot console, the export template, an earlier export) are reported where a
real run would stop, and the dry run carries on to show the rest.

```
python tools\steamdeck\run.py smoke --dry-run --host <the shard's address>
python -m pytest tools\steamdeck     # the tests; also in launchers\dev\pytest_all.bat
```

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
