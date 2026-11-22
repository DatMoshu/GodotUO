@echo off
rem Plans the seabed and shallows for a whole facet with tools/seabed and stages it outside the install.
rem args: generate|stats [--facet N] [--seed N] [--out DIR]
call "%~dp0..\_shared\common.bat" || exit /b 1
if "%~1"=="" (
    "%UO_PYTHON%" "%UO_TOOLS%\seabed\run.py" --help
) else (
    "%UO_PYTHON%" "%UO_TOOLS%\seabed\run.py" %*
)
exit /b %ERRORLEVEL%
