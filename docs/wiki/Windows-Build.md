# Windows Build

Running from the launchers uses the engine binary in `tools\godot`. A
**Windows export** produces a standalone `GUO.exe` that carries the project's
own icon and splash. Source of truth: `tools\windows\README.md` and its run
table.

```bat
launchers\windows\doctor.bat    REM what a Godot 4.7 .NET Windows export needs, and what this machine has
launchers\windows\export.bat    REM headless --export-debug "Windows Desktop" into build\windows\GUO.exe, then the icon check
```

Underneath: `python tools\windows\run.py <doctor|preset|export|icon>`.

## What an export does

1. Generates `godot\GUO\GUO.sln` if the editor has not (the .NET export needs it).
2. Renders `tools\windows\export_presets.template.cfg` into
   `godot\GUO\export_presets.cfg` (gitignored). The Android and web tools
   render the same file from their own templates, and each re-renders before
   it exports, so the three never have to coexist.
3. Runs `godot-console --headless --path godot\GUO --export-debug "Windows Desktop" build\windows\GUO.exe`.
   That runs `dotnet publish` for win-x64 and packs the project into `GUO.pck`
   beside the executable, with `GUO.console.exe` as the console wrapper.
4. Extracts the icon Windows associates with the exported `GUO.exe`, saves it
   as `build\windows\exe_icon.png`, and compares it with the 32 px frame of
   `icon.ico` (must match) and with the icon of the export template itself
   (must differ: that one is the engine's logo). **A build whose icon is
   still the engine's fails the export.**

The exported build ships the plugin host, so assistant plugins load from it
as they do from a launcher run (commit "Ship plugin_host in an exported
build").

## The icon and the splash

Godot 4.7 writes the executable's PE resources itself; the `rcedit` setting of
Godot 4.3 and earlier no longer exists, so there is nothing to install. The
preset's `application/modify_resources=true` turns the rewrite on and both
`application/icon` and `application/console_wrapper_icon` name
`res://icon.ico`.

| Where the brand shows | Setting | File |
|---|---|---|
| Window and taskbar at runtime | `application/config/icon` | `godot\GUO\icon.png` |
| Same, with proper small sizes on Windows | `application/config/windows_native_icon` | `godot\GUO\icon.ico` |
| The exported `.exe` and `.console.exe` | preset `application/icon`, `console_wrapper_icon` | `icon.ico` |
| Boot splash | `application/boot_splash/image` | `godot\GUO\splash.png` |
| Android launcher and Android 12 splash | Android preset `launcher_icons/*` | `godot\GUO\android_icons\*.png` |

All of them are built from the one committed master,
`design\brand\guo-sigil.png`, by `python tools\brand\run.py`
(`launchers\dev\brand_icons.bat`). Change the sigil, rerun the tool, commit
what it wrote. The sigil is not pixel art, so the tool resamples it with
Lanczos; CLAUDE.md rule 7 is about the game's art.

One thing an export cannot change: a run **through the engine binary**
(`play.bat`, `screenshot.bat`, the editor) shows the engine's icon in the
title bar for the first few hundred milliseconds, until Godot applies the
project icon. The exported `GUO.exe` never shows it.

## What has been run

From the README's table, recorded 2026-09-26 on the branch that introduced
the sigil: `doctor` all ok; `export` produced `GUO.exe`, `GUO.console.exe`
and `GUO.pck` with the icon check passing (distance 0.0 to ours, 103.5 to the
engine's); the exported executable's window, title bar and taskbar showed the
sigil, captured through `PrintWindow` into `build\windows\` and
`build\brand_capture_exe\`. The desktop smoke was 5/5.

## Requirements

`doctor.bat` lists them. In short: the pinned mono engine, the mono export
templates for 4.7.2 in `%APPDATA%\Godot\export_templates\4.7.2.stable.mono`,
and the .NET SDK. Build output is gitignored under `build\windows\`.
