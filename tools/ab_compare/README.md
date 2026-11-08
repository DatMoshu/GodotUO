# ab_compare — the original next to the port

`launchers\dev\ab_compare.bat`

Stands ClassicUO and GUO in the same places on the same dev shard and takes a
picture with each, so a rendering difference can be looked at rather than
argued about. Output is `build\screenshots\ab\<place>\`: `cuo.png`, `guo.png`
and `ab.png`, the two stacked and labelled.

## What it controls for

Both clients read **the same profile** — the CUO one is a copy of the profile
GUO saved for the same character, so roofs, fading, terrain shadows, the
game-window size and the open gumps all match. Both log into the same shard
as the same character and are sent to the same coordinates with `[go`.

Both passes pin the shard to full daylight with `[globallight 0` first.
Without it the shard's clock runs on between them and one client gets a duller
world than the other, which reads as a difference in the renderer and is not
one. `LightCycle.LevelOverride` outranks the clock and holds until the shard
restarts.

Both frames are drawn at one pixel per pixel of the window. ClassicUO does not
do that by default: `GameController.Draw` renders the whole frame -- world and
gumps -- to a target of back buffer over `DpiScale` and blows it up again, so
on a display scaled to 150% it draws at two thirds of the window's real
resolution. That is right for a person, whose UI stays legible, and wrong for
a comparison, where it looks like ClassicUO is zoomed in and soft. `DpiScale`
is the display scale times the `screen_scale` setting, so the run writes
`screen_scale` as one over the display scale and the two cancel. Godot is DPI
aware already and needs nothing.

What it still does not control for: the season, and the mobiles, which wander.

## How each client is driven

GUO already has `--play`, `--shard-command` and `--screenshot-name`, so its
pass is one process per place and touches no windows.

ClassicUO has none of that, so it is driven the way a person would: `-autologin`
with the owner account from `config.bat`, `-skiploginscreen`, then the place
typed into the chat line as `[go X Y` and the window photographed off the
screen. **That pass needs the desktop to itself** — it brings the window to the
front and types into it, with the same keyboard whoever is at the desktop is
using. It is therefore **off unless `--allow-foreground` is passed**: the
default `--only both`, and `--only cuo`, stop with a message before anything
starts (exit 2), and `focus()` / `send_keys()` refuse when called any other way.
`--only guo` and `--only compose` never touch a window and need no flag.

Around each `SendKeys` call Caps Lock is read and put back: SendKeys toggles
it to type upper case and a send that is cut short leaves it toggled, which
looked like Caps Lock changing on its own. The Alt tap that lifts Windows'
foreground lock is sent as a down/up pair that cannot be split, and chat lines
are escaped so `+ ^ % ~` in them are typed rather than read as Shift, Ctrl,
Alt and Enter. The output folder is only opened in Explorer when the
foreground was allowed, since an Explorer window takes it too.

## One login per client

`--one-session` is the fast way, and it does not touch the desktop. `ShotDump`
(`godot/GUO/src/Bootstrap/ShotDump.cs`) is compiled into GUO and into the
ClassicUO this tool builds; `one_session.py` hands it one list of shard commands
and shots through `GUO_SHOT_DUMP`, so each client logs in once, walks every
place, and saves its own screenshots. Gumps are hidden for the run. Windows are
kept off the monitors as `tools/visual_ab` does.

Logging in is what costs the time: a client relaunched per place spends most of
each minute starting up. Results land in the same `<place>\cuo.png` and
`guo.png`, and the sheets are composed the same way.

## Clients nothing can be compiled into

`tour.py` photographs any client binary, such as a build from someone else. The
dev shard does the driving (patch `0026-dev-photo-tour`): started with
`GUO_PHOTO_TOUR` set to a manifest path, it waits for the listed characters to be
online, moves them from stop to stop, sets the light and writes a stamp at each
one. `tour.py` writes that manifest, launches the clients and copies each window
with `PrintWindow` on every stamp, so nothing types into a client and no window
needs focus. Several clients can run at once on different characters and see the
same world at the same moment. One login kicks another, so each client needs its
own account.

```
set GUO_PHOTO_TOUR=<folder>\tour.json        (before launchers\shard\run.bat)
python tools\ab_compare\tour.py --manifest <folder> --character NAME --out <folder> --client "NAME|WORKING FOLDER|COMMAND LINE"
```

The client logs itself in, by whatever settings or arguments it takes.

## Tests

```
python -m pytest -q tools/ab_compare
```

Covers the step lists, the spec, collecting shots and the tour manifest. The
clients themselves need a shard and are checked by running the tool.

## Building ClassicUO

Built on first use, or with `--build`, into `build\cuo\`. Out of tree on
purpose: `ClassicUO.Client.csproj` hard-codes an `OutputPath` inside
`sources\`, and every project would drop an `obj\` there as well, so the build
passes `--artifacts-path` and an explicit `OutputPath`. `sources\` is read-only
reference and stays pristine — `git status` in it should show nothing after a
run.

The client that gets built is `cuo.exe`, not `ClassicUO.exe`.
`ClassicUO.Bootstrap` is the net472 plugin host that launches it, and there are
no plugins here.

## Options

| | |
|---|---|
| `--only guo\|cuo\|both\|compose` | which half to run; `compose` only redraws the sheets |
| `--place <name>` | one place, repeatable |
| `--character <name>` | the character on the owner account (default `Guoprobe`) |
| `--build` | rebuild ClassicUO first |
| `--login-wait`, `--settle` | seconds to wait for autologin, and after each `[go` |
| `--no-open` | do not open the output folder at the end |
| `--allow-foreground` | let the ClassicUO pass take the foreground and type; off by default |
| `--one-session` | log each client in once and shoot every place in that session; nothing typed, no focus |
| `--size W,H`, `--zoom Z`, `--session-timeout` | `--one-session`: window size, the same camera zoom on both (default 1; 0 keeps each profile's), and seconds allowed per client |
| `--guo-best` | `--one-session`: GUO with `LightingSpeedMode` on and lamp shadows at maximum |
| `--guo-set PROP=VALUE` | `--one-session`: a GUO profile property for this run only, repeatable |
| `--guo-day-hour`, `--guo-night-hour` | `--one-session`: the Labs test clock; needs `dotnet build -p:GuoLabs=true` |
| `--guo-cache-dir` | `--one-session`: run GUO from this home instead of the shared one |

The places are five in `run.py`, the same five `launchers\dev\sweep.bat` uses:
a town in daylight, a forest with buildings in it, a coastline, a dungeon mouth
and a street among houses with roofs.
