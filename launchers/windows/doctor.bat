@echo off
REM ============================================================================
REM  Says what a Godot 4.7 .NET Windows export needs and what this machine
REM  has: templates, the sigil icon and splash, the project settings that
REM  point at them. Also says why no rcedit is needed.
REM ============================================================================
call "%~dp0..\_shared\common.bat" || exit /b 1

"%UO_PYTHON%" "%UO_ROOT%\tools\windows\run.py" doctor %*
exit /b %ERRORLEVEL%
