@echo off
rem Unpacks UO art, gumps and animations to PNG plus JSON and packs edited folders back with tools/uopack; prints its usage when given nothing.
rem args: unpack|pack|roundtrip|selftest ...
call "%~dp0..\_shared\common.bat" || exit /b 1
if "%~1"=="" (
    "%UO_PYTHON%" "%UO_TOOLS%\uopack\run.py" --help
) else (
    "%UO_PYTHON%" "%UO_TOOLS%\uopack\run.py" %*
)
exit /b %ERRORLEVEL%
