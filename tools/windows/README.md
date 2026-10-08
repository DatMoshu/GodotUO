# tools/windows -- the Windows export, and the icon it carries

Exports the client as a Windows build and proves the executable carries
the brand. The brand itself (the sigil, and every icon built from it) is
`tools\brand\run.py`; this folder is the export and its doctor.

```
launchers\windows\doctor.bat   what a Godot 4.7 .NET Windows export needs,
                               and what this machine has
launchers\windows\export.bat   headless --export-debug "Windows Desktop" into
                               build\windows\GUO.exe, then checks its icon
launchers\dev\brand_icons.bat  rebuild every icon from design\brand\guo-sigil.png
```

Everything is `python tools\windows\run.py <doctor|preset|export|icon>`
underneath.

The shared preset excludes seven reviewed editor addon families and five exact
Accounts C# source placeholders. These are resource-package exclusions, not
MSBuild compile removals; account CLR behavior remains compiled. The normal
launcher renders this template before each export. Icon/export success is
separate from privacy, semantic, license and distribution approval.

`audit_scanner.py` provides the reviewed PLT15 unmasked byte/PCK scanner:

```
python tools/windows/audit_scanner.py --package <IMMUTABLE_PACKAGE> --deny-file <LOCAL_DENY_FILE> --review-root <REVIEW_CHECKOUT> --out <NEW_REPORT.json>
python tools/windows/test_windows.py
```

It requires the existing immutable PLT13/PLT05/PLT12 review anchors and `pefile`
in the selected Python environment. Missing anchors or coverage failures are
explicit refusals; no dependencies are installed automatically. Raw findings
remain present even when exact metadata annotations explain them. Semantic
bindings describe the reviewed overnight package only; a new package needs new
independent provenance review. Exit 0 means complete raw visitation, while every
report remains `BLOCKED_PROPOSAL_NOT_ACCEPTANCE`, with distribution uncleared.
The output must be new and separate from package/reviewed artifacts. This tool
does not certify acquisition rights, complete privacy detection, dynamic/native
semantics, install/login behavior or a distributable release.

## What an export does

1. generates `godot\GUO\GUO.sln` if the editor has not (the .NET export
   needs it)
2. renders `export_presets.template.cfg` -> `godot\GUO\export_presets.cfg`
   (gitignored; the Android and web tools render the same file from their
   own templates, and each re-renders before it exports)
3. runs `godot-console --headless --path godot\GUO --export-debug "Windows
   Desktop" build\windows\GUO.exe`, which itself runs `dotnet publish` for
   win-x64 and packs the project into `GUO.pck` next to the executable
4. extracts the icon Windows associates with the exported `GUO.exe`
   (`[System.Drawing.Icon]::ExtractAssociatedIcon`, through PowerShell),
   saves it as `build\windows\exe_icon.png`, and compares it with the 32 px
   frame of `icon.ico` (must match) and with the icon of the export
   template itself, `windows_debug_x86_64.exe` (must differ: that one is
   the engine's logo). A build whose icon is still the engine's fails the
   export.

## The icon, and why there is no rcedit

Godot 4.3 and older changed a Windows executable's icon and version strings
by running `rcedit`, configured in the `export/windows/rcedit` editor
setting, and without it the exported .exe kept the engine's logo. **Godot
4.7 writes the PE resources itself.** There is no `export/windows/rcedit`
setting in 4.7's editor settings, and the engine binary contains no
"rcedit" string at all; the doctor prints this as an `info` line rather
than a check, because there is nothing to install. The preset's
`application/modify_resources=true` is what turns the rewrite on, and
`application/icon` / `application/console_wrapper_icon` both name
`res://icon.ico`, so the game executable and the console wrapper
(`GUO.console.exe`) carry the same sigil.

The other places an icon shows, all pointing at the same brand:

| Where | Setting | File |
|---|---|---|
| Window and taskbar icon at runtime | `application/config/icon` | `godot\GUO\icon.png` (256 px, sigil on a dark rounded slab) |
| Same, with proper small sizes on Windows | `application/config/windows_native_icon` | `godot\GUO\icon.ico` (16..256 px) |
| The exported .exe and .console.exe | preset `application/icon`, `application/console_wrapper_icon` | `icon.ico` |
| Boot splash (was the engine's logo) | `application/boot_splash/image`, `bg_color`, `stretch_mode=1` | `godot\GUO\splash.png` (1280x720, sigil on #14100C) |
| Android launcher (legacy, adaptive, monochrome) and Android 12 splash | Android preset `launcher_icons/*` | `godot\GUO\android_icons\*.png` |

All of them are built by `python tools\brand\run.py` from the one master,
`design\brand\guo-sigil.png`, which is committed: the build has no
dependency outside the repository.

One thing the export cannot change: **a run through the engine binary**
(`play.bat`, `screenshot.bat`, the editor) shows the engine's icon in the
title bar and taskbar for the first few hundred milliseconds, until Godot
applies the project icon. That icon is the engine executable's own
resource. The exported `GUO.exe` never shows it.

## What has actually been run, and what has only been written

Recorded on 2026-09-26, on the branch that introduced the sigil.

| What | Result |
|---|---|
| `python tools\brand\run.py --sheet build\brand_sheet.png` | wrote icon.png, icon.ico (7 sizes), splash.png, the four Android PNGs; sheet at `build\brand_sheet.png` |
| `dotnet build godot\GUO\GUO.csproj` | 0 errors |
| `launchers\dev\smoke.bat` | OK, 5/5 (imports wrote `splash.png.import`) |
| `run.py doctor` | all ok; rcedit reported as not needed |
| `run.py export` | `build\windows\GUO.exe` 100,941 KB + `GUO.console.exe` + `GUO.pck`; icon check: vs ours 0.0, vs the template's engine logo 103.5 -> **ok**; `build\windows\icon_evidence.png` shows exe icon, expected icon, template icon side by side |
| `run.py icon --exe build\windows\GUO.console.exe` | same: 0.0 / 103.5, ok |
| `build\windows\GUO.exe -- --screenshot` captured by `capture_window.ps1 -Process GUO` | the window rendered the sigil splash on #14100C, title bar icon the sigil, taskbar icon the sigil: `build\brand_capture_exe\window_evidence.png` |
| `launchers\dev\screenshot.bat` captured by `capture_window.ps1 -Process Godot_v4.7.2-stable_mono_win64` | same splash and icons once the project loaded; the engine's icon for the first frames, as above |
| `python tools\android\run.py export` | `build\android\GUO-debug.apk`; `res/mipmap-*/icon*.webp` and `res/drawable/splash_icon.webp` dumped and checked: all the sigil (`build\android\apk_icons\apk_icon_sheet.png`). Not installed: another agent held the device |

`capture_window.ps1` is the helper for the two capture rows: it renders the
client's window through `PrintWindow` (so another window on top of it does
not get into the shot; another agent's game was sitting exactly where
ours opens) and grabs the taskbar strip.
