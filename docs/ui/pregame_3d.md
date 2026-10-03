# Pregame 3D: a pad-first front end over the classic login painting

Author: Speeko (Spekks), PR #16. Status: working, opt-in off the Linux build
(see "Turning it on"). It replaces the classic 2D pregame gumps (login, server
list, character list, character creation) with one screen a controller can drive
end to end, and drives the real login through `LoginScene`. The classic gumps stay
and are what every desktop run and every existing probe use.

## What it is

The classic login screen, alive. The client's own login painting (gump `0x014E` on
7.0.64+ clients, the older login art before that) is read at runtime from the
player's own install, xBR-style upscaled, its stone wall mirrored out to fill a wide
screen, lit by a flickering warm light and drifting slowly. The client's own gump
pieces (fields, arrow, Quit, Credits, the three boxes) stand over it where the
classic gump puts them, and a 2D overlay carries the cards, the hint band and the
on-screen keyboard. There is no 3D scene, no model and no engine-made art: every
picture is read from the install, and nothing is packaged.

Pixel art is never filtered. The painting shader samples with `filter_nearest`, the
layers and the gump pieces set `TextureFilter = Nearest`, and the overlay scales by a
whole number.

## Turning it on

| How | Effect |
|---|---|
| `--pregame-3d` / `--pregame-classic` | this run only |
| `pregame3d.json` beside settings.json: `{"enabled": true}` | remembered |
| default | on in an exported Linux build (the Steam Deck build), off everywhere else |

`--pregame3d-probe` turns it on and drives the whole login (see "Verification").

## Code (`src/Pregame3D/`, GUO-native)

| File | Job |
|---|---|
| `PregameScreen.cs` | the node: layers, the painting, step switching on `CurrentLoginStep`, input routing, the modal cards, the window (`PrepareWindow`) |
| `Painting.cs`, `PregameAssets.cs` | composes the painting from the client's gumps; loads every gump the pregame draws on a worker thread (and the figure's frames after login) |
| `GumpProp.cs`, `Overlay.cs` | a client gump piece with its states, glow and dim; the overlay's cards, rows and text in the client's font |
| `PadFocus.cs` | the explicit neighbour graph, held-direction repeat, the `PadCmd` set |
| `Stage.cs` and `LoginStage` / `StatusStage` / `ServerStage` / `CharacterStage` / `CreationStage` (+ `CreationSteps`) | one per step; each drives only `LoginScene`'s own calls (`Connect`, `SelectServer`, `SelectCharacter`, `CreateCharacter`, `DeleteCharacter`, `StepBack`) |
| `Mannequin.cs`, `WorldMapImage.cs` | the creation figure from the player's animation art; the Home map drawn from the player's map files |
| `OnScreenKeyboard.cs`, `NativeKeyboard.cs` | our grid keyboard; the device's (Steam's, Android's) when there is one |
| `Pregame3DSettings.cs`, `Pregame3DProbe.cs` | the switch; the probe |

Shaders in `assets/pregame/shaders/`: `painting.gdshader` (the painting: wall mirror,
light, drift, dim), `gump.gdshader` (the pieces: xBR-style upscale, dim, a gold glow
for the pad's focus) and `xbr.gdshaderinc`. The xBR include is a reduced,
single-pass corner blend, not Hyllian's xBR: it takes the edge rule (weights 48/7/6,
threshold 15, the wd1/wd2 edge weights) from Hyllian's xBR, which is MIT licensed
(`docs/upstream/XBR-HYLLIAN-MIT.md`).

## Hooks in other files (each marked `PORT DEVIATION (GUO)`)

- `Game/Scenes/LoginScene.cs`: no classic gump for a step the pregame owns
  (`PregameScreen.Owns`, which also brings the screen up); the window is left alone
  and `PregameScreen.PrepareWindow` is called instead of the 640x480.
- `Client/GameController.cs`: keys and pointer buttons go to
  `PregameScreen.HandleMainInput` while it is up.
- `Input/Gamepad/GamepadInput.cs`: D-pad, left stick, A/B/X/Y, Start and the shoulders
  go to `PregameScreen.HandlePad` while it is up (the right stick keeps the pointer).
- `Bootstrap/Main.cs`: the three flags are accepted (read by `Pregame3DSettings`).

## Steps and controls

| Step | A | B | X | Y | Start | Other |
|---|---|---|---|---|---|---|
| Login | press / type in a field | | edit account | credits | Login | LB: Servers |
| Status | OK (message) | cancel / OK | | | | |
| Servers (the shard's list) | choose | back | | | choose | |
| Characters | play / new on an empty plinth | back | new | delete (confirm) | play | |
| Creation | edit / choose | back one step | | random | next / create | LB/RB: step |

Keyboard: arrows, Enter = A, Escape = B, Tab, Ctrl+Enter = Start; typing on a focused
field opens the keyboard with that letter. Mouse: hover focuses, left click presses,
right click = B.

### Servers (the shard list, review fix)

The login step has a **Servers** entry in its top-left corner, in the pad's focus
graph (Quit, then up to Servers, then right to the account field) and on **LB**. It
opens the same card the classic login screen's Servers button opens
(`PregameCard.OpenOnMain`), over the pregame: the player's servers, favourites and
community list, `ServerPlay.Check` (a shard that only allows its own client; one that
needs its own client files, asked as the restart question; one that names content
packs, asked as the install question), and the shard's data folder or content lock
(`ShardSession`). The card is Godot controls in their own viewport, so while it is
open the pad's commands reach them as `ui_up/down/left/right/accept` actions (A
presses), and B answers "no" to a question or closes the card. Play on a server runs
`ServerPlay.Play` as the classic card does; with no login gump it hands the choice to
`LoginStage.PlayOn`, which closes the card, lists the new server's saved accounts and,
for a saved account chosen on the card, logs in as the classic path does. A restart for
a shard's files or packs works as on the classic screen. Typing into the card's own text
fields (Add server, Add account) still needs a keyboard; the pregame's grid keyboard does
not serve them yet.

### Character creation: the Tailor's Table

Five steps under tabs (`1 Trade 2 Look 3 Skills 4 Home 5 Name`; Skills only for
Advanced). The character stands on the left, live, from the player's own animation art;
the right stick turns it. Trade: the profession cards, Y at random. Look: body, race,
skin, hair, beard, shirt, pants, colours from the hue data. Skills: Str/Dex/Int with a
fixed total and the gump's skill slots. Home: a map drawn at runtime from the map files,
a pin per city. Name: validation as the gump's, the keyboard, Enter Britannia, which
calls `LoginScene.CreateCharacter`.

## Steam Deck

- Detection: env `SteamDeck=1`, or `/sys/class/dmi/id/board_name` / `product_name`
  "Jupiter" or "Galileo". Game Mode: `SteamGamepadUI=1`, `XDG_CURRENT_DESKTOP=gamescope`
  or `GAMESCOPE_WAYLAND_DISPLAY`. Under Steam: either, or `SteamAppId` /
  `SteamClientLaunch`, or a running `steam` process.
- Window: on the Deck a borderless 1280x800 window at 0,0 (gamescope's fullscreen is a
  1920x1080 canvas scaled onto the panel); an exported Linux build elsewhere goes
  fullscreen; a desktop run keeps its window. The layout follows the OS window when the
  root viewport lags it.
- Text fields: Steam's keyboard (`steam steam://open/keyboard`) only when a steam process
  is already running (it never starts Steam), else our grid; Android's own; scripted runs
  always use the grid. `GUO_NATIVE_KEYBOARD=0` forces the grid, `=dry` takes the Steam
  path with the URLs only logged.

## Verification

`--pregame3d-probe` drives the whole login with synthetic pad events through the real
input path, with a screenshot per step into `--screenshot-dir`. It checks that every
visible control is inside the screen at each size, that no flat stand-in frame is
showing, types the account and password on the keyboard, reaches the Servers entry by
the D-pad, opens the card, picks the dev shard and plays (back on the login step with
the server set), asks a files-needing server's restart question and answers it with B,
logs in, goes through creation on a fresh account (or plays the existing character with
`--account`), and checks the pregame freed itself in the world. Exit 0 = pass. It needs a
shard (use a private one, `tools/editor_shard`). Not checked there: a device (the Deck)
itself; the content-pack install is covered by the classic `--pregame-probe` with
`UO_PROBE_SHARD_CONTENT`.

## Not done

- No sound hook; the card's text fields have no pad keyboard.
- Resizing the window by the user is left as the contributor wrote it
  (`AllowUserResizing`), and `PregameScreen.Owns` creates the screen as a side effect
  of its name; both are noted for the author.
