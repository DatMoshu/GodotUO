# Unlocalized strings in the mobile UI

Flagged for C8, not fixed. Every string below is an English literal in GUO's
own mobile UI code, where upstream would use a cliloc or a `ResGumps`
resource. The desktop is unaffected: none of these show without the touch
layer, a second screen or the mobile window controls. The Options rows at the
end are the only ones that also show on the desktop.

When these are localized, use a cliloc where the client already has the
word, as the bar's Paperdoll/Inventory/Journal/Map/Chat captions do (3000133,
3000431, 3000129, 3000430, 3000131). Otherwise add a `ResGumps` entry.

Measured on `work/ui-command-bar` after a43a4af.

## Embedded scripts preview (desktop and touch)

`src/Input/Touch/Modern/ModernScripts.cs` adds Scripts — CE preview, Close,
Load, New, Script name, Save, Run, Stop, the supported-command/help text,
unsaved/overwrite confirmations and file/status messages. Runtime diagnostics
in `src/Game/Scripting/ScriptRunner.cs`, `ClientScriptHost.cs` and
`ScriptLibrary.cs` are also English pending localization. These appear on
desktop when the user explicitly opens the scripts panel.
The starter popup, search/preview descriptions and templates, command signatures,
Suggest/Indent buttons, My scripts list, and Scripts/Stop script command-bar
actions are also English literals in this preview.

## Touch bar — `src/Input/Touch/TouchGumpBar.cs` (`Label`)

| String | Where |
|---|---|
| `Options` | Bar button; the top bar has no cliloc for it |
| `Self`, `Cancel` | Target-cursor swap buttons |
| `Nearest Hostile`, `Next Target`, `Attack Last`, `Last Target`, `Last Object`, `Bandage Self` | Macro row captions, also the Options slot lists (`MacroTitle`) |
| `War`, `Peace` | War/Peace macro button |
| `‹`, `›` | Chip paging (symbols; they can probably stay) |

## Minimise chips — `GumpMinimise.cs` (`Title`)

`Paperdoll`, `Status`, `Journal`, `Container` (the fallback used when an item has no name).

## Hold and flick — `GumpFlick.cs`

- The Options choices (`ActionNames`): `Do nothing`, `Send to top screen`,
  `Send to bottom screen`, `Close (with Reopen)`, `Reset size`, `Size menu`,
  `Lock / unlock size`, `Shelf's own slot`, `Minimise to the touch bar`.
- The hint while a gump is lifted: `Release: menu`.
- The direction labels: `To top screen`, `To bottom screen`, `Fit to screen`,
  `Close`, `Reset size`, `Size menu`, `Lock size`, `Unlock size`,
  `To its shelf slot`, `Minimise`, `Nothing`.
- The undo toast: `Closed.`, `Reopen`.
- `DirectionNames` (up/down/left/right), used in the Options row labels.

## Window menu — `WindowMenu.cs`

`Window`, `Close` (tooltip), `Size`, `Lock pinch size`,
`Two fingers leave this window's size alone.`, `Move to bottom screen` /
`Move to top screen` / `Fit to screen`, `Reset size`, `Bottom screen`, `Top screen`,
`This screen`.

## Companion tabs — `CompanionTabs.cs`

`Journal`, `Character`, `Classic ›`, `‹ Tabs` (tabs), `Hits`, `Mana`, `Stamina`,
`Strength`, `Dexterity`, `Intelligence`, `Gold`, `Weight`, `Armour`.
Its timestamps use the fixed format `HH:mm`, not the locale's.

## Options rows added by GUO — `OptionsGump.cs`

- Second screen:
  - `Use the second screen as a shelf for gumps`;
  - the five `Shelve the … when opened` rows;
  - `Second screen scale (0 = as the main screen)`;
  - `Fine second screen scale (overrides the slider)` and its `Off` choice.
- The touch rows:
  - `Touch bar chevron inset from the corner`;
  - `Macro row slot N`;
  - `Hold and flick …`;
  - `Debug: show touches on screen`;
  - `Companion tabs on the second screen (prototype)`.
- Shown on the desktop too:
  - `Show window handles`;
  - `Mobile window controls (for Android emulation)`.

## Added in C10

- **The catalogue** (`BarCatalogue.cs`): every action's `Title` and `Short`,
  the seven group names, and the default speech words.
- **The slot editor** (`BarEditor.cs`):
  - "Row N, slot M";
  - "Tap:", "Hold 1:", "Hold 2:", "None";
  - "Search all actions";
  - "… says:";
  - "Hold a bar button for its two extra actions.";
  - "Cancel", "Save", "X".
- **The popup:** "Edit...".
- **The window menu:** "X", "Fit to screen".
- **The companion tabs:** "Classic", "Tabs".
