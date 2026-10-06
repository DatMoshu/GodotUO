# Scripted Runs and Probes

The client can drive itself: log in, walk, open gumps, take a picture and
exit with a status, so a change can be checked without a person at the
window. Every flag below is parsed in `godot\GUO\src\Bootstrap\Main.cs`;
`launchers\game\play.bat` passes whatever follows it to the client, and
`launchers\dev\screenshot.bat` adds `--screenshot` and the output folder.

```bat
launchers\game\play.bat --play --silent --no-focus
launchers\dev\screenshot.bat --play --shot-after 300
```

## Session flags

| Flag | Meaning |
|---|---|
| `--play` | Auto-login: the login gump is filled in and submitted, the character picked, and the run continues in the world. Exports bake this in. |
| `--account NAME`, `--password PW`, `--character NAME` | Which account and character `--play` uses instead of the saved ones. `--character` matches an existing character; a lane makes its own on first use. |
| `--host HOST`, `--port PORT` | The shard, overriding `UO_SHARD_HOST` / `UO_SHARD_PORT` for this run. |
| `--offline` | No shard; load the data and draw the login screen. |
| `--client-data PATH`, `--client-version V`, `--cache-dir DIR` | Override the install, its version, and the client's home folder. Probes use `--cache-dir` to get a scratch home so the real `settings.json` and profiles are never edited. |
| `--language X` | The client's language. |
| `--stay` | Keep running when a probe would otherwise quit (as `--login-probe-stay` does for the login probe). |
| `--scratch-profile`, `--own-profile` | Run in a fresh client home of its own (`scratch/<pid>` under the usual one, with only `settings.json` copied in), so the profile, saved gumps and saved look start as a new player's and a run does not depend on the last. The touch probe does this by default; `--own-profile` keeps the usual home. |
| `--accounts-restart save\|check\|auto` | The saved-accounts store across an app restart: `save` saves a throwaway account with a random password, `check` (after the app is closed and opened again) reads it back and forgets it. `auto` picks by whether a save is waiting. Mainly for Android, whose Keystore key must outlive the process. |
| `--postfx NAME\|off` | The post-processing look for this run only (`off` is Classic); the saved look in `postfx/state.json` is neither read nor written. The perf, A/B and smoke tools pass `--postfx off`, and a device export that runs a probe gets it unless it names a look, so a look a player or a probe saved is not timed with the build. The perf report names the look it ran. |

## Sound and focus

| Flag | Meaning |
|---|---|
| `--silent` / `--sound` | Sound is off in every scripted run and in exports unless `--sound` is passed. |
| `--no-focus` / `--focus` | A scripted run creates its window without taking the keyboard from whoever is working at the machine (commit "Refuse to type unless ClassicUO has the foreground" is the ClassicUO-side rule of the same kind). `--focus` asks for the foreground. |
| `--window-position X,Y`, `--window-size W,H`, `--screen-scale N` | Place and size the window; `multi_client` tiles four with these. `--screen-scale` sets the client's integer scale. |

## Pictures

| Flag | Meaning |
|---|---|
| `--screenshot` | Screenshot mode: draw one fixed frame and save it. `screenshot.bat` sets this. |
| `--shot-after N` | Capture after N frames instead. The client spends its first frames loading, so a shot taken at once is a black window; ADR-0006's validation uses `--play --shot-after N`. |
| `--hide-gumps` | The world without the UI, for screenshots and films: every gump but the world view, the top bar, the command bar, the Modern views, the window menu, the pre-game card and the cursor are hidden; mobiles, items, overhead names and speech stay. Nothing is closed or moved. Ctrl+Shift+H toggles it in any run. |
| `--screenshot-dir DIR`, `--screenshot-name NAME` | Where and what to save. |
| `--background SPEC` | Override the canvas background for this run, never saved. See [Canvas Background](Canvas-Background.md). |
| `--shard-command "TEXT"` | Type a line into the chat once in the world, for example `[go 1602 1591`; the parity tools stand a client in a place with it. |

## Probes

A probe logs in, does one thing, checks it in the game's own state, prints
its result and exits 0 or 1. Each has a launcher or a lane; the ADR or commit
that introduced it names what it verified.

| Flag | Checks | Where it is run from |
|---|---|---|
| `--input-probe` | Warps the pointer onto the login gump, clicks, types, clicks Login, all through `Godot.Input.ParseInputEvent`. The shot has the typed text; the log has the login attempt. | `screenshot.bat --play --input-probe --shot-after N` (ADR-0006) |
| `--login-probe`, `--login-probe-stay` | Prints one line once the login gump has been drawn; the Android and web smokes wait for it on logcat or the console. | `launchers\android\smoke.bat` |
| `--touch-probe`, `--touch`, `--touch-trace` | Synthetic fingers through the touch layer on the desktop, in a scratch profile (`--own-profile` to use yours); `--touch` enables the layer, `--touch-trace` logs each gesture decision. | `launchers\dev\touch_probe.bat` |
| `--ui-probe` | Logs in, opens the backpack and a second container, logs the profile's platform fields, checks placement and how much of each other gump the containers cover, photographs it. | the `work/ui` commits |
| `--effects-probe N`, `--effects-plain` | Frame cost of a crowd of N blended effects; `--effects-plain` draws them without the blend. | `multi_client.bat` lane `effects` |
| `--pad-wheels-probe` | The pad's controller feel by injected joypad events, in a scratch home: the Set controls offer and B skipping it; LT's wheel opening the backpack and the paperdoll as full-screen controller panels, the tap reopening, the middle opening nothing, and every wheel window opening a full-screen panel; the Set controls card showing each action, its glyph and its binding with no description; RT's radar choosing a mobile (a horse is `[add`ed beside the character when none is near) with the right stick, X look, A use, RB/LB, Y menu, use on release, picking for a target cursor; R3 war mode, L3 always run, Start and the Options entry; the wizard cancelled by three inputs, then run with one job swapped and a slice changed, and the saved map taking effect; the Steam Deck trackpad leaving the mode on Gamepad. A picture per step into `--screenshot-dir`. | its commit |
| `--highlight-probe` | The mesh highlight check, which goes away and back with `[go`. | `multi_client.bat` lane `highlight` |
| `--door-probe` | Sends the open-door command and captures the door packets as they arrive, checking the door art changes. | its commit |
| `--batcher-probe` | 130 checks over the canvas batcher. Does not cover the frame split (ADR-0006 says so). | `launchers\dev\batcher_probe.bat` |
| `--zoom-probe` | 240-frame averages of prepare, draw and wall clock at each zoom; the numbers in ADR-0007. | its commit |
| `--art-sample` | Decodes a sample of art and shows it, with no client startup; the loader check from the first pixels. | its commit |
| `--warm-cache` | Builds the decode cache and exits. | `launchers\pipeline\02_warm_cache.bat` |
| `--endure N` | The playtest, then N more seconds of walking a square, comparing the last stretch with the first. | `launchers\dev\endurance.bat` |
| `--trade-partner` | Run as the second client the playtest trades with. | started by `playtest.bat` |
| `--dual-probe`, `--dual-screen WxH`, `--dual-off` | See [Dual Screen](Dual-Screen.md). | `launchers\android\dual_probe.bat` |

## The launchers that wrap them

| Launcher | Runs |
|---|---|
| `launchers\dev\playtest.bat` | One full session as the owner account: log in, make or pick a character, walk, open the backpack, move an item, open the gumps, speak. Exit 0 only when every check passed. Needs the shard. |
| `launchers\dev\endurance.bat [seconds]` | The same, then keeps playing; five minutes by default. |
| `launchers\dev\multi_client.bat` | Four lanes at once (`session`, `effects`, `highlight`, `sweep`), each on its own account and quarter of the screen; a log, a frame, a 2x2 contact sheet and `summary.md` under `build\multi_client\<stamp>\`. `--only a,b`, `--list`, `--sound`. |
| `launchers\dev\plugin_probe.bat` | A native and a managed test plugin loaded into a session; each must have been installed, told of the connection, seen packets and been given the player's position. |
| `launchers\dev\screenshot.bat` | Any of the above with a picture at the end, into `build\screenshots`. |

Every one of these needs `launchers\shard\run.bat` in another terminal
except `--offline`, `--warm-cache`, `--batcher-probe` and the renderer
probes.
