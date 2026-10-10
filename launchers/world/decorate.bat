@echo off
rem Furnishes generated multis from UO's own interiors with tools/decorate; prints its usage when given nothing.
rem args: mine|stats|decorate|preview|demo ...
call "%~dp0..\_shared\common.bat" || exit /b 1
if "%~1"=="" (
    "%UO_PYTHON%" "%UO_TOOLS%\decorate\run.py" --help
) else (
    "%UO_PYTHON%" "%UO_TOOLS%\decorate\run.py" %*
)
exit /b %ERRORLEVEL%
