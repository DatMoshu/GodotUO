# ADR-0021: Client Data Sources: a Custom Folder, then the UO Install, then the First-Run Wizard

## Status

Accepted (owner, 2026-09-27)

## Date

2026-09-27

## Last Verified

2026-09-27, tools and launchers only (the wizard itself is G2 and does not
exist yet):

- `python tools\guo\test_config.py`: 26 tests pass. The 11 new ones check
  the order on made-up folders and never read the machine's own install:
  - environment, then setting, then default;
  - a broken setting is reported, not replaced;
  - nothing valid resolves to the wizard;
  - a complete custom folder comes first;
  - a layered folder goes over the install, and alone it is the wizard;
  - the manifest checks;
  - `load_config` agrees with the resolver.
- `python tools\datasources\run.py check` on the owner's machine resolves the
  install from `config.local.bat` (origin `setting`).
  - `UO_CLIENT_DATA=D:\nowhere` gives exit 3 with "run the first-run wizard".
  - `UO_CUSTOM_DATA=<the Dreadcrest stage>` gives the install layered with the
    stage's 5 files, marked local only.
- `launchers\game\play.bat`, with a stub in place of Godot (no window), passes:
  - the install as `UO_CLIENT_DATA` in the normal case;
  - `--files-override` for the layered stage;
  - with no valid data, it says the wizard will open and starts the client
    anyway.
- `tools\uodata\run.py verify` and `tools\android\run.py push` return exit 3
  with the wizard message instead of failing. `tools\steamdeck\run.py doctor`
  reports missing Deck data as info, not a failure.
- The Windows platform default is measured, not guessed. The EA installer
  writes `HKLM\SOFTWARE\WOW6432Node\Electronic Arts\EA Games\Ultima Online Classic`,
  value `InstallDir`, on the owner's machine.

The runtime half, `godot/GUO/src/Bootstrap/DataSources.cs`, verified
2026-09-27:

- The resolver replaced the client's `tiledata.mul` probe. Its required
  table, `DataRequirements.g.cs`, is generated from `FILE_REGISTRY`
  (`tools\datasources\run.py gen-cs`), and `test_config.py` fails if it is
  stale.
- Five windowed desktop runs (never focused), with `--login-probe`:

| Run | Result |
|---|---|
| `UO_CLIENT_DATA` a missing folder | logs "wizard needed" with the reason, exits through `OnNoValidData` |
| normal (`UO_CLIENT_DATA`) | install (environment); login gump drawn |
| `UO_CUSTOM_DATA` = the Dreadcrest stage | install+custom, 5 override entries loaded, marked local only; login gump drawn |
| no `UO_CLIENT_DATA`, `settings.json` `ultimaonlinedirectory` set | install (setting); login gump drawn |
| nothing set | install (default) from the registry `InstallDir`; login gump drawn |

The first-run screen (`Bootstrap/FirstRunScreen.cs`), verified 2026-09-27:

- Setup: a windowed run, never focused, with `UO_CLIENT_DATA` pointing at a
  missing folder and `--first-run-probe <the real install> --login-probe`.
- It shows the screen: "GUO needs your own UO install", the reason, and a
  note that the configured `UO_CLIENT_DATA` keeps precedence.
- A bad folder shows 12/12 missing, and Continue stays disabled.
- The real install shows 12/12 found (UOP or MUL for each entry), and
  Continue is enabled.
- Continue saves `ultimaonlinedirectory` to the client home's
  `settings.json`, and the same run goes on to draw the login gump. No
  restart is needed.
- Screenshots are in `build\first_run_proof\`.
- A headless run, or any mode other than play, still logs the reason and
  exits, because there is nobody to ask.

Re-opening from Options, verified 2026-09-27:

- The button is Options > Video > Start > "Change UO folder...", a marked
  PORT DEVIATION in the ported OptionsGump.
- It opens `FirstRunScreen.OpenChange`, the same screen in change mode:
  "Now using: <folder>. The folder you choose is used the next time GUO
  starts.", with Save and Cancel.
- Save sets upstream's `Settings.GlobalSettings.UltimaOnlineDirectory` and
  saves it.
- Proof, on the private shard in game, windowed and never focused: the
  button is on screen, a bad pick is refused, and Save writes the other
  install into `settings.json`. Screenshots are in
  `build\options_folder_proof\watch\`.

**Not yet verified:**

- the native folder dialog itself (`DisplayServer.FileDialogShow`, falling
  back to Godot's `FileDialog`). The probe picks a folder without opening it,
  because opening one needs a person at the desktop.
- the Android SAF picker (GUOAndroid) and the web.
- the Android and Deck platform defaults at runtime. They are written, but
  not yet run on a device.

## Context

Today GUO finds its data one way per surface:

- the launchers read `UO_CLIENT_DATA` from `config.local.bat`;
- the client reads `UO_CLIENT_DATA` or `--client-data`;
- the Deck and Android tools each have their own device path.

With nothing configured, every one of them fails with a different error.
That is fine for the developers and wrong for a player: the owner wants a
first run that asks for the UO install instead of crashing (Epic G).

Two things already put data beside the install:

- ADR-0020's asset overlay: a world project exports a `verdata.mul` and
  `hues.mul` that the client reads through upstream's `files_override`;
- ADR-0022's staged set: modified copies of single install files, also read
  through `files_override`.

The owner also wants room for full art conversions that ship with the
client. So the order has to name a custom folder before the install.

## Decision

### One order, everywhere

1. **A custom data folder**, if one is configured and valid.
2. **The UO install**, if valid.
3. **Otherwise, the first-run wizard.**

`tools/guo/datasources.py` implements the order for the tools and launchers.
The runtime (G2) implements the same order in C#. `docs/data_formats.md`
§15 is the contract between them.

### 1. The custom data folder

Where it comes from:

| Surface | Source, in order |
|---|---|
| Tools and launchers | `UO_CUSTOM_DATA`: environment, then `config.local.bat`, then the central config |
| The client | `--custom-data PATH`, then `UO_CUSTOM_DATA`, then a `guo_data/` folder beside the executable (a shipped pack) |

A custom folder is only recognised by its manifest, `guo_data.json`
(format `guo/data-folder@1`). The manifest is either:

- **`complete`**: a whole data set of its own. It must pass the validity rule
  below by itself. It is then the client's data, and the install is not
  needed. This is the shape of a total conversion.
- **`layered`**: files that replace single files of a UO install. Its
  `files` name them. They reach the client through upstream's
  `files_override` (name=path lines), so the loaders need no new code. A
  layered folder without a valid install beneath it is the wizard's case.

An invalid folder is passed over, with the reason recorded, and the order
goes on to the install. Examples: no manifest, a file it names is missing, a
name that escapes the folder, or an incomplete "complete" folder. One custom
folder is used at a time; stacking several is a later decision.

**How it relates to the other ADRs.**

- **ADR-0022.** A staged set is a layered custom folder. `tools/uodata_write`
  now writes `guo_data.json` into every stage. So
  `UO_CUSTOM_DATA=build\uodata\moshu` is how a developer plays with authored
  content, and `play.bat` passes the override on.
- **ADR-0020.** A world project's export is the same shape: replaced files
  beside the install. The overlay stays the editor's authoring format; its
  export may carry a manifest and then be used as a custom folder.

### 2. The UO install

Where it comes from:

| Order | Tools and launchers | The client |
|---|---|---|
| a. | `UO_CLIENT_DATA` in the environment | `--client-data`, then `UO_CLIENT_DATA` |
| b. | The saved setting: `config.local.bat`, then the central config (`config.bat` ships none) | The saved setting: upstream's `settings.json` `ultimaonlinedirectory`, which the wizard writes |
| c. | The platform default | The platform default |

**A configured value is authoritative.** The first one that is set is the
only one tried. If it is not valid, the result is the wizard with the reason;
it is never silently replaced by another install that happens to exist,
which could be a different client version. The platform defaults are tried
only when nothing is configured.

Platform defaults, tried in order:

| Platform | Default |
|---|---|
| Windows | the registry `HKLM\SOFTWARE\WOW6432Node\Electronic Arts\EA Games\Ultima Online Classic` `InstallDir` (measured); then `%ProgramFiles(x86)%` and `%ProgramFiles%` `\Electronic Arts\Ultima Online Classic` |
| Steam Deck and Linux | `~/UO` (the Deck tools' existing default, `UO_DECK_CLIENT_DATA`) |
| Android | the app's external files folder, `/sdcard/Android/data/<package>/files/uo` (where `tools\android push` puts it) |
| Web | none; the wizard (File System Access, later) |

### What "valid" means

A folder is valid when **every required entry of `tools/guo/formats.py`'s
`FILE_REGISTRY` is satisfied**, using the resolution rules of
`docs/data_formats.md` §3:

- an entry is satisfied by its UOP form, or by its MUL form together with its
  index;
- a MUL without its index does not count;
- names match case-insensitively.

Optional entries never block. The client version does not change which
entries are required, because the registry already accepts the MUL and the
UOP form of each. The version stays a separate setting (`UO_CLIENT_VERSION`,
or `client.exe`), and `tools\uodata verify` reports it in the manifest.

The runtime must use this same rule, not the `tiledata.mul` probe it uses
today, so the wizard and the tools agree on the same folder.

### 3. The first-run wizard

When nothing is valid:

- **Tools** print "no valid UO data found: run the first-run wizard, or set
  UO_CLIENT_DATA ..." and exit with **3**. That code is reserved for this case
  (`datasources.WIZARD_EXIT`); it is not a crash.
- **`play.bat`** says so and starts the client anyway, with
  `UO_DATA_SOURCE=wizard`, and with whatever broken value was configured
  still in `UO_CLIENT_DATA` so the wizard can show it.
- **The client** (G2) opens the wizard instead of exiting. The wizard:
  - checks a folder with this rule;
  - saves it as the saved setting in 2b;
  - continues to login.

### What a shipped data pack may contain

A pack that ships with GUO, or is published for it (a `guo_data/` folder, an
Asset Store pack), follows these rules:

- **Never EA data.** No UO client file, whole or modified, and nothing
  derived from one by conversion, unless the owner has a licence that allows
  it and says so.
- **Every manifest declares `contains_ea_data`** (true or false). A folder
  without the declaration is invalid.
  - `true` is allowed only for local folders built from the user's own
    install. A staged set is one. The resolver marks them "local only, never
    shipped".
  - Release and publishing tools must refuse `true`.
- **Original content only**, with its licence and origin recorded in the
  manifest (`license`, `source`). For art made by Codex or an artist, that is
  the same provenance the Asset Store already records (ADR-0019).
- A **complete** pack must be a full data set of its own original content.
  A layered pack's files replace install files the player already owns.

## Consequences

**Positive**

- One answer to "which data?" on every surface, with the reason for every
  candidate passed over.
- A player with nothing configured is sent to a wizard, not shown a crash.
  The tools already behave that way.
- A developer plays authored content by setting one variable.
- Layered data uses upstream's `files_override`, so no loader changes.

**Negative**

- The runtime half (the C# resolver and the wizard) is not written yet, so
  the client and the tools can disagree until G2 lands.
- A configured but broken path no longer falls back to a working default.
  This is deliberate, but a user who moves their install gets the wizard even
  though the registry would have found it.
- A layered folder replaces whole files, not entries within them.

## Alternatives considered

- **Fall through a broken setting to the next candidate.** Rejected: it can
  load a different client silently, and the setting stays broken unnoticed.
- **Merge several custom folders.** Deferred: the order between two packs
  that replace the same file needs its own rule.
- **Keep a separate GUO data-location file.** Rejected: upstream's
  `settings.json` already holds `ultimaonlinedirectory`, and the tools
  already have `config.local.bat`.

## Related

- ADR-0019 (Asset Store provenance), ADR-0020 (asset overlay), ADR-0022
  (staged sets), ADR-0017/0018 (the Android and Deck targets).
- `docs/data_formats.md` §3 (validity) and §15 (the custom-data manifest).
- `tools/guo/datasources.py`, `tools/datasources/run.py`,
  `launchers\game\play.bat`.
