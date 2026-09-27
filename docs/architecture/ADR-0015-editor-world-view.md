# ADR-0015: The Editor's World View Runs the Game's Renderer, Embedded

## Status

Accepted

## Date

2026-09-27

## Last Verified

2026-09-27: `python tools\editor_smoke\run.py` and `--headless --reload` pass.
The UO World tab, reached from the Maps panel, boots the embedded world in
1.2-1.7 s. It draws east Britain at map0 1496,1628 through `GameScene.Draw`:
8,017-10,425 objects windowed, and a 2004x1804 frame with more than 4,096
colours. Clicking picks the land cell through the game's own `PixelPicker`.
A multi placed through the server's object path draws, and is removed through
the delete path. An assembly reload with the world and a world project open
unloads cleanly, with zero engine errors in the log.

## Decision Makers

Project owner (Moshu); GUO-Fable (plan author); GUOEditor session.

## Summary

The editor's UO World tab (docs/editor_plan.md §4.4) draws the world with
the client's own draw path. A second `GameController` is built in an
**embedded** mode: it is never added to the scene tree, owns no window, and
starts no audio, socket, plugins or login. The editor sets it as
`Client.Game`, builds a `World` and a `GameScene` around a stand-in player,
and calls the scene's `Draw` into a `SubViewport` of the editor's main
screen. `GameScene`, the sorter, the views and the batcher are untouched.
Two ported files carry one `PORT DEVIATION (GUO)` block each.

## Engine Compatibility

| | |
|---|---|
| Engine | Godot 4.7.2 stable, mono |
| APIs | `EditorPlugin._HasMainScreen` / `_MakeVisible` / `_GetPluginName`, `EditorInterface.GetEditorMainScreen`, `SetMainScreenEditor`, `SubViewport` with `CanvasItemDefaultTextureFilter.Nearest` |
| Relies on | ADR-0002 (the batcher draws into any canvas item's RID) and ADR-0006 (`GameController` is a node whose loop is driven from outside) |

## ADR Dependencies

- Builds on ADR-0001 (one presenter), ADR-0002, ADR-0004 (chunk meshes),
  ADR-0006 and ADR-0010 (the addon's shape and reload rule).
- ADR-0011 (world project overlay) feeds this view's map.
- The live tier (ADR-0012) will feed `WorldHost.PlaceServerMulti` /
  `RemoveServerObject` from the shard.

## Context

### Problem Statement

The plan requires that "what you see in the editor is what the client shows".
That rules out a second renderer. The game's renderer, though, is reached
through `Client.Game` and assumes it owns the window, a network session and a
logged-in player.

### What was measured (a survey of the draw path, then running it)

- `GameScene.Draw` needs `Client.Game.UO` (the renderer's asset classes),
  `World` with a `Map`, `World.Player` (the draw is gated on
  `World.InGame => Player != null && Map != null`, the camera centres on the
  player, and roofs are measured against its Z), `ProfileManager.CurrentProfile`
  and `Settings.GlobalSettings`. It does not need a socket or a login.
- `GameController`'s constructor changes the OS window (vsync, title, mouse
  mode). `_Ready` starts audio, the socket and plugins. `_Input` marks every
  event handled. `DrawFrame` sizes from `DisplayServer`. `GameScene.Load`
  resizes the window and builds UI. None of these may run in the editor.
- `UltimaOnline.Load` builds its own `UOFileManager`, the renderer's asset
  classes, the hue and light textures (through `SetHueTextures`) and the
  `World`. It needs the controller only for the batcher.
- **Found by the reload test:** `new World()` creates `UoAssist`. With
  `Client.Game.Window` set, `UoAssist` registers a Win32 window class whose
  window procedure is a managed delegate, on the *editor's* window handle,
  and never unregisters it. After an assembly reload the next `World` calls
  into the unloaded delegate and the CLR dies (`0x80131506`). The embedded
  controller therefore has no `Window`, which is also right on its own terms:
  the editor must not publish a UOAssist window for Razor to attach to.
- A reload recreates editor objects through their parameterless
  constructors. A constructor that sets `Visible` fires `visibility_changed`
  into handlers from the unloaded assembly. The view sets nothing there, and
  unhooks named handlers on teardown.

## Decision

### Architecture

```
EditorPlugin (main screen "UO World")
  WorldView : VBoxContainer          toolbar, SubViewportContainer (nearest)
    SubViewport (nearest)
      Node2D "WorldCanvas"           the batcher's canvas item; RenderTargets live under it
  WorldHost                          owns the embedded GameController, World, GameScene
    GameController(embedded: true)   PORT DEVIATION block in Client/GameController.cs
    Client.AttachEmbedded / Detach   PORT DEVIATION block in Client/Client.cs
    World.Player                     stand-in: Graphic 0 (no body), placed on the land Z
```

Per frame, when the tab is visible, `WorldHost.Draw` sets `Time.Ticks`, sizes
`Camera.Bounds` to the viewport, puts `Mouse.Position` where the pointer is
and sets `SelectedObject.TranslatedMousePositionByViewport` as
`GameScene.Update` would. It then calls `DrawEmbedded`: `EnsureSizes`,
`BeginFrame`, `Scene.Draw`, `RenderTargets.Draw`. `GameScene.Update` is
never called, since it pings the network, walks the player and drops chunks.

### Key Interfaces

- `GameController(bool embedded)`, `LoadEmbedded(CanvasItem)`,
  `SetEmbeddedScene(Scene)`, `DrawEmbedded(Node, Rectangle)`,
  `UnloadEmbedded()`: `internal`, in one PORT DEVIATION block.
- `Client.AttachEmbedded(GameController)` / `DetachEmbedded`: `internal`.
- `WorldHost`: `Boot`, `GoTo(facet, x, y)`, `Draw`, `Picked`,
  `PlaceServerMulti`, `RemoveServerObject`, `OpenProject`, `ApplyOverlay`,
  `CloseProject`.
- `MapPanel.JumpToWorld(facet, x, y)` → `GuoEditorPlugin.ShowInWorld`.

### Implementation Guidelines

1. Never add the embedded controller to a tree. Never call
   `GameScene.Load`, `Update` or `Unload`.
2. Settings and the profile point at `build\editor_world\` so the editor
   never touches a player's `settings.json` or profiles.
3. What a server would send is fed through `WorldHost` methods that repeat
   the packet handler's calls in its order (`UpdateGameObject` for 0x1A/0xF3,
   `DeleteObject` for 0x1D), each naming its source. Offline there is no
   server, so the view shows the map files, their patches and the world
   project, and nothing else: no mobiles, no server-placed items or houses.
4. Teardown (plugin exit and before every reload) closes the world project,
   `UnloadEmbedded`, detaches `Client.Game` and frees the controller.

## Alternatives Considered

### Alternative 1: Run the whole client in the editor

Add a real `GameController` under the editor root, logged in to the shard.
It would take over the editor's window, input and mouse, and needs a server
and an account. Rejected.

### Alternative 2: An editor-only world renderer

Draw land and statics from the map files with our own sorting. It would
disagree with the game in exactly the places parity work cares about (sort
order, hues, lights, `CanBeDrawn`). The plan forbids it.

### Alternative 3: A bigger hook set (a flag on every window touch)

Make `GameController` embedded-aware at each call site. That is more lines
in more places, all reconciled on every upstream merge. One block that adds
a separate path is cheaper to keep.

## Consequences

### Positive

- The World tab is the game's renderer: sort order, hues, lights, chunk
  meshes, `CanBeDrawn` and picking are the client's own. Renderer bugs will
  show up here.
- `port_drift`: both touched files count as marked; the unmarked total is
  unchanged.

### Negative

- The editor process loads the install twice: once for the Assets panels
  (`EditorData`) and once for the world (`UltimaOnline.Load`), about 1.5 s
  and the memory of a second set of loaders. Unifying them is possible once
  the panels can read through `Client.Game.UO`.
- Process-wide statics (`Client.Game`, `ProfileManager`, `Settings`,
  `SelectedObject`, `Mouse`, the chunk and object pools) are shared. There
  can be only one world view per editor process. A running client is a
  separate process and unaffected.
- Server state is absent offline, so the view differs from a logged-in
  client by the mobiles, items, houses and decoration a shard sends, and by
  the light level.

### Neutral

- The stand-in player has no body, so nothing is drawn for it. It still
  decides roofs and the camera centre, exactly as a player does.

## Risks

| Risk | Mitigation |
|---|---|
| A future ported change makes `Draw` touch `Client.Game.Window` or the network | `editor_smoke` walks the World tab on every run; a null `Window` fails loudly |
| Something else in `World` registers native callbacks, like `UoAssist` did | `editor_smoke --reload` boots the world before and after a reload |
| Memory grows as the user pans (chunks are never cleared, since `ClearUnusedBlocks` runs from `Update`) | Acceptable for now; call `Map.ClearUnusedBlocks` from the view on a timer if it bites |

## Performance Implications

Boot takes 1.2-1.7 s once per editor session (or reload). A frame is the
game's frame for the viewport's size. The view draws only while its tab is
visible.

## Migration Plan

None; new.

## Validation Criteria

- `editor_smoke`'s World stage: tab brought forward by the Maps panel, world
  booted at map0 1496,1628, more than 0 objects drawn, more than 64 colours
  in the frame (windowed), a pick at the centre reaches the UO Inspector, and
  a server-path multi adds to what is drawn.
- `editor_smoke --reload` with the world booted: no `ERROR` or `Fatal` in the
  editor log.
- **Not yet done:** the plan's pixel comparison against a logged-in client's
  frame at the same spot. It needs a client on the dev shard (`--play`,
  `[go 1496 1628`) and will differ by shard objects and light, so the
  comparison must mask or place those.

## GDD Requirements Addressed

None: GUO is a port. The requirements are docs/editor_plan.md §4.4 and
§5 phase 2, and §9 question 2 (answered: yes, with the two hooks above).

## Related

- docs/editor_plan.md, ADR-0006, ADR-0010, ADR-0011
