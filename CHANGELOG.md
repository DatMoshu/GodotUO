# Changelog

What changed for someone building or playing GUO. Newest first. Developer
detail lives in the commit history and the ADRs under `docs/architecture/`.

## Unreleased

### Safety
- The privacy scan's `--staged` mode checks the staged content that will be
  committed, not the working copy, and reads UTF-16 text as text.
- Deploying a hosted shard stops with git's error when a ModernUO patch does
  not apply, instead of calling it already applied.
- The local dev shard listens only on this PC unless `UO_SHARD_BIND` opens it
  on purpose (for a phone or the LAN set `UO_SHARD_BIND=0.0.0.0` in
  config.local.bat). Its owner and game master passwords are generated per
  user into the workspace's `shard\secrets.bat` and never printed; an existing
  shard switches to them at its next start. Run `launchers\shard\fetch.bat`
  once to re-apply the changed patch.

### Platforms
- **Android** (ARM64, debug): export, install, run and smoke from
  `launchers\android\`. Login screens are centred, the soft keyboard opens
  for text fields, chat sits above the keyboard, and Android Back closes the
  top gump, then the keyboard, then asks before quitting. ADR-0017.
- **Dual screen**: on a device with a second display (such as the AYN Thor)
  the lower screen opens with the client, shows a welcome panel before
  login, and holds shelved gumps in fixed slots during play. ADR-0009.
- **Steam Deck**: reaches the login screen. Export, push over ssh, run, screenshot and smoke from
  `launchers\steamdeck\`; a Steam shortcut too. ADR-0018.
- **Windows**: `tools/windows` exports a stand-alone build with the GUO
  sigil as its icon.
- **Downloads**: every push to main builds Windows, Steam Deck and an
  Android debug APK in GitHub Actions (the `release` workflow); a `v*` tag
  makes a draft release with all three.
- **Web**: still blocked upstream; `launchers\web\doctor.bat` reports why.
  ADR-0008.

### Playing
- Canvas background behind the game: grey, wood, your own image or video,
  or one of ten built-in CC0 loops. ADR-0016.
- Containers can open as a grid of finger-sized slots (mobile default), and
  new containers are placed clear of the character and of open gumps.
  Corpses open as one grid on mobile. A drop on an empty grid slot lands
  there, and the other items stay put.
- Targeting has Self and Cancel buttons on touch screens.
- A combat macro row on touch screens (Next Target, Attack Last and more)
  behind a chevron on the bar; it shows itself on entering War mode unless
  you hid it this session.
- The item you are holding shows at the pointer on both screens of a
  dual-screen device.
- A screen saver for OLED screens: after a set idle time it blacks out both
  screens with drifting effects; the waking tap never reaches the game. On
  by default on Android.
- Very short taps now answer target cursors, and a tap holds a button for
  two frames as a mouse click does.
- The desktop opens full-window with zoom on by default.
- Per-pixel land lighting; the circle of transparency works (it never did
  before); night lighting and world draw order match ClassicUO.
- Profile settings migrate automatically; profile version 10.

### Building and managing
- **GodotUO Asset Store**: a local web catalogue (`launchers\store\serve.bat`)
  where a folder on disk stands in for web storage, and a Store in Options
  that installs, updates and removes packs with every file hash-checked.
  Ten CC0 background packs to start. Installed backgrounds appear in the
  background picker. Each character can point at a different store
  address; the Store is finger-sized on touch screens, and uninstalling the
  background in use resets it. `tools/asset_store/samples.py` publishes
  labelled sample themes, sounds and presets. ADR-0019.
- **Editor phase 5**: import art and gumps as PNG, edit hues, export a
  patch set and read it back in the client; `tools\world\run.py pack`
  sends your edits to a shard owner. ADR-0020.
- **UltimaLive on UOP installs**: the maps are converted into UltimaLive's
  shard folder (never into your install), and live editing works on all
  six facets.
- **Godot editor addon** (phases 0-4): browse the UO data, a world view,
  export to a shard, and a live tier against a private ModernUO instance.
  ADR-0010 to 0012, 0015.
- Scripted runs never take keyboard focus from the desktop, and are silent
  unless `--sound` is passed.
- The wiki is in the repository under `docs/wiki/`.
- CI checks for game data, machine paths and personal details on every
  push, checks every documentation link, and runs the configuration, store
  and profile-migration tests. An opt-in pre-push hook runs the same checks
  locally (`launchers\dev\install_git_hooks.bat`).
- Known issues for players: `docs/wiki/Known-Issues.md`.
