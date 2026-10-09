@echo off
rem Builds, stages and proves towns on a generated map with tools/mapgen_districts; prints its usage when given nothing.
rem args: town|build|stage|prove ...
call "%~dp0..\_shared\common.bat" || exit /b 1
if "%~1"=="" (
    "%UO_PYTHON%" "%UO_TOOLS%\mapgen_districts\run.py" --help
) else (
    "%UO_PYTHON%" "%UO_TOOLS%\mapgen_districts\run.py" %*
)
exit /b %ERRORLEVEL%
