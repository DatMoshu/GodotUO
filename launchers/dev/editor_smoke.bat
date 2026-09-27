@echo off
REM ============================================================================
REM  Opens the Godot editor on the project and lets the GUO editor addon check
REM  itself against the real client install: the UO docks load, an art search
REM  decodes real pixels, and the editor window is captured.
REM  Output: build\editor_smoke\<mode>\ (report.json, art.png, editor.png).
REM
REM    editor_smoke.bat                  windowed, with a screenshot
REM    editor_smoke.bat --headless       no window, checks only
REM    editor_smoke.bat --reload         also rebuild and hot-reload the C#
REM
REM  See tools\editor_smoke\README.md.
REM ============================================================================
call "%~dp0..\_shared\common.bat" || exit /b 1
"%UO_PYTHON%" "%UO_TOOLS%\editor_smoke\run.py" %*
exit /b %ERRORLEVEL%
