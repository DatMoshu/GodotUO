@echo off
rem Lists what a C# web export needs (the community Godot build, its templates and SDK) and what this machine has, with a MISS line for each gap.
REM ============================================================================
REM  Says what a Godot 4.7 .NET web export needs and what this machine has.
REM  Read tools\web\README.md and ADR-0008 first: the mono engine ships no
REM  web template, so today this ends in MISS lines that say exactly why.
REM ============================================================================
call "%~dp0..\_shared\common.bat" || exit /b 1

"%UO_PYTHON%" "%UO_ROOT%\tools\web\run.py" doctor %*
exit /b %ERRORLEVEL%
