# ADR-0019: GUO Asset Store

## Status

Accepted for implementation — 2026-09-27. Local store and portable C#
installer verified; see Validation for the exact evidence.

## Decision

The content-pack expansion is tracked in
[asset_pack_ecosystem.md](../asset_pack_ecosystem.md). Its v2 envelope adds
client/server/combined targets and typed components while preserving v1.
This changes packaging support; it does not imply every asset consumer is
implemented or that installation activates content.

GUO distributes independently licensed user content as ZIP packs. The pack
and index contract is defined in [data_formats.md](../data_formats.md#12-guo-asset-store-packs-adr-0019),
written before either producer or consumer. Supported kinds are background,
theme, sound and profile-preset. Art overrides remain disabled. Installation
of theme, sound and preset packs stores their files; it does not execute
code, merge arbitrary profile JSON or replace game art.

The standard-library Python tool in `tools/asset_store` verifies, publishes,
indexes and serves immutable id/version releases from `UO_STORE_DIR`.
`index.json` and a static searchable card page are at its root. A single
HTTP byte range is supported for GET and HEAD. A seed command builds ten
reproducible CC0 packs from the shipped background loops and their original
licence notice. No UO install is consulted during publication.

`src/Store/StoreClient.cs` fetches the configured `UO_STORE_URL` index and
validates downloads into `user://store/<id>/<version>`. It has no Godot
reference: the headless smoke links these exact sources. A ZIP hash checks
the transfer; per-file hashes and matching index/manifest metadata check
its contents. Installation stages beside the destination and renames only
after every check passes. Existing versions are immutable. Removal stays
inside the selected installed version. Filesystem links, ZIP traversal,
case aliases, executable content, UO data names and oversized content are
rejected. HTTP redirects are disabled; pack URLs stay at the configured
origin and directory. HTTPS provides publisher authenticity on remote
stores; hashes alone are not signatures.

The in-client store runs on a Godot CanvasLayer and offers search, kind
filtering, install, version comparison/update and uninstall. Profile
compatibility uses `PlatformDefaults.CurrentVersion` and does not bump it.
An update adds the new version; older versions remain explicitly removable.
Delisted packs also remain removable offline.

## ADR-0016 integration

There is one marked integration block in `OptionsGump.cs`. It adds the
Store button and adapts the existing background dropdown through
`StoreOptions`, entirely implemented under `src/Store`. The existing Apply
path still writes the existing background mode/path keys. Installed choices
use the existing `image` or `video` mode and `user://store` file paths.
Low-power profiles select the pack's preview still. Reopen Options after
install/removal to refresh the choices. The renderer and its sampling state
are unchanged; no store code filters UO pixel art.

## Amendment 1 — the `screensaver` kind (2026-09-27, S11)

A fifth kind, `screensaver`, carries exactly one `.ogv` loop plus its
preview still, and requires `min_profile_version` 11. Profile v11 adds
`ScreenSaverChoice` (data formats section 12) and no platform default; the
version exists so that the installer's existing compatibility gate keeps a
screensaver off a client that cannot pick it. The Python publisher and the
C# installer enforce the same rule; `test_store.py` and the headless smoke
cover acceptance, the missing/extra loop and the v10 refusal.

Options > Video > Screen saver offers the drifting UO effects, the built-in
loops from `assets/screensavers/screensavers.json`, then installed
screensaver packs; the choice is a profile key, not a new renderer seam.
Loops are decoded by a hidden `VideoStreamPlayer` and drawn through the
batcher, so they cover the second screen of a dual-screen device too, and
the whole frame drifts (burn-in safety holds for every choice). The waking
input is still swallowed. Uninstalling the chosen pack resets it to the
effects, beside the existing background reset.

The client gains `--store-install ID`, which installs a pack at startup
through the same in-client installer; scripted and device runs use it to
prove install, pick and play without driving the Store window.

## Amendment 2 — applying theme, sound and profile-preset packs (Proposed, 2026-09-27, S7)

**Status: Proposed.** Draft only: nothing here is implemented, and the owner
agrees the contract before any code is written. Until then, these three kinds
install and are stored, and applying them stays disabled (as today).

### What "apply" means for each kind

| Kind | Carries | Applying it |
|---|---|---|
| `profile-preset` | `preset.json`: profile key → value, only keys on the allowlist below | Writes those keys into the **current character's** profile, through the ordinary profile save path |
| `theme` | `theme.json`: allowlisted **appearance** keys only (hues, fonts, lights), plus optionally one still or `.ogv` for the canvas background and one `.ogv` for the screen saver, both files in the pack | Writes the keys, and sets the background/screen-saver keys to the pack's files, as picking them in Options does (ADR-0016, Amendment 1) |
| `sound` | `sound.json`: GUO's own sound slots → `.ogg`/`.wav` files in the pack (login music, the Store's and the Options' UI clicks, the screen saver's ambience) | Points those GUO slots at the pack's files. **Never** replaces a UO sound id or UO music: that is the same line `art-override` holds for art |

Applying is always a user action (a button in the Store window's detail
view, with a list of exactly what will change). It is never done at install,
at login or by another pack.

### `preset.json` / `theme.json`

```json
{ "schema": "guo/profile-preset@1",
  "keys": { "SpeechHue": 52, "JournalDarkMode": true, "SoundVolume": 70 } }
```

- Only keys on the allowlist for the pack's kind. An unknown key, a key off
  the list, or a value of the wrong type or out of range refuses **the whole
  pack**, not just that key. The check runs at install (publisher and
  installer both, as for every other rule: the S6 corpus gains cases) and
  again at apply.
- Values are typed as the profile types them. Hues are `0..0xFFFF`, volumes
  `0..100`, enums one of their names, booleans `true`/`false`. No strings
  except the file references in `theme.json`, which must name a declared
  payload file.
- `min_profile_version` must be at least the profile version that
  introduced every key used, so an older client refuses a pack whose keys it
  lacks. Nothing is renamed or migrated on the pack's behalf.

### The allowlist (v1)

The rule: **appearance and sound only**. A key may be on the list only if
changing it alters how the game looks or sounds to this player, and nothing
about what they can do, what they can see of others, or what is written to
disk.

| Group | Keys (current `Profile` names) |
|---|---|
| Sound | `EnableSound`, `SoundVolume`, `EnableMusic`, `MusicVolume`, `EnableFootstepsSound`, `EnableCombatMusic`, `ReproduceSoundsInBackground` |
| Text hues | `SpeechHue`, `WhisperHue`, `EmoteHue`, `YellHue`, `PartyMessageHue`, `GuildMessageHue`, `AllyMessageHue`, `ChatMessageHue`, `TooltipTextHue` |
| Notoriety and effect hues | `InnocentHue`, `FriendHue`, `CriminalHue`, `CanAttackHue`, `EnemyHue`, `MurdererHue`, `BeneficHue`, `HarmfulHue`, `NeutralHue`, `PoisonHue`, `ParalyzedHue`, `InvulnerableHue`, `PartyAuraHue` |
| Fonts and text | `ChatFont`, `TooltipFont`, `OverrideAllFonts`, `OverrideAllFontsIsUnicode`, `JournalDarkMode`, `TextFading`, `HideChatGradient` |
| Tooltips | `UseTooltip`, `TooltipDelayBeforeDisplay`, `TooltipDisplayZoom`, `TooltipBackgroundOpacity` |
| Light and look | `UseAlternativeLights`, `UseColoredLights`, `UseDarkNights`, `AnimatedWaterEffect`, `ShadowsEnabled`, `ShadowsStatics`, `TerrainShadowsLevel`, `UseXBR`, `EnableBlackWhiteEffect`, `EnableDeathScreen`, `HueContainerGumps`, `BackpackStyle`, `UseLargeContainerGumps` |
| Canvas and idle | `CanvasBackgroundMode`, `CanvasBackgroundPath`, `CanvasBackgroundFps`, `CanvasBackgroundLowPower`, `ScreenSaver`, `ScreenSaverMinutes`, `ScreenSaverChoice` (the two paths and the choice only through `theme.json` file references) |

`theme` packs may use the Text hues, Notoriety and effect hues, Fonts and
text, Light and look, and Canvas and idle groups. `profile-preset` packs may
use every group.

**Refused, with the reason, so the list's edges are deliberate:**

- **Visibility of others and the world:** `HideVegetation`, `TreeToStumps`,
  `DrawRoofs`, `FieldsType`, `UseCircleOfTransparency` and its radius and
  type, `NoColorObjectsOutOfRange`, `UseCustomLightLevel`, `LightLevel`,
  `LightLevelType`, and the highlight, overhead-name and HP-bar display
  keys. These are legitimate user choices, but a pack that changes what a
  player can see of other players is not "appearance". A future list could
  admit them with a per-key consent prompt; the owner decides.
- **Behaviour:** movement, pathfinding, targeting, hotkeys, macros, drag and
  select, auto-open and corpse options, container placement, spell casting,
  and everything with `Disable`, `Auto`, `Hold`, `Use*System` or `*Packet`
  in its name.
- **Files and privacy:** `SaveJournalToFile`, `JournalFileWithSerial`,
  `MaxJournalFiles`, screenshot settings, `GrabBagSerial`, and any serial or
  path.
- **Device and window:** scale and zoom, window size, position and border,
  `GameWindow*`, `DualScreen*`, touch and flick keys, `ReduceFPSWhenInactive`,
  `ShowWindowHandles`, and all saved gump positions. These depend on the
  device a pack cannot know.
- **Bookkeeping:** `ProfileVersion` and every key the profile migration writes.

### Undo

- **Before applying**, the client writes
  `user://store/applied/<character>/<pack id>@<version>.json`. It holds, for
  each key the pack will set, the value it has now (or "absent").
- **Undo** puts each key back only if it **still holds the value the pack
  set**. A key the player changed afterwards is theirs and is left alone, and
  the undo lists it as "kept".
- **One undo per pack per character.** Applying the same pack again replaces
  its record, keeping the oldest "before" values. Applying a second pack
  records its own undo; undoing packs in any order still only touches keys
  that hold that pack's values.
- **Uninstalling** a pack offers its undo first; its files are then removed as
  today. A background or screen saver that pointed into the pack is reset
  (existing behaviour).
- **Nothing outside the one character's profile is touched,** so undo never
  needs another character, the install, or the network.

### Why not other shapes

- **Merging arbitrary profile JSON** (the pack ships a whole or partial
  `profile.json`): refused already in this ADR's Decision. It would carry
  window positions, serials and paths from someone else's machine.
- **A denylist instead of an allowlist:** new profile keys arrive with every
  merged feature. With a denylist, each new key would be pack-settable by
  default. With an allowlist, a new key is not settable until someone
  decides it is appearance.
- **Replacing UO sounds or music:** this is `art-override` for audio, and
  the same reasons keep it disabled.

### What the owner decides

1. The v1 allowlist above, and whether the "visibility" keys ever get a
   consent prompt.
2. Whether `theme` may carry the canvas background and screen saver files,
   or should only reference packs of those kinds.
3. The `sound` slots list: which of GUO's own sounds are replaceable.

## Amendment 3 — the `postfx` kind (Proposed with ADR-0023, 2026-09-27)

A sixth kind, `postfx`, carries screen-effect looks for the post-processing
framework (ADR-0023): preset `.json` files, the `.gdshader` passes they
name, and images they reference (LUTs, palette strips). It must hold at
least one preset. **`.gdshader` is accepted in this kind only**; any other
kind that carries one is refused. The Python publisher (`pack.py`) and the
C# installer (`StorePack.Validate`) enforce the same rule, and the shared
corpus has cases for it: `ok-postfx`, `postfx-no-preset` and
`shader-outside-postfx`.

Installing needs no apply step and touches no profile key. `PostFxLibrary`
reads installed `postfx` packs (`user://store/<id>/<version>/`) as read-only
folders after the player's own `postfx/` folder, so a player's same-named
look wins. The effects menu lists their presets beside the built-in ones.

A shader is code, but it is GPU code in Godot's sandboxed shading language.
It cannot reach files, the network or the game state, and it only ever sees
the world texture (and the light target when it asks). The worst a bad
shader can do is a black or garish world, or a slow frame. Uninstalling the
pack, or choosing Classic, undoes it.

## Amendment 4 — Razor script packs (2026-10-02)

Authorized by the owner alongside the independent BSD-2-Clause engine and
continued optional external-assistant support. The `razor-script` kind
stores UTF-8 `.razor` scripts under the existing immutable pack/version
directory. The contract is in data formats section 12. No interpreter or
assistant DLL is included; all existing provenance/licence checks remain.

Install never executes a script or changes personal scripts. Installed packs
are browsable in the in-game Scripts popup, with source preview and an
embedded-engine validation result. Unsupported CE commands may be stored and
copied for editing; the preview reports the limitation rather than claiming
full CE support. Validation uses a separate runner and does not stop or run
the player's active script.

Add to my scripts copies one selected script to the current character's
library under a new name, preserving pack/author/version/licence attribution
in a neighbouring text file. Hashes are checked again at read/import time.
Updates add a new immutable version; uninstall removes the pack originals,
not personal copies. External assistants remain independently configured;
store imports do not write into an external assistant's directories.

## Alternatives

- A native service or database adds deployment dependencies without helping
  this folder-based, immutable catalogue. Static files work with an ordinary
  HTTP host as well as the included local development server.
- Writing into the UO installation would mix proprietary data with user
  packs and make removal unsafe. The dedicated user store is the boundary.
- Registering packages in the renderer would violate ownership and expand
  the rendering change. The existing ADR-0016 mode/path seam is sufficient.

## Validation

- `python tools/asset_store/test_store.py`: publication immutability, invalid
  metadata, forbidden/path-traversal payloads, corruption and HTTP Range/HEAD.
- `python tools/asset_store/smoke.py`: publish and serve a fixture; use the
  actual C# client to fetch, install, hash-check, discover, compare versions,
  repeat installation, uninstall, reject corruption/incompatible profiles,
  and check staging cleanup. Exit 0/1.
- `python tools/asset_store/seed.py`: ten CC0 loops with stable ZIP timestamps.
- `dotnet build godot/GUO/GUO.csproj`: full client integration build.
- `launchers/dev/smoke.bat`: full engine, build and read-only UO data probe.
- `build/screenshots/store-page.png`: ten-card web catalogue, search and kind
  filters tested in the browser. `build/screenshots/store-installed-background.png` is captured through
  the no-focus screenshot launcher and opt-in StoreProof scene (which sizes
  the login window after startup and constructs the real Options gump).

## Operational notes

Run `python tools/asset_store/seed.py`, then `launchers/store/serve.bat` and
`launchers/store/open.bat`. Use `launchers/store/publish.bat <pack.zip>` for
another validated release. Run `python tools/asset_store/run.py index` to
rebuild the catalogue from existing packs. Set `UO_STORE_URL` for clients and
`UO_STORE_DIR` for the publisher in the shared configuration convention.
Use `serve --port <port>` if the OS reserves the default port; point clients
at that port. A worktree needs the usual shared engine/upstream paths and
an ignored classic `.sln` file for Godot's build callback.

The full dev smoke passes on the rebased main native-loader fix, including the headless editor checks. The client build has no errors and seven inherited warnings.

Store addresses can be saved per character in a Store-owned profile sidecar (data formats section 12), without changing the profile version. The Store window validates HTTP(S) addresses and reports connection failures; pack verification remains unchanged.
