# Changelog

What changed for someone building or playing GUO. Newest first. Developer
detail lives in the commit history and the ADRs under `docs/architecture/`.

## Unreleased

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
- **Web**: still blocked upstream; `launchers\web\doctor.bat` reports why.
  ADR-0008.

### Playing
- Canvas background behind the game: grey, wood, your own image or video,
  or one of ten built-in CC0 loops. ADR-0016.
- Containers can open as a grid of finger-sized slots (mobile default), and
  new containers are placed clear of the character and of open gumps.
  Corpses open as one grid on mobile.
- Targeting has Self and Cancel buttons on touch screens.
- The desktop opens full-window with zoom on by default.
- Per-pixel land lighting; the circle of transparency works (it never did
  before); night lighting and world draw order match ClassicUO.
- Profile settings migrate automatically; profile version 8.

### Building and managing
- **GodotUO Asset Store**: a local web catalogue (`launchers\store\serve.bat`)
  where a folder on disk stands in for web storage, and a Store in Options
  that installs, updates and removes packs with every file hash-checked.
  Ten CC0 background packs to start. Installed backgrounds appear in the
  background picker. ADR-0019.
- **Editor phase 5**: import art and gumps as PNG, edit hues, export a
  patch set and read it back in the client; `tools\world\run.py pack`
  sends your edits to a shard owner. ADR-0020.
- **Godot editor addon** (phases 0-4): browse the UO data, a world view,
  export to a shard, and a live tier against a private ModernUO instance.
  ADR-0010 to 0012, 0015.
- Scripted runs never take keyboard focus from the desktop, and are silent
  unless `--sound` is passed.
- The wiki is in the repository under `docs/wiki/`.
- CI checks for game data, machine paths and personal details on every
  push; the `release` workflow exports Windows and Steam Deck builds.
