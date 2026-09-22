# ADR-0006: GameController Is a Godot Node, Not a Game Loop

## Status

Accepted

## Date

2026-09-21

## Last Verified

2026-09-21 — the client boots, draws the login screen, takes a click and a
keypress, and reaches the socket. Screenshots in `build\screenshots`.

## Decision Makers

Project owner; `uo-render-engineer`; `fna-migration-specialist`.

## Summary

`GameController` is upstream's `Microsoft.Xna.Framework.Game`: it owns the
window, the graphics device, the frame loop, and — through an SDL event filter
— every input the client receives. `Game.Run()` does not return until the
client exits.

In GUO it is a `Node2D` in Godot's scene tree. Godot owns the loop and calls
into the node; the node owns nothing but the client. `Initialize`/`LoadContent`
become `_Ready`, the loop body becomes `_Process`, `OnExiting` becomes
`_ExitTree`, the window is `DisplayServer`, and the SDL event filter is
replaced wholesale by `src/Input/GodotInput.cs` driven from `_Input`.

Everything above `GameController` — the scenes, the UI, the network stack —
sees the same calls in the same order it always did. That is the point.

## Context — measured, not assumed

### What upstream's Game actually provides

Reading `sources/ClassicUO/src/ClassicUO.Client/GameController.cs`, the base
class is load-bearing in six places and nowhere else:

* the loop that calls `Update` then `Draw`, and `SuppressDraw`;
* `Window` — size, title, position, minimum size, resizability, and the native
  handle that `UoAssist` passes to the Windows API;
* `GraphicsDevice` and `GraphicsDeviceManager`, for the back buffer size, the
  clear, and the render targets;
* `IsActive`, and the `Activated`/`Deactivated` events `AudioManager`
  subscribes to;
* `IsMouseVisible`;
* `SDL_SetEventFilter`, installed in `Initialize`, which is where all input
  happens.

### Godot supplies five of the six, differently

`_Process` is the loop. `DisplayServer` is the window, including
`WindowGetNativeHandle` for `UoAssist`. The graphics device has no equivalent
and does not need one: ADR-0002 already replaced the back buffer and the
batcher with canvas items, so the only thing left of the device is the clear
and the size, and both belong to the viewport. `DisplayServer.WindowIsFocused`
is `IsActive`, and `Godot.Input.MouseMode` is `IsMouseVisible`.

The event filter is the one that does not survive, and it is also the largest
— 340 lines. Godot delivers input as `InputEvent` objects through `_Input`, in
a different set of categories, with text folded into the key press. See
ADR section 5 below and the header of `src/Input/GodotInput.cs`.

### The two things that actually bit

**A node cannot add itself to a tree that is still building.** `Client.Run`
ends by handing the controller to the scene tree, and it is called from the
host's `_Ready`. A direct `AddChild` there fails with *"Parent node is busy
setting up children"* and the client never starts. The call is deferred.
Upstream has no equivalent step, so this rule could not be inherited from it;
it was found by running the client.

**`Begin()` was erasing the frame.** In FNA the device clears the back buffer
and a `SpriteBatch` owns nothing between batches, so opening the batcher
repeatedly in one frame is free. Here the batch is a pool of canvas items that
persist until something clears them, and a single upstream frame opens the
batcher four times — the world, the UI, the cursor, the composite. Resetting
the pool in `Begin` meant each pass wiped the one before it.

### What a frame costs to *not* have a device

Measured on the running client: the login screen builds two 2048x2048 atlas
pages and draws inside a 640x480 window at one client pixel per screen pixel.
No render target is created for the window itself; the viewport is it.

## Decision

### 1. `GameController : Node2D`, in the tree, owned by the host

The host scene's `Main` boots the client, `Client.Run` constructs the
controller and defers an `AddChild` onto the scene root. `_Ready` calls
`Initialize()` then `LoadContent()` in that order, which is the order
`Game.Run` used. `_ExitTree` calls `UnloadContent()` and disposes the scene,
which is what `OnExiting` did.

`partial` is not optional: Godot's source generators emit the other half, and
GD0001 stops the build exactly as a C# error does.

### 2. `_Process` is the loop, and nothing sleeps in it

`_Process(double delta)` calls `Update(delta * 1000)` and then `DrawFrame()`
unless the frame is suppressed. Upstream's fixed-interval throttle is kept
verbatim, including `_intervalFixedUpdate` and the inactive-window branch,
with one deviation: upstream also calls `Thread.Sleep(1)` there. `_Process`
runs on Godot's main thread, and sleeping in it stalls the engine rather than
the client. `Engine.MaxFps` does the pacing; the suppression flag still skips
the draw.

`Draw` is renamed `DrawFrame`, because `CanvasItem.Draw` is the draw signal and
a method of that name hides it.

### 3. The window is `DisplayServer`, behind upstream's `Window` shape

A nested `GameWindow` keeps the members the port uses — `ClientBounds`,
`Title`, `AllowUserResizing`, `Handle` — and forwards each to `DisplayServer`.
Keeping the shape is what lets `UoAssist`, `LoginScene` and `GameScene` stay
as they are.

There is no stretch mode. The client sizes its own window, and any stretch
would leave it drawing into part of a fixed-size root viewport, at a scale it
does not know about, through a filter it must not have (project rule 7).

### 4. Focus and window changes come in as notifications

`_Notification` handles `WMSizeChanged`, `ApplicationFocusIn`/`Out` and
`WMMouseEnter`/`Exit`. The focus cases raise the `Activated` and `Deactivated`
events upstream declared, so `AudioManager` subscribes to them unchanged, and
call the plugin host's focus hooks the SDL filter used to call.

Godot's notification constants are `long` and the override takes `int`; the
switch casts, rather than truncating each constant.

### 5. `src/Input/GodotInput.cs` replaces the SDL event filter

The order inside upstream's filter is the behaviour, and it is copied step for
step: the plugin host is asked before anything else and a plugin that swallows
a key also suppresses the text that would follow it; the scene sees an event
before the UI does and the UI only gets what the scene left; a double click is
a second press inside `MOUSE_DELAY_DOUBLE_CLICK`, tracked per button, and a
handled one poisons the following release with `0xFFFF_FFFF` so it does not
also fire a single click.

Three things move, because Godot puts them elsewhere:

* **Text.** SDL raises `SDL_EVENT_TEXT_INPUT` separately; Godot folds it into
  the key press as `Unicode`. Text is dispatched from the key-down path, with
  upstream's guards — nothing behind a command modifier, nothing when a plugin
  took the key.
* **Mouse enter, leave and focus** are window notifications, so they are in
  `_Notification` (section 4) and not in the input layer.
* **Mouse position** comes from `Mouse.Update()`, which asks the window where
  the pointer is, as upstream's does. The event's own position is not used, so
  synthesised input has to warp the pointer — see Validation.

`SDL_KeyboardEvent` survives as the *shape* the ported code expects, filled in
by this layer. Godot names a letter key by its capital and SDL by its
lowercase, which is the only difference across the printable range; above ASCII
the two numbering schemes are unrelated and are tabled.

### 6. `UltimaBatcher2D.BeginFrame()` is separate from `Begin()`

`BeginFrame` resets the item pool and the counters once per frame, from
`DrawFrame`. `Begin` only sets the view transform and the render state, and may
be called as many times in a frame as upstream calls it. This is a GUO-only
method: upstream has no frame call because FNA's device made one unnecessary.

## Alternatives Rejected

**Keep the loop and run Godot headless underneath it.** Preserves
`GameController` almost verbatim. Rejected: nothing in Godot renders outside
its own frame, so the batcher, the audio players and the input queue would all
need to be driven from a foreign loop, and every one of them is a node.

**A `MainLoop` subclass instead of a node.** Closer to "the client owns the
loop", and Godot supports it. Rejected: it gives up the scene tree, and the
scene tree is what `AudioStreamPlayer` (ADR-0005) and every future Godot-side
tool need. It also has no `_Input`.

**Shim `Game`, `GameWindow` and `GraphicsDevice` in `GUO.Compat`.** Would make
`GameController` a shim-tier file. Rejected on the standing rule that
`GUO.Compat` holds value types only. A `GraphicsDevice` shim is engine
behaviour behind an XNA name, and it is the exact failure mode the narrow
Compat rule exists to prevent — four fifths of this port is mechanical only for
as long as that holds.

**Godot `Control` nodes for the UI, fed by the ported `UIManager`.** Rejected
outright: the client draws its own gumps from `.mul` art, and a parallel
retained-mode tree would have to be kept in sync with a UI that already works.
`_Input` rather than `_UnhandledInput` follows from this — there is no Godot
Control to consume anything first, so everything is marked handled after the
client has seen it.

## Consequences

* `Client.Run` returns immediately instead of blocking until exit. Its remark
  says so; a caller that treats it as "the client has finished" is wrong.
* `GameController` is a rewrite-tier file, and the audit should say so.
* `Network/Plugin.cs` is ported, but `Plugin.ProcessWndProc` — the first thing
  upstream's filter calls, handing a plugin the raw SDL event — has no call
  site: there is no SDL event to hand it. Plugins still get hotkeys and the
  mouse through `ProcessHotkeys` and `ProcessMouse` from the input layer; one
  that relies on OnWndProc sees nothing.
* `TakeScreenshot` is `internal` rather than `private`: PrintScreen reaches it
  from the input layer now, not from a filter inside the class.
* `CUOEnviroment.ExecutablePath` is a `static readonly` off
  `Environment.CurrentDirectory`, and it decides where settings, logs and
  screenshots go. The host moves the working directory before touching anything
  in the client namespace.

## Validation

* `launchers\game\play.bat` — the client boots, loads every archive from a real
  install, and draws the login screen.
* `launchers\dev\screenshot.bat --play --shot-after N` — captures the running
  client after N frames, which is how a claim about what is on screen gets an
  artefact behind it. Screenshot mode's one fixed frame can be captured at
  once; the client spends its first frames loading, and a shot taken then is a
  black window.
* `launchers\dev\screenshot.bat --play --input-probe --shot-after N` — warps
  the pointer onto the login gump's account field, clicks, types, and clicks
  Login, all through `Godot.Input.ParseInputEvent` so nothing on the path from
  the window to the client is bypassed. The shot has the typed text in the
  field; the log has `LoginScene` starting a login and the socket coming back
  refused.
* `launchers\dev\batcher_probe.bat` — 130 checks over the batcher. It does
  not cover the frame split: the defect in `Begin()` was found by reading
  the call sites, and the probe passed both before and after the fix,
  because every one of its stages opens the batcher once.
