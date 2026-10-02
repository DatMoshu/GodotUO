# Pregame 3D — a pad-first, PSX-style 3D front end

Status: in progress (Spekks / Brett); first working build on branch `pregame-3d` (see "Implementation" below). Replaces the classic 2D pregame gumps
(login, server select, character select, character creation) with one 3D
diorama a controller can drive end to end. The classic gumps stay; the 3D
front end is opt-in by a setting until it is proven.

## Look: PSX retro

- Rendered into a `SubViewport` at **320x240-ish internal resolution** (keep
  the window's aspect: 1280x800 → 400x250), shown upscaled with
  nearest-neighbour filtering. The rest of GUO's no-filter rule applies.
- Materials: one shared PSX spatial shader. Vertex snapping to the internal
  resolution grid (the wobble), affine texture mapping, nearest-filtered
  textures at 64–256 px, vertex lighting feel, 15-bit colour + ordered
  dither in a post pass. Optional fog towards the back wall.
- Lighting: warm, candle/torch-lit, one key light plus flicker. No shadows
  beyond a baked/blob look.

## Layout: the classic login screen, alive

Same composition as UO's login screen (640x480 reference):

| Object | Role | Behaviour |
|---|---|---|
| Stone wall backdrop | set | static; torch light flickers on it |
| Red velvet cloth, draped under the chest | set | slow subtle cloth sway (vertex shader) |
| Chest (body + hinged lid) | the frame of every step | lid creaks open on boot; camera moves into/around it between steps |
| Tufted red velvet chest interior | set | |
| Stone oval plaque inside the chest | login form | carries "Account name" / "Password" fields as 3D text |
| Login button (gold-trimmed plaque) | action | presses in, thunk |
| Green heater shield, left | **Quit** | lifts + glows on focus, thumps when pressed |
| Credits plaque, right | Credits | |
| Checkbox studs on the chest front | Autologin / Save account / Music | toggle |
| Candles / wall torch | light | flicker |

Every interactive object is a **hotspot**: on pad focus it lifts slightly,
rim-glows gold (`#e0b050`) and shows the pad glyph for A. A presses it
(short down-up animation + sound). The pointer still works: mouse hover =
focus, click = press.

## Steps (same diorama, camera moves)

1. **Login** — plaque fields + Login, Quit shield, Credits, option studs.
   Saved accounts (AccountBook) appear as wax-sealed cards/scrolls to pick
   with one press; picking one fills the fields.
2. **Connecting / loading** — lid half-closes / a candle gutters; status text.
3. **Server select** — servers as scrolls laid in the chest (name, ping
   dot). D-pad moves, A picks.
4. **Character select** — up to N character slots as framed portraits/
   plinths in the chest; the selected character is shown as its UO
   animated sprite on a billboard (faithful, and very PSX). A = play,
   Y = delete (confirm), "New" slot = create.
5. **Character creation** — stations in the same space: profession (cards),
   appearance (cycle body/hair/beard with left/right, hue palettes),
   stats/skills (dials/sliders), starting city (map table). Name via the
   on-screen keyboard.

## Input

- D-pad / left stick: move focus along an explicit neighbour graph per step
  (authored, not guessed). A: press. B: back (`LoginScene.StepBack`) or close
  the keyboard. Start: confirm/Login. Right stick keeps the pointer.
- Keyboard + mouse still work; hot-swap per `InputMode`.
- **On-screen keyboard** (ours; Linux/Deck has no OS keyboard GUO can call):
  a PSX-styled 3D/overlay grid, D-pad to move, A types, X backspace, Y shift,
  Start done, password fields show dots.

## Code shape

- New, GUO-native code under `src/Pregame3D/` (no ported files beyond one
  marked hook): `Pregame3D` (owns the SubViewport + camera rig + step state),
  `Hotspot` (focusable 3D object: lift/glow/press, neighbours), `PadFocus`
  (routes pad D-pad/A/B into the focus graph while the pregame is up, the way
  `WindowMenu.Navigate` does), `OnScreenKeyboard`, one stage class per step.
- Drives the real login through `LoginScene` (`Connect`, `SelectServer`,
  `SelectCharacter`, `StartCharCreation`, `CreateCharacter`,
  `DeleteCharacter`, `StepBack`, `CurrentLoginStep`, `Servers`,
  `Characters`, `Cities`) and `AccountBook`/`ServerBook`, never by faking
  clicks on the gumps.
- Hook: `LoginScene.GetGumpForStep` returns no gump for the steps the 3D
  front end owns when it is enabled — marked `PORT DEVIATION (GUO)`.
- Assets: `assets/pregame3d/` — glb models + small PNG textures, all
  original (authored for GUO in Blender, or from the team PSX library). No
  EA art, no textures sampled from the client data.

## Implementation (2026-10-02)

### Turning it on

| How | Effect |
|---|---|
| `--pregame-3d` / `--pregame-classic` | this run only |
| `pregame3d.json` beside settings.json: `{"enabled": true}` | remembered |
| default | on in an exported Linux build (the Deck build), off elsewhere, so desktop dev runs and every existing probe keep the classic gumps |

`--pregame3d-probe` turns it on and drives the whole login by synthetic pad
events (see Verification).

### Code (`src/Pregame3D/`, all GUO-native)

| File | Job |
|---|---|
| `PregameDiorama.cs` | the Pregame3D node: SubViewport + upscale + post pass, overlay layer, step switching on `CurrentLoginStep`, input routing, camera framing, UI-anchored objects, 3D text |
| `DioramaScene.cs` / `DioramaLayout.cs` | loads `layout.json` + the glbs (runtime glTF fallback when not yet imported), placeholder primitive per missing node (one log line), PSX materials, lights + flicker, lid |
| `Hotspot.cs`, `PadFocus.cs`, `Overlay.cs` | focusable 3D objects (lift, gold rim, press, ray pick), the explicit neighbour graph + repeat, UO-styled overlay rows |
| `OnScreenKeyboard.cs` | the field card; with no device keyboard, our grid: D-pad, A type, X delete, Y shift, Start done, B cancel; physical keys type too; passwords as `*` |
| `NativeKeyboard.cs` | the device's keyboard: Steam's (`steam://open/keyboard` / `close/keyboard`) on the Deck or under Steam, the OS one on Android; Deck and Game Mode detection |
| `LoginStage` / `StatusStage` / `ServerStage` / `CharacterStage` / `CreationStage` | one per step; drive LoginScene's own calls only |
| `Pregame3DSettings.cs`, `Pregame3DProbe.cs` | the switch; the probe |

Shaders: `assets/pregame3d/shaders/` — `psx.gdshader` and `psx_cloth.gdshader`
share `psx_common.gdshaderinc` (vertex snap to the internal grid, affine UVs,
nearest sampling, vertex lighting, rim glow; the cloth adds a sway);
`psx_post.gdshader` is the 15-bit + 4x4 Bayer pass.

### Screen

Internal resolution = window / whole-number scale, about 400 lines
(1280x800 → 640x400 x2, 1920x1080 → 640x360 x3, 2560x1440 → 640x360 x4,
3840x2160 → 768x432 x5), re-evaluated on resize; vertex snapping follows it.
Wide windows keep the layout's vertical FOV; narrower ones widen it until the
step's content (chest + plaque + Login; the scrolls; the plinths) fits across.
`ui_anchors` objects (Quit shield, Credits plaque) are placed every frame
parallel to the camera's image plane, in their corner. 3D text is a Label3D in
the client's font at a whole number of font pixels per internal pixel.

### Hooks in other files (each marked)

- `Game/Scenes/LoginScene.cs` — `PORT DEVIATION (GUO)`: `GetGumpForStep`
  returns null when the 3D pregame owns the step; `Load` and
  `UpdateCharacterList` skip their direct gump adds; `Update` tolerates a null gump;
  `Load` leaves the window alone (no 640x480, no restore, no minimum size) and
  calls `PregameDiorama.PrepareWindow`, which goes fullscreen in an exported
  Linux (Deck) build. Everything (internal resolution, FOV fit, overlay scale,
  ui_anchors, 3D label pixel sizes) is laid out again on every window size
  change; the window size wins when the root viewport lags it (gamescope).
- `Client/GameController.cs` — `PORT DEVIATION (GUO)`: keys and pointer buttons
  go to `PregameDiorama.HandleMainInput` while it is up.
- `Input/Gamepad/GamepadInput.cs` — D-pad, left stick, A/B/X/Y, Start, shoulders
  go to `PregameDiorama.HandlePad` while it is up (right stick keeps the pointer;
  an unresolved layout's face buttons still go to GamepadInput).
- `Bootstrap/Main.cs` — the three flags are accepted (read by Pregame3DSettings).

### Controls

| Step | A | B | X | Y | Start |
|---|---|---|---|---|---|
| Login | press / type in a field | – | edit account | credits | Login |
| Status | OK (message) | cancel / OK | | | |
| Servers | choose | back | | | choose (LB/RB page) |
| Characters | play / new on an empty plinth | back | new | delete (confirm) | play |
| Creation | edit / choose | page back (out from Appearance) | | | next / create |

Keyboard: arrows, Enter = A, Escape = B, Tab, Ctrl+Enter = Start; typing on a
focused field opens the keyboard with that letter. Mouse: hover focuses, left
click presses, right click = B.

### Verification

`--pregame3d-probe` (under `xvfb-run`, with the dev shard): types the account
and password on the on-screen keyboard, logs in, picks the server, opens
creation (changes gender/hues, names the character, picks a profession,
reaches the cities) and backs out, plays the character, checks it is in the
world and that the diorama freed itself; screenshots per step into
`--screenshot-dir`. Exit 0 = pass.

### Not done yet

- No UO sprite of the character on the plinth (stand-in plinth + name).
- Creation is functional, not pretty: overlay lists, no mannequin, hues shown as numbers.
- Server ping only where the shard's address answers ICMP; no server-book editor.
- Sound hook (`Hotspot.PressSound`) is unset.

### Steam Deck (2026-10-02)

- Detection: env `SteamDeck=1`, or `/sys/class/dmi/id/board_name` /
  `product_name` "Jupiter" or "Galileo". Game Mode: `SteamGamepadUI=1`,
  `XDG_CURRENT_DESKTOP=gamescope` or `GAMESCOPE_WAYLAND_DISPLAY`. Under Steam:
  either, or `SteamAppId` / `SteamClientLaunch`, or a running `steam` process.
- Window: on the Deck never Fullscreen (gamescope gives a 1920x1080 canvas
  scaled onto the panel); a borderless 1280x800 window at 0,0.
- Text fields: Steam's keyboard under Steam (typing real keys; the field card
  moves to the top, "press Start when done"); Android's own; our grid only
  without either. Scripted runs (`--pregame3d-probe`, no-focus runs) always use
  the grid; `GUO_NATIVE_KEYBOARD=0` forces it, `=dry` takes the Steam path
  with the URLs only logged. The log says which: `pregame3d: keyboard = ...`.
- `ui_anchors` boxes are clamped inside the viewport whatever the margins.
