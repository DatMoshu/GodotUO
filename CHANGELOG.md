# Changelog

What changed for someone building or playing GUO. Newest first. Developer
detail lives in the commit history and the ADRs under `docs/architecture/`.

## Unreleased

### Performance
- Windows draws the land as one array and covering land in runs by default
  (proposed; the owner decides). At 2560x1440 and zoom 2.5 the Britain bank
  goes from 26 to 40 fps and a dungeon from 44 to 97, with the same pixels.
  `--merged-land=off --merged-cover=off` brings back the old draw
  (`docs/perf/2026-10-19_cover_regression.md`).

### Editor
- The editor's automation (MCP) tools have per-user permissions in Editor
  Settings: reading state, moving around and connecting start on; anything
  that changes your server starts off until you turn it on. Every call is
  checked against your saved choices, changes to them go in the Admin
  audit log, a damaged permissions file denies access without touching your
  manual tools, and switching AI off hides the settings and stops the work.
  None of this is in an exported client.
- StaticStudio now respects the editor AI switch: generation and workflow
  discovery stop when disabled, pending or rebound jobs cannot publish stale
  images, and saved-theme/staged-content authoring remains available.
- ComfyUI setup now pins custom nodes to commits and verifies model SHA256
  hashes. The new ComfyUI doctor checks an existing installation offline;
  mismatched files stay untouched and first-use models are marked unverified.
- The Admin tab's god view works with nobody logged in: **Go there**,
  **Bring here**, **Open paperdoll** and **Follow** use the server's hidden
  presence when no staff character of yours is online (or pick it in
  **Act as**). Nothing moves in the game but what you bring: the presence's
  spot shows on the map as a white cross, Bring here brings to it, and the
  paperdoll is listed in the tab. The god view no longer moves a staff
  character above your tab's own level.
- The Admin tab has **Commands**: every command your server has, with its
  level, usage and description. Search them, see the help for the one you
  type, bring back earlier ones with Up and Down, and read what the server
  answered right in the tab. No character needs to be logged in; name your
  staff character in **Run as** for a command that asks for a target in
  game. Shutting the server down, wiping, deleting accounts, decorating the
  whole world and changing many things at once ask you to type the
  command's word first. Every command is in the audit log, and one carrying
  a password is logged by its name only.
- The Shard dock's command box needs your server's admin token now (it
  sends it for you), and shows what the server answered.
- The Admin tab has **Backups** for a server on this computer: **Back up
  now** saves the world and keeps a copy of the save, and the oldest copies
  go once there are more than you keep (10 unless you change it).
  **Restore** asks first, keeps a copy of the world as it is, restarts the
  server with the backup in place, and puts the previous save back if
  anything goes wrong. Health shows when the last backup was taken.
- Stills of the Admin tab's Settings no longer show the folder of your UO
  install.
- The Admin tab has **Accounts**: every account on your server with its
  access level, last login and characters. Make an account, give it a
  level, give it a new password (made for you, shown once and masked, or
  typed twice), ban it or lift the ban. Passwords fit the login screen's
  16-character box and are never written to a log. Every change is in the
  audit log.
- Your server's generated staff passwords have 16 characters now, so they
  fit the login screen; a longer one made before is replaced the next time
  the server starts.
- The Admin tab has **Settings**: a form over your server's configuration,
  with plain labels, checks on every value, a list of what you changed, and
  **Save and restart**, which keeps the previous files. Passwords and webhooks
  are never shown; they are kept in your own secrets file.
- The god view **acts**: with a player, NPC or spawner selected, **Go there**
  moves your own logged-in staff character to it, **Bring here** brings a
  player or NPC to your character, **Open paperdoll** opens its paperdoll in
  your client, and **Follow** keeps your character beside it (the map follows
  it too, across facets) until you stop it. A spawner has **Respawn** and
  **Clear**. "Act as" picks the character when more than one of yours is
  online; a line under the buttons says why one is off. Every action is in
  the audit log.
- The Admin tab has a **god view**: every player, NPC and spawner on a facet,
  drawn over the facet's radar map and kept up to date as they move, spawn and
  die. Filters show or hide players, NPCs and spawners; **Find** searches
  every facet by name or serial and centres the map on a result. Selecting a
  spawner shows what it spawns and how many, and lines to its creatures. It
  needs the GameMaster level of the admin token. Every time in the tab is now
  UTC, written with a Z. The shard's `run.sh` (Linux, Steam Deck) hands the
  bridge its admin token as `run.bat` does.
- The editor has an **Admin** tab for a server you run on this computer: its
  health (uptime, players online, items, mobiles, memory, last save) in
  numbers and in plain words, **Save now**, and **Restart**, which saves first
  and then has the run bar stop and start the server. It uses the server's
  admin token from your workspace and never shows it. The shared dev shard
  (`launchers\shard\run.bat`) now also hands the token to the editor
  bridge when that shard loads it.

### Safety
- The voice tool can no longer hang the voice worker: its output is read
  while it runs, the time limit now works, and speech text and names from
  the server reach it as whole arguments, so a stray quote or backslash
  cannot shift them.
- The editor bridge has an admin channel for the coming Admin tab: admin
  operations run only for an editor that sends the server's admin token
  (generated per user beside the shard passwords, never printed), each at a
  stated access level, and each is written to an audit log on the server with
  passwords and tokens masked. Map editing works as before, with no token.
- The shard's owner (and game master) account is no longer handed to whoever
  logs in under its name first: an existing account is raised only if it
  already holds the configured password, and a blank or old default password
  makes no staff account at all. A hosted shard profile for a public server
  (`guo-vps`) turns automatic account creation off. Run
  `launchers\shard\fetch.bat` once to re-apply the changed patch.
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
- The objects proof tool refuses an `--out` or `--clip` outside `build/` (or
  overlapping the client install) before it deletes or writes anything.
- Every GUO change to ModernUO, and every ModernUO bug found, is listed in
  `tools/modernuo/UPSTREAM.md` with an upstream-or-ours verdict and
  upstream-ready versions waiting for the owner's review. Nothing has been
  submitted.
- The editor saves world blocks, shard objects and hue overlays to a temporary
  file and then moves it into place, so a crash mid-save leaves the old file
  whole.
- The data writer checks every file it is about to write (after following `..`
  and links) lies inside its staging folder, and refuses the whole call before
  writing anything if one does not.
- The client's startup trace no longer prints the account name or password
  passed on the command line; the one-line change to ported code is recorded
  in docs/upstream/GUO_DIVERGENCES.md.
- The dev shard raises an existing account to owner or game master only if it
  already holds the configured password, and never accepts a published default
  password; the VPS shard profile is included (MUO patch, ours).
- The server manager's own tests build and pass again without the Godot
  engine; the server console path now lives in the engine-free workspace code,
  unchanged.
- A server packet with an impossible length, or one that makes its handler
  fail, is logged and dropped instead of freezing or crashing the client;
  connecting to a dead server gives up after 5 seconds. The three edits to
  ported code are recorded in docs/upstream/GUO_DIVERGENCES.md.
- launchers/shard/fetch.bat and fetch.sh stop with git's error when a ModernUO
  patch neither applies nor is already applied, instead of saying 'already
  applied' and building the old patch; moving to a new pin resets the files of
  a patch that will not come off.

### Platforms
- **Android emulator**: `launchers\android\smoke.bat --avd guo_phone`
  boots GUO's own emulator, installs the app, waits for the login screen,
  takes a screenshot and cleans up. It only ever touches the emulator it
  started, and needs 10 GB of free memory.
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
- **Android release APK**: `launchers\android\export.bat --release` builds
  an APK signed with your own key, kept outside the repository and named in
  your local config. `doctor.bat --release` says what is missing. On a tag,
  the `release` workflow also builds one when the repository holds the key as
  secrets.
- **Web**: plays on a local shard in Chrome, and reaches the login screen in
  Firefox, on a community build of the engine; `launchers\web\doctor.bat`
  says what it needs. ADR-0008.

### Playing
- After Alt-Tab, Alt, Ctrl and Shift no longer stay held: the next click is
  a plain click again, as in ClassicUO.
- AltGr characters type on keyboards that need them (the `@`, `{` or `\` of
  German, French, Polish and similar layouts), and Chinese, Japanese and
  Korean input methods work in text fields.
- When a server refuses GUO's client version, the disconnect message says
  so in plain words, with the server's own words and the version GUO
  reported, instead of a bare "connection lost"; a server that drops the
  connection during the login gets the same hint. The Servers tab shows the
  client version a shard needs, or the one you last played it as.
- **Enhanced HD** (Options, off by default): world sprites can be drawn from
  an installed, hash-checked HD art pack, with a choice of filter (Auto,
  Point, Point with mipmaps, Smooth). Anything a pack does not cover stays
  native art, and cursors, the backpack and clicking always use native art.
  With it off, the game draws exactly as before. ADR-0027.
- **Regional audio and recorded voices** (Options > Sound, off by default):
  installed packs can play music and sounds by region and recorded voices
  for NPCs, matched by name, then body. **Change:** ordinary NPC speech no
  longer starts text-to-speech; an NPC is silent until a voice pack covers
  it. ADR-0036.
- **Screen effects**: 32 more looks, from MeesaJarJar, come as an optional
  Store pack rather than as built-ins; the curated built-in looks stay.
  Installing the pack does not switch a look on; Classic switches effects
  off.
- Screen effect sliders that take whole numbers stay whole and inside their
  range, and resizing the window with an effect on no longer drops the
  picture.
- Clicking a replaced object (such as a 3D splat standing in for a statue)
  now targets, uses or picks up the original, as it would without the
  replacement.
- Controller: a **Say** screen speaks the common commands with A (bank,
  vendor buy and sell, guards, hail, all follow me and the other pet
  commands). Find it at the top of the Macros screen or put it on the wheel.
- Controller: with a target cursor up, the radar lists yourself, and the
  pointer starts on the tile in front of you for spells aimed at the ground.
- Controller: Set controls from the pad's own Options screen opens the list
  of jobs too, as it does from Options.
- Controller: **Set controls...** in Options is now a list of jobs. Pick one
  with the D-pad and set only that one; a quick tap of B keeps it as it was,
  **Reset to defaults** puts the shipped layout back, **Save** keeps your
  changes and B leaves without changing anything. One wrong press no longer
  means setting every button again.
- Controller: mounting works from the radar. Choosing a horse that is a few
  tiles away walks you up to it and mounts; while riding, choose yourself in
  the radar to get off.
- Controller: in war mode, A on a monster in the radar attacks it, as a
  double click does with the mouse, and keeps it as your target, so X goes
  on attacking it after the radar closes.
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

- Dev shard: exempt loopback from automatic behavioral ban contributions so a
  silent readiness connection cannot reject all local clients for 15 minutes;
  preserve explicit bans and LAN/public protection (NET-00c, MUO patch).
- `docs/input_audit_2026-10-18.md`: every input in keyboard and mouse, gamepad
  and touch compared with ClassicUO, with a severity each, the upstream line
  it differs from, and the scripted runs behind it. It sets the order of the
  input fixes that follow.
- Readying a fresh git worktree (`launchers\dev\worktree_setup.bat`) also
  copies your Claude Code approvals (`.claude/settings.local.json`), so a new
  worktree no longer asks again to approve the project's MCP servers.
- Generated output (screenshots, smoke runs, exports) can live outside the
  checkout: set `UO_BUILD_DIR` in config.local.bat, one folder per checkout
  with `%UO_CHECKOUT%`. Left unset, it stays `build\` as before.
- `launchers\dev\build.bat` stops a headless build that runs longer than
  `UO_BUILD_TIMEOUT` (180 seconds by default) and cleans up only the
  processes it started.
- GUO's features are sorted into layers, from the faithful port (L0) through
  player options, content packs and editor packages to Labs (L4), with
  review rules for each, in AGENTS.md and CONTRIBUTING.md. Labs features
  build only in an opt-in Debug build and never ship in an export.
- The Store knows three new pack kinds: HD art, 3D splats and recorded
  voices. Installing one runs nothing; the publisher and the client check
  the same rules.
- The editor smoke's Roofs check judges only the roofs the World view
  draws and waits for their fade to finish, so it no longer fails at
  random.
- `docs/data_formats.md` describes the Admin tab's backup folders and their
  manifest.
- Upstream drift report for 2026-10-18 (`docs/upstream/drift_2026-10-18.md`):
  the eight ClassicUO commits since the reviewed pin, each marked take, skip
  or later, and what the ClassicUO 2.0 beta means for GUO (its source is not
  public, so it is a reference for the water, terrain and modding work, not
  code to port).
- World editor: the Layers menu (Land, Statics, Multis, Roofs, Objects and the
  live layers) now looks like a button beside the others, keeps its ticks for
  the whole editor session, and switching Roofs off takes effect at once.
- The World tab speaks plain words: each view's legend says what its colours
  mean (the IDs patchwork is normal, not damage), a brush stroke that places
  nothing says which rule stopped it (Keep existing statics, Avoid water,
  Density), choosing a Nearby tiles row names the Set Z / hue button, Area to
  multi announces the new building in the Multis tab with a Back to World
  button, and item rows show the name before the number.
- World tab settings stay readable on a small screen: the Tools tab keeps the
  rows its settings need, a "More below" bar marks settings under a fold, the
  inspector never covers the settings, the tool rail and the map name tools
  in words, the view legend sits under the map's hint line in larger text,
  and the item box shows the ground tile chosen for Terrain.
- World editor tour: a recorded walk through every map-editing feature of the
  World tab, in plain words, with every on-screen mark checked against the
  control it names; the editor's own tools to record, export and review it.
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
