# ADR-0016: Canvas Background

## Status

Accepted — 2026-09-26. Runs on Windows (every mode photographed in the
world) and on the AYN Thor (image and video behind the login and shard
gumps, the wood backdrop behind the centred login gump, the mobile profile
migrated to v6 with `CanvasBackgroundLowPower` set; it was v4 in UO_Port, renumbered on transplant behind the desktop v5 table); see Validation.
## Date

2026-09-26

## Last Verified

2026-09-26 — Godot 4.7.2 mono, Windows 11, branch `work/background`.

## Decision Makers

Project owner (the feature and its five modes were decided before this ADR);
`uo-render-engineer` (where the node sits relative to the batcher);
`mobile-web-engineer` (the picker on Android, the low-power default).

## Summary

Upstream tiles one embedded 50x50 art file behind the world render target and
the gumps, and nothing else. GUO makes that one drawing replaceable from the
profile: a `CanvasLayer` at layer -1 (`src/Render/CanvasBackground.cs`) draws
a wood tile GUO owns, a picture, a looping muted `.ogv`, or a folder of frames,
cover-scaled to the window; in the default `builtin-grey` mode the node is
hidden and upstream's draw runs unchanged, so desktop parity is untouched.

## Engine Compatibility

| Field | Value |
|-------|-------|
| **Engine** | Godot 4.7.2 stable mono |
| **Domain** | Rendering / UI / Configuration |
| **Knowledge Risk** | MEDIUM — `FileDialog.use_native_dialog` on Android (system picker, 4.4+) and `VideoStreamPlayer.loop` are post-4.2 APIs; both exist in the pinned 4.7.2 `GodotSharp.xml` |
| **References Consulted** | `tools/godot/.../GodotSharp/Api/Release/GodotSharp.xml` (VideoStreamPlayer, FileDialog, Image members); ADR-0001, ADR-0002, ADR-0017, ADR-0008 |
| **Post-Cutoff APIs Used** | `FileDialog.UseNativeDialog` on Android; `VideoStreamPlayer.Loop` |
| **Verification Required** | The Android system picker returns a path `FileAccess` can open (needed for the copy into `user://backgrounds`); `.ogv` decodes on the device; `Image.Load` of a `user://` path on Android |

## ADR Dependencies

| Field | Value |
|-------|-------|
| **Depends On** | ADR-0002 (the batcher owns the controller's canvas; this node stays off it), ADR-0017 (mobile profile defaults, `PlatformDefaults`) |
| **Enables** | None |
| **Blocks** | None |
| **Ordering Note** | `PlatformDefaults.CurrentVersion` goes 3 -> 4 here; any later per-platform default must take 5 |

## Context

### Problem Statement

The window's backdrop is the one piece of the client a player looks at all
session long that is not game data, and upstream hardcodes it. Players ask
for their own picture behind the world; on a phone, where the world fills the
screen, the backdrop is what the login and character screens sit on.

### Current State

`RenderTargets.Draw` (a ported file) calls `batcher.DrawTiled` with the
embedded `game-background.png` across the whole window, then draws the
world, the lights and the UI target over it. The batcher builds its canvas
items each frame directly under the `GameController` node's canvas item
(layer 0), with draw indices set per item (ADR-0002).

### Constraints

- Rule 2: ported code changes carry a `PORT DEVIATION (GUO)` marker and stay
  small. Rule 7: UO art is never filtered, and `default_texture_filter`, the
  world/UI viewports and the batcher's materials must not change.
- `GUO.Compat` holds value types only; nothing here goes there.
- The background must sit under the batcher's items without any change to
  their ordering, and follow window resizes.
- Nothing may write into the client install; a picked file on Android must
  be readable again on the next launch (scoped storage).
- No `OS.HasFeature` in the background code itself; platform behaviour comes
  from the profile table and the options gump.

### Requirements

- Five modes: `builtin-grey` (upstream, default), `builtin-wood` (GUO-owned,
  CC0 by construction), `image` (PNG/JPG/WebP, cover-scaled), `video` (`.ogv`,
  looping, muted), `frames` (numbered PNGs in a folder, or a sprite sheet, at
  `CanvasBackgroundFps`); plus the shipped loops (`builtin:<name>`) listed by
  `assets/backgrounds/backgrounds.json` (branch `work/backgrounds-media`:
  ten 1280x720 24 fps Theora loops with a same-size first-frame `.png`,
  CC0), which the dropdown reads at build time of the gump.
- Four profile keys, upstream-style initialisers as the desktop values;
  Mobile and Web get `CanvasBackgroundLowPower = true` through one
  `PlatformDefaults` entry at version 6 (was 4 in UO_Port).
- Options -> Display: mode dropdown, path field with Browse, fps slider,
  low-power checkbox.
- `--background <mode>[:<path>]` for scripted runs, never saved.
- Frame cost of the default mode: zero change (the node is hidden and does
  no work beyond one struct comparison per frame).

## Decision

One node, `GUO.Renderer.CanvasBackground : CanvasLayer` (layer -1), created
by `GameController.LoadContent` right after the render targets get upstream's
tile. Inside it one `Surface : Control` fills the viewport (full-rect
anchors; a `CanvasLayer` child's parent rect is the viewport, so resizes are
free) and draws the current mode in `_Draw`, or hosts a `VideoStreamPlayer`
sized to the cover rect.

Every frame the node resolves what it should show, in this order:

1. `CanvasBackground.Override` (set from `--background`, never saved);
2. `ProfileManager.CurrentProfile`'s four keys;
3. before any profile is loaded (login, shard list, character list): the
   background keys of the most recently saved `profile.json` under the
   profiles root, read once, so a player sees their own backdrop from the
   first frame;
4. otherwise `builtin-grey`.

When the resolved settings differ from the last applied ones the surface
reloads. A load that fails (missing file, undecodable image, a stream that
will not start) logs one `[GUO] canvas background: ... failed: ...` line and
falls back to `builtin-grey`. `Active` is true in every mode but
`builtin-grey`; `RenderTargets` is handed a `Func<bool>` and skips its
`DrawTiled` while it is true. That is the whole coupling: two marked hunks in
`RenderTargets.cs`, one in `GameController.cs`.

Filtering: the surface sets its own `TextureFilter` — `Nearest` for the
tiles, `Linear` for a player's picture, video or frames. Nothing else
changes; the batcher's items keep their per-item sampler (ADR-0002).

### Architecture

```
Window (viewport, no stretch)
 |
 +-- CanvasLayer -1 : CanvasBackground            <- this ADR
 |     +-- Surface : Control (full rect)
 |           _Draw: wood tile / image / frame  (or)
 |           +-- VideoStreamPlayer (cover rect, loop, muted)
 |
 +-- layer 0 : GameController (Node2D) canvas item
       +-- batcher items, draw-indexed (ADR-0002)
             [DrawTiled(game-background.png)]   <- skipped while Active
             [world RT] [light RT] [UI RT]

Profile.CanvasBackground{Mode,Path,Fps,LowPower}
   ^ Options gump (Display / Background)      ^ PlatformDefaults v6 (LowPower on Mobile, Web)
   ^ --background mode[:path]  -> CanvasBackground.Override (not saved)
```

### Key Interfaces

```csharp
// src/Render/CanvasBackground.cs
internal enum CanvasBackgroundMode { BuiltinGrey, BuiltinWood, Image, Video, Frames, BuiltinMedia }
internal sealed class BuiltinBackground { string Name, Title, Video, Still; static IReadOnlyList<BuiltinBackground> All; }
internal readonly record struct CanvasBackgroundSettings(CanvasBackgroundMode Mode, string Path, int Fps, bool LowPower)
{
    static CanvasBackgroundSettings FromProfile(Profile p);
    static CanvasBackgroundSettings Parse(string spec);   // "mode[:path][@fps][,lowpower]"
    static CanvasBackgroundMode ParseMode(string name);   // "builtin-grey" ... "frames"
}
internal sealed partial class CanvasBackground : CanvasLayer
{
    static CanvasBackgroundSettings? Override { get; set; }
    bool Active { get; }                                  // RenderTargets reads this
    static void Browse(Node host, CanvasBackgroundMode mode, bool copyIntoUserDir, Action<string> picked);
}

// src/Render/RenderTargets.cs (PORT DEVIATION)
public void SetBackgroundReplaced(Func<bool> backgroundReplaced);

// src/Configuration/Profile.cs (PORT DEVIATION)
public string CanvasBackgroundMode { get; set; } = "builtin-grey";
public string CanvasBackgroundPath { get; set; } = "";
public int CanvasBackgroundFps { get; set; } = 12;
public bool CanvasBackgroundLowPower { get; set; }
```

### Implementation Guidelines

- `builtin-wood` is generated at first use (`WoodTile.Create`, 256x256, sums
  of sines with whole periods across the tile, so it is seamless) and cached.
  There is no art file to import, export, or licence.
- `frames` accepts a folder (natural-order sort: `frame_2` before
  `frame_10`) or one sheet whose stem ends in `_CxR`; a sheet without the
  suffix is a strip of square frames. Every frame is cover-scaled by the
  first frame's size.
- Low power pauses the video once its first frame has decoded and never
  advances frames.
- Browse: `FileDialog` with `UseNativeDialog`. On Mobile
  (`PlatformDefaults.Platform`) the picked file is copied into
  `user://backgrounds/` and that path is stored, because the system picker's
  URI is not reopenable later; on a phone `frames` therefore means a sheet.
  On Web the button is disabled with its label saying so; the path can
  still be typed.
- The options gump writes the four keys on Apply; the node notices on the
  next frame. Nothing else calls into the node.
- Shipped backgrounds: `BuiltinBackground.All` reads the manifest once
  (`{"name","title","video","still"}`, paths relative to the folder). The
  profile stores `builtin:<name>` in `CanvasBackgroundMode` and leaves the
  path field alone. Low power, or a video that will not open, shows the
  still. In an export the `.ogv`/`.png` come through Godot's import
  (`GD.Load`); in a project run they are read off the file; the manifest is
  in the export include filter of both preset templates.

## Alternatives Considered

### Alternative 1: Swap the texture the batcher tiles

- **Description**: keep `RenderTargets.DrawTiled` and feed it a different
  texture per mode; video via `GetVideoTexture()` each frame.
- **Pros**: no new node; one hunk.
- **Cons**: `DrawTiled` tiles, it does not cover-scale; the batcher's items
  are point-sampled by design (ADR-0002), so a photo would be filtered
  nearest or the batcher would need a per-draw filter; video would be a
  texture swap through the batcher every frame.
- **Estimated Effort**: similar, but every mode becomes a batcher feature.
- **Rejection Reason**: puts player media on the UO-art draw path, the one
  place rule 7 says not to touch.

### Alternative 2: A child node of GameController with z_index -1

- **Description**: same surface, parented under the controller's Node2D.
- **Pros**: no CanvasLayer.
- **Cons**: inherits the transform the batcher sets on the controller's
  canvas item at `Begin`, and its draw index competes with pooled batcher
  items on the same parent; ordering would rest on an engine tie-break.
- **Rejection Reason**: a separate layer has no such interaction.

### Alternative 3: Store the background in settings.json (global)

- **Description**: one background for the machine rather than per profile.
- **Pros**: visible on the login screen without the "last saved profile"
  lookup.
- **Cons**: the request is per profile, and settings.json is shared by the
  four multi-client lanes.
- **Rejection Reason**: decided per profile; the login screen follows the
  most recently saved profile instead.

## Consequences

### Positive

- Desktop parity unchanged: the default mode runs upstream's draw and the
  node is hidden.
- Four marked hunks in ported files (`Profile.cs`, `RenderTargets.cs` x2,
  `GameController.cs`), plus the gump; everything else is a new file.
- Player media is filtered only on its own node.

### Negative

- A second reader of `profile.json` (the pre-profile lookup) that has to
  agree with `ProfileManager`'s root rule.
- `PlatformDefaults.CurrentVersion` bumps to 4: every mobile and web
  profile saved before gets `CanvasBackgroundLowPower = true` once.

### Neutral

- `--background` beats the profile for the run and the options gump still
  shows the profile's values; the flag is for scripted runs.

## Risks

| Risk | Probability | Impact | Mitigation |
|------|------------|--------|-----------|
| Android picker path not reopenable / not copyable | Medium | Browse unusable on device | Copy at pick time into `user://`; `--background` and a pushed file as the fallback (Validation) |
| Big image on a phone (memory) | Low | Jank on load | Loaded once per change; a failed load falls back to grey |
| Theora decode cost on a phone | Medium | Battery | `LowPower` default on Mobile/Web freezes the first frame |

## Performance Implications

| Metric | Before | Expected After | Budget |
|--------|--------|---------------|--------|
| CPU (frame time), default mode | x | x + one struct compare | +0 ms |
| CPU, video mode (desktop) | — | one Theora decode/frame at the stream's rate | < 2 ms |
| Memory | — | one texture (image), the frames set, or the video buffer | image size |
| Load Time | — | image decode on first change only | — |

## Migration Plan

1. Profiles gain four keys with defaults; old profiles read as
   `builtin-grey`. Mobile/Web profiles at version < 4 get low power on.
2. Nothing else changes for a desktop profile.

**Rollback plan**: remove the node creation in `GameController.LoadContent`;
`RenderTargets` then never has a replacement and draws upstream's tile.

## Validation Criteria

- [x] Desktop, default run: grey tile, world view and gumps as before
      (`build/screenshots/bg_default.png`).
- [x] Desktop, each mode via `--background`: `bg_builtin-wood.png`,
      `bg_image.png`, `bg_video.png`, `bg_frames.png`, `bg_sheet.png`,
      `bg_video_lowpower.png` (each shows the world and gumps over it);
      a manifest built-in (`builtin:test`, a temporary manifest that was
      not committed): `bg_builtin_manifest.png` (video),
      `bg_builtin_manifest_lowpower.png` (its still).
- [x] Desktop, the ten shipped loops (merge 79e6336 of `work/backgrounds-media`):
      the manifest parses to ten entries (logged by name and title);
      `builtin:moongate-shimmer`, `builtin:rain-on-stone` and
      `builtin:twin-moons` decode and wrap (`video looped (1) at process
      frame 1442` / `1802`), frames 6 either side of the seam are continuous
      (`bgm_moongate_before/after.png`, `bgm_twinmoons_before/after.png`,
      `bgm_rain_cal.png`); `builtin:candle-parchment,lowpower` shows the
      still (`bgm_candle_lowpower.png`). The login window is 640x480 by
      upstream's choice, so those runs grew it to 1600x1000 from outside;
      the gump then stays top-left on the desktop (upstream places it once).
      The Android export packages every `.ogv`, the imported stills and the
      manifest (`GUO-bg-media.apk`, 122 MB, +22 MB over the bare client).
- [x] Desktop, the Options gump's Display page shows the Background section
      (`bg_options.png`, captured by `--ui-probe`).
- [x] `launchers\dev\smoke.bat` OK on the branch (2026-09-26).
- [x] `tools/port_drift --strict`: 16 unmarked files, all inherited from
      `work/ui` and untouched here; none of this branch's edits is unmarked.
- [x] Thor (2026-09-26, debug APKs with `--background` baked in, logged in
      as guoprobe): login screen on the wood backdrop filling behind the
      centred gump (`build/android/thor_login_wood.png`); a pushed PNG behind
      the login and shard-list gumps (`thor_login_image.png`,
      `thor_shards_image.png`); a pushed `.ogv` playing (two captures 1.5 s
      apart differ, `thor_login_video_a/b.png`); the existing mobile profile
      logged `migrated mobile profile v3->v4, set CanvasBackgroundLowPower`
      (in UO_Port, where this table was v4; here it is v6).
      In the world the mobile profile's full-size game window covers the
      backdrop entirely (`thor_world_image.png`), so on a phone the setting
      shows on the login, shard and character screens and behind any
      windowed world; that is by design, not a defect.

## GDD Requirements Addressed

Foundational — no GDD requirement. The port has no GDDs; the request is the
owner's feature decision recorded in this ADR's Summary.

## Related

- ADR-0001 (render presenter seam), ADR-0002 (batcher on canvas items),
  ADR-0017 (Android, `PlatformDefaults`), ADR-0008 (web).
- `godot/GUO/src/Render/CanvasBackground.cs`,
  `godot/GUO/src/Render/RenderTargets.cs`,
  `godot/GUO/src/Client/GameController.cs`,
  `godot/GUO/src/Configuration/Profile.cs`,
  `godot/GUO/src/Configuration/PlatformDefaults.cs`,
  `godot/GUO/src/Game/UI/Gumps/OptionsGump.cs`,
  `godot/GUO/src/Bootstrap/Main.cs`.
