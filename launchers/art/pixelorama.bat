@echo off
rem Opens a PNG in Pixelorama with the GUO tools extension (UO hue palettes, size checks, Save back to GUO); Pixelorama's window opens and this waits for it.
rem args: <file.png> [--sidecar <file.json>]
REM ============================================================================
REM  Open a PNG in Pixelorama with the GUO tools extension (UO hue palettes,
REM  templates, size checks, "Save back to GUO"). See tools\pixelorama\README.md.
REM
REM      pixelorama.bat <file.png> [--sidecar <file.json>]
REM ============================================================================
call "%~dp0..\_shared\common.bat" || exit /b 1
"%UO_PYTHON%" "%UO_TOOLS%\pixelorama\run.py" open %*
exit /b %ERRORLEVEL%
