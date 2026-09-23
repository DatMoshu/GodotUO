# Porting ClassicUO to Godot — the plan

**Goal:** a complete Ultima Online Classic client running on Godot 4 .NET,
off FNA entirely, with behaviour matching the original.

**Approach:** transplant ClassicUO's C# rather than rewrite the game. The
network stack, file readers and game logic are kept; only the parts genuinely
bound to FNA are reimplemented.

Companion documents: `docs/port_status.md` (measured, regenerated),
`docs/data_formats.md` (the data contract).

---

## 1. Why a transplant, and not a rewrite

ClassicUO is roughly **155,000 lines across 429 C# files**. Rewriting that as
GDScript would mean reimplementing an MMO client by hand and permanently
losing the ability to take upstream bug fixes.

The measurement that decided the strategy: **FNA coupling is far shallower
than the file count suggests.**

| Upstream project | Files | Files touching XNA |
|---|---:|---:|
| `ClassicUO.Assets` | 23 | 0 |
| `ClassicUO.Bootstrap` | 4 | 0 |
| `ClassicUO.IO` | 13 | 2 |
| `ClassicUO.Utility` | 46 | 4 |
| `ClassicUO.Renderer` | 51 | 33 |
| `ClassicUO.Client` | 296 | 133 |

And of those 133 client files, **97 import only
`Microsoft.Xna.Framework`** — the maths and colour structs (`Point`,
`Color`, `Rectangle`, `Vector2/3`). Only 36 touch Graphics, Input or Audio.

Those structs have no engine behaviour behind them. Providing
API-compatible versions in a `GUO.Compat` namespace turns a third of the
codebase from "rewrite" into "change one `using` line".

---

## 2. The three tiers

| Tier | Files | Lines | Share | Treatment |
|---|---:|---:|---:|---|
| `verbatim` | 247 | 74,760 | 49% | Renamespace `ClassicUO.*` → `GUO.*` |
| `shim` | 116 | 56,510 | 37% | Renamespace + `using GUO.Compat;`, and `using SDL3;` → `using GUO.Platform.Sdl;` |
| `rewrite` | 37 | 21,959 | 14% | Reimplement on Godot |

Figures from `docs/port_status.md`, 2026-09-23. The rewrite count fell from
the first measurement as files found to carry no real FNA binding moved down
(see `SHIM_DESPITE_HEAVY_IMPORT` in `tools/port_audit/run.py`); the port
itself is read too, and a file whose port calls Godot's API directly is
`rewrite` whatever upstream imported (`GODOT_CALL`, same file).

The shim tier has two mechanical transforms, and only these two:

1. **XNA math.** Add `using GUO.Compat;`; the XNA value types resolve to the
   ones in §3.
2. **SDL values.** Replace `using SDL3;` with `using GUO.Platform.Sdl;`.
   Upstream reaches SDL in about thirty files, overwhelmingly for key and
   modifier enums (`SDL.SDL_Keycode.SDLK_a`). `src/Platform/Sdl` keeps the
   class name `SDL` so those call sites read unchanged, and provides:
   - `SdlKeys.cs` — `SDL_Keycode` and `SDL_Keymod`, verbatim from SDL3-CS;
   - `SdlEvents.cs` — `SDL_Event`, `SDL_KeyboardEvent`, `SDL_Scancode`,
     `SDL_EventType` and `SDLBool`, as value types the Godot input layer
     fills in (no event is ever read from native SDL);
   - `SdlPlatform.cs` — the clipboard trio (`SDL_HasClipboardText`,
     `SDL_GetClipboardText`, `SDL_SetClipboardText`) on Godot's
     `DisplayServer`, the only SDL behaviour bridged by signature.

   Everything else SDL does upstream — surfaces, windows, cursors — is real
   platform behaviour and is rewrite work in the area that owns it. A file
   that needs one of those is not a shim file.

**Four fifths of this port is mechanical.** That ratio is the reason the
project is tractable, and protecting it is the single most important
architectural constraint. The main way to lose it is by letting
`GUO.Compat` grow engine behaviour: the moment it holds a texture or a
device, the boundary stops being reviewable and the rewrite tier starts
expanding.

Tiers are measured, not assumed — `launchers\pipeline\03_port_audit.bat`
recomputes them from the actual source.

---

## 3. `GUO.Compat`

`godot/GUO/src/Compat` provides XNA-compatible value types:

| Type | Approach |
|---|---|
| `Point` | Own struct — Godot's `Vector2I` differs in members |
| `Rectangle` | Own struct — half-open semantics, `Contains`/`Intersects` |
| `Color` | Own struct — byte channels; Godot's is normalised floats |
| `Vector2` / `Vector3` / `Vector4` | Aliased to Godot's own; API already matches |
| `Matrix` | **Not shimmed** — 36 uses, all inside rewrite-tier files |

Conversions to engine types are explicit (`ToGodot()`), never implicit, so
the port/engine boundary stays visible in a diff. Full rules in that folder's
README.

`GUO.Platform.Sdl` (`src/Platform/Sdl`) is Compat's sibling for SDL, under the
same rule: values, plus the three clipboard calls, and nothing that needs
state or a lifecycle. §2 lists what it holds.

---

## 4. Order of work

Dependency order, not preference. Each phase is testable before the next
begins.

### Phase 0 — scaffold ✅ complete

Repo, pinned Godot 4.7.2 .NET, upstream reference, launchers, tooling,
`Compat` skeleton, audit baseline. `launchers\dev\smoke.bat` passes.

### Phase 1 — foundations ✅ complete
`Utility` → `IO`

Almost entirely verbatim. Ports the `.mul` / `.uop` readers, the memory-mapped
file access and the core collections. Keep the pointer arithmetic and the
memory mapping: they exist for throughput over hundreds of megabytes.

**Done:** all 56 files compile and run. A real 7.0.107.76 install opens and
every archive loads in ~1.1s.

### Phase 2 — asset loaders ✅ complete
`Assets`

Zero XNA imports upstream. The one real boundary is where a loader returns a
texture — that becomes Godot, and nothing above it changes.

**Done:** all 23 loaders compile and run; 175 art tiles decode correctly from
the real install, checked against a screenshot rather than a log line.

Two decompression bugs had to be fixed to get here, both marked
`PORT DEVIATION` in the source: upstream assumes a native `zlib.dll` that
Godot never puts on the search path, and its managed `ZLIBStream` validates
the Adler-32 trailer against the end of the whole stream, which is wrong for
the slices UOP hands it. Both now route through .NET's own `ZLibStream`.

### The staged build — retired

The port is a single assembly, so one unresolved type stops everything. The
mechanical tiers all landed at once (341 files), and for a while several areas
referenced rewrite-tier types that did not exist yet, so `GUO.csproj` carried a
`<Compile Remove>` list and the length of that list was the running total of
what was left.

**That ItemGroup is gone. Nothing is excluded; the whole client compiles.**

Two things the staged build taught, both now built into
`tools/port_errors/run.py` rather than remembered:

* Measure with `-t:Rebuild`. An incremental build can skip the compile and
  report a handful of errors from a partial pass, which reads like progress and
  is not: 9 errors incrementally against 1,600 on a rebuild, same tree, same
  day.
* A small error count can mean one missing type is hiding the rest. Roslyn does
  not bind method bodies once a declaration fails, so a single unported type
  used in a field or a parameter silences every method in the build — measured
  at 9 errors against 577 once two files referring to an unported gump were
  moved aside. The tool says so when the count drops below 50.

### Phase 3 — first pixels
`Render` (rewrite) + `Compat` hardening

The first genuinely hard phase, and the one with a binding architecture
decision: **`docs/architecture/ADR-0001-render-presenter-seam.md` (Accepted).
Read it before writing any code under `src/Render`.**

Rebuild sprite batching on `RenderingServer` or `MultiMesh`, atlases as
`ImageTexture`, and the hue system as a shader sampling a LUT built from
`hues.mul`.

**Classic only.** We ship exactly one presenter, `ClassicPresenter`, at strict
pixel parity with the original client. A modern or alternate renderer is
explicitly **not** in scope — not now, not as a stub. What Phase 3 owes the
future is the *seam*, not a second implementation:

- `RenderLists` + `SceneSorter` stay presenter-agnostic (no Godot rendering
  types) so an alternate presenter is an additive change later.
- `IScenePresenter` is defined and implemented once.
- No presenter-selection config, no registry, no `ModernPresenter` placeholder.

Three constraints that are easy to get wrong and expensive to fix later:

- **Never filter pixel art.** The project pins `default_texture_filter=0`;
  any new viewport or material must preserve it.
- **Draw order is gameplay.** In an isometric world, sort order decides what
  the player can see and click. Port upstream's rules verbatim rather than
  substituting a generic depth sort.
- **One depth function.** Draw order and mouse picking must both read
  `CalculateDepthZ()`. Upstream already shares it; if a presenter recomputes
  depth, the player clicks something other than what they see.

**Done when:** a static scene of terrain and statics renders correctly at
pixel parity with a reference capture, evidenced by a screenshot, and
`grep -rn "Godot\." src/Render/Scene/` is empty.

**Status:** met, by screenshot. The batcher, atlases, hues, blend states and
fonts are in and measured (`launchers\dev\batcher_probe.bat`, 130 checks), and
New Haven draws at 3840x2054 — terrain, statics, multis, mobiles, and the
lighting that comes with nightfall — one client pixel per screen pixel, with
the camera following the player. Decisions: ADR-0001 through ADR-0004.

### Phase 4 — the world
`Game/Data`, `Game/GameObjects`, `Game/Map`

Largely verbatim and shim. Terrain, statics, multis, mobiles.

**Done when:** a facet loads and displays, and the camera moves over it.

**Status:** met. Trammel loads around New Haven and the camera follows the
player walking through it.

### Phase 5 — the shard
`Network`, `Configuration`

Engine-agnostic and ports cleanly, but unforgiving: the wire format is a
contract with real server software. Port packet definitions exactly —
lengths, endianness, fixed-width strings, seed and version negotiation,
compression, encryption variants.

**Done when:** login completes, the character list arrives, and the world
loads from a live shard.

**Status:** met, against a live shard. `launchers\shard\run.bat` runs a local
ModernUO (`tools/modernuo/README.md`); the client logs in, gets the shard list,
creates a character, enters the world and walks — which is a move request, an
accept and a position update, so the wire works in both directions and under
the server's own throttling. Speech goes out and comes back through the
server, and so does a container: the probe picks an item up out of the
backpack and drops it somewhere else in it, and the server's answer is what
moves it. Using a skill raises the target cursor, and the target the probe
picks is answered by the server in the journal.

The dev shard's world is generated now (`launchers\shard\populate.bat`, 5,612
spawners), so the client is finally talking to a shard with people on it: the
probe hovers a townsperson, hits them with the renderer's own hit test, and
the journal fills with the names the server sends back for the mobiles on
screen.

Three more round trips are exercised on top of that, each one chosen because
only the server can produce the evidence:

- **Wearing.** Taking what is in the hand off into the backpack and putting a
  weapon on, both as drags. What comes back is the layer the character is
  drawn wearing.
- **Combat.** War mode and a double-click on a lawful target. The shard
  answers 0xAA with the target it accepted and the client then asks for that
  mobile's status, so a target that comes back with hit points is the round
  trip and not an echo of the click.
- **Shopping.** Walking to a shopkeeper with the client's own pathfinder,
  "vendor buy", and a purchase off the shop gump. The thing bought arrives in
  the backpack as a new item the client did not have.
- **Trade.** An item handed to another player. The probe starts a second copy
  of the client on its own account (`--trade-partner`), which logs in, writes
  down where it ended up and accepts what it is offered; the probe goes to it,
  drops an item on it, and both sides tick their box. The item leaves the
  backpack because the server moved it.

- **The window.** Dragged smaller and back, with "always use fullsize game
  window" on, to see the world follow it. Upstream resizes from an SDL event
  and this port from Godot's viewport notification, so the wire between them
  is worth pulling.

Every path the client has is now exercised. Plugins have their own check:
`launchers\dev\plugin_probe.bat` builds a native test plugin and a managed,
Razor-shaped one, plays the same session with both listed, and reads back
what each saw — lifecycle, packets both ways, and the player's position.

`launchers\dev\endurance.bat` runs that same session and then keeps playing,
walking a square so that land, statics and mobiles are loaded and dropped the
whole time. It compares the last stretch with the first, because what goes
wrong over a session goes wrong slowly: a cache that only grows, a texture
freed on one path and not another, a draw list that is rebuilt but never
emptied. Five minutes of it -- 18,000 frames -- held 16.69 ms a frame at the
start and 16.70 ms at the end, with 106 fewer objects and 3.3 MB more memory in
hand. That is the first measurement the port has of a client that has been
played rather than one that has just started.

Two things the trade step had to learn, both about aim and both worth keeping
in mind for anything else that points at the world:

- What is under the cursor is worked out while the world is drawn, so asking
  less than about four frames after moving the mouse reads the answer to the
  previous question.
- Two characters standing next to each other overlap, and which of them owns
  a given pixel changes as they breathe. An offer is therefore lifted first
  and aimed afterwards -- a drag that decides where to let go before it picks
  anything up lets go a second later, and by then the answer has moved.

Twenty minutes of it -- 72,000 frames -- said the same thing, and finished
with less memory in hand than it started with.

A check has to be made of something that always answers, too. Three of these
failed on a client that had done nothing wrong: a book dropped a fixed forty
pixels from where it was picked up landed in a bag and the server correctly
put it in the bag; a target cursor raised by Arms Lore, which wants an item,
was clicked on a person; and a context menu was asked of a townsperson, whom
the server has nothing to offer about. They now drop on bare container,
prefer a skill that asks about people, and ask a shopkeeper -- who always has
a menu -- for the menu.

And a check has to make the thing happen before it reads it. The resize check
found a client that would not follow its window, and what it had actually
found was a maximised window quietly ignoring a request to be smaller: the
window manager owns that size, not the client. It goes windowed first now.
Two overlapping characters are the same kind of problem from the other end --
an offer aimed correctly can be released a frame later at a pixel that has
changed hands, and a refused drop is silent -- so the offer gets three goes,
which the run that proved the fix needed. A window is worth losing on
purpose, too: the client once came up where no monitor covered it, with no
title bar to drag and a taskbar icon that does nothing, and getting it back
took SetWindowPos from another process. The probe now pushes it to
-30000,-30000 and watches it come back.

And what no check caught at all was reported by eye, from a screenshot: house
furniture painted over the roof it stands under, and trees over the houses
behind them. The cause was a piece of upstream read too quickly. ClassicUO
keeps four render lists and walks them one after another, and it is tempting
to read that walk as the draw order. It is not: `GameScene.DrawWorld` turns a
depth buffer on first, and what paints over what is then settled per pixel by
`CalculateDepthZ()`, the very number the lists carry but never sort on.
Submission order upstream means nothing. The same is true of the UI, which
has two gump queues flushed one after the other and an ever-climbing
`layerDepth` to make the split harmless -- harmless there, and on a canvas
with no depth test it put journal text across a world map and a status gump
inside a map's own frame.

Godot's 2D canvas has no depth buffer, so the sort has to happen before the
batcher sees anything. It is exact rather than approximate because UO art is
cut out -- a pixel is opaque or it is absent -- so ordering whole sprites
answers as ordering pixels would. The cost was measured, not guessed: 16.66
ms a frame before and after. The lesson is the general one, and it is rule 6
pointed at the renderer -- an FNA call that configures the device is part of
the behaviour being ported, as much as the code around it. A device state
this port cannot reproduce is a rewrite, not a detail.

### Phase 6 — interface
`Game/UI` (117 files, ~51k lines), `Input`

The largest single area. Mostly shim tier, but gump rendering depends on
Phase 3.

**Done when:** core gumps — paperdoll, backpack, status, skills — work.

**Status:** met. `Input` is done — `src/Input/GodotInput.cs` replaces
upstream's SDL event filter (ADR-0006), and clicks, typed text and a held
right button all reach the scene. The input probe opens the core gumps by
finding their buttons and clicking them, and they are all up at the end of a
run:

    gumps open: TopBarGump, MiniMapGump, StatusGumpModern, PaperDollGump,
                ContainerGump, StandardSkillsGump, WorldMapGump,
                WorldViewportGump

The backpack comes from the server with its contents and draws each item from
its own art; the paperdoll shows what the character is wearing; the skills
gump draws its scroll, its groups and its caps. The two maps are in the list
because they are the only gumps that draw a map rather than the world: the
minimap builds a texture out of `MultiMap.mul`, the world map decodes a PNG
and draws it itself.

Past clicking: the probe drags an item from one place in the backpack to
another, which is a press, motion with the button held, a pick-up onto the
cursor and a drop the server has to accept. And it double-clicks the
character in the world — not a gump — which means the renderer's hit test
against the drawn sprites answered correctly, since the paperdoll it opens is
one the probe closed first. The same hit test finds other people: a
townsperson walking past is picked out by asking the client what is under the
cursor.

The skills gump is worth its own line, because it is the one that needed all
three: it opens under the paperdoll that opened it, its groups start
collapsed, and its rows are laid out whether or not they are drawn. Moving it
clear, expanding a group and pressing a skill's own use button is what a
player does, and it ends with a target cursor.

Two things learned from dropping items, both about aim rather than protocol.
A drop onto another item is a different gesture from a drop onto the
container it sits in -- the client asks the server to put the one into the
other, and a refusal is silent, so the probe finds bare container first. And
the paperdoll only wears onto a layer that is empty, which makes equipping
two drags and not one.

The shop gump is the newest of these, and the longest chain in the client
outside combat: pathfinder walk, speech the server has to parse as a keyword,
a gump built from the server's list, a double-click to pick a line off the
shelf and an Accept that sends the purchase.

### Phase 7 — parity and polish
`Audio`, lighting, effects, and the long tail.

**Done when:** a full play session is indistinguishable from the original
client.

**Status:** where the work now is. `Network/Plugin.cs` is ported, so native
plugins load through their `Install` export and managed .NET Framework ones
(Razor, Razor Enhanced, ClassicAssist) through the ported bootstrap in
`tools/plugin_host`, which `src/Network/PluginClrHost.cs` starts inside the
client's own process. cuoapi is not empty: upstream ships it as a binary in
`external/cuoapi`, and both projects reference it there. The 26
`Renderer/Batching` commands a plugin draws through are waived — they are a
command buffer for an FNA `GraphicsDevice` that this client does not have —
and `Plugin.HandleCmdList` drops such a list with one warning. So is
`XBREffect`, which nothing upstream constructs.

Plugins are not needed to play, so from here parity is measured by playing
rather than by the audit — see rule 6. What the probe measures today, in the
world with eight gumps open at 3840x2054, in a generated New Haven with
townspeople and animals moving in it: 59.7 fps, 21.78 ms worst frame,
footstep audio playing (`feet12b.wav`), and no leaked RIDs at exit.

---

## 5. Working rules

1. **Never edit `sources/`.** It is read-only reference.
2. **Port faithfully.** No reformatting, renaming, modernising or opportunistic
   bug fixing. Every gratuitous edit must be reconciled by hand on every
   future upstream merge. Note bugs; do not fix them mid-port.
3. **Build in small batches** (`launchers\dev\build.bat`). One file's error is
   cheap; fifty files' errors are not.
4. **Re-measure after each batch.** The audit is the only honest progress
   report.
5. **Parity before improvement.** Get it identical, then propose changes
   separately.
6. **"Ported" ≠ "working".** The audit matches filenames. Claims of working
   behaviour need a smoke test or a screenshot.

---

## 6. Upstream drift

ClassicUO is actively developed. `launchers\dev\sync_upstream.bat` updates the
reference and flags commits touching **already-ported** files — the ones that
rot silently. Assess, port across, then re-pin with `--pin`.

---

## 7. Licensing

- **ClassicUO** — BSD 2-Clause. Ported files keep their upstream copyright
  header; the licence and originating commit are recorded in
  `docs/upstream/`.
- **Claude Code Game Studios** — MIT; provenance in `docs/upstream/`.
- **UO client data** — proprietary, never redistributed. Users supply their
  own installation, read in place and never committed.
