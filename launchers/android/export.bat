@echo off
REM ============================================================================
REM  Exports a debug APK, headless, into build\android\GUO-debug.apk.
REM  Renders the export preset from tools\android\export_presets.template.cfg
REM  (the rendered file is gitignored) and points Godot's editor settings at
REM  the SDK, JDK and keystore from config.bat first. Extra client flags to
REM  bake into the APK go after --args, e.g. --args "--login-probe-stay".
REM ============================================================================
call "%~dp0..\_shared\common.bat" || exit /b 1

"%UO_PYTHON%" "%UO_ROOT%\tools\android\run.py" export %*
exit /b %ERRORLEVEL%
