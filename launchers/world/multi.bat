@echo off
rem Mines, builds, validates and writes new multis (houses to castles) with tools/multi; prints its usage when given nothing.
rem args: mine|sheets|build|write|prove ...
call "%~dp0..\_shared\common.bat" || exit /b 1
if "%~1"=="" (
    "%UO_PYTHON%" "%UO_TOOLS%\multi\run.py" --help
) else (
    "%UO_PYTHON%" "%UO_TOOLS%\multi\run.py" %*
)
exit /b %ERRORLEVEL%
