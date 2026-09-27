@echo off
REM ============================================================================
REM  Exports the Windows build headless into build\windows\GUO.exe, then
REM  extracts the executable's icon and fails if it is not the sigil.
REM  Log: build\windows\export.log. Read tools\windows\README.md.
REM ============================================================================
call "%~dp0..\_shared\common.bat" || exit /b 1

"%UO_PYTHON%" "%UO_ROOT%\tools\windows\run.py" export %*
exit /b %ERRORLEVEL%
