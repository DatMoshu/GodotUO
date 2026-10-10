@echo off
rem Generates a UO map procedurally with tools/mapgen and validates it; prints its usage when given nothing.
rem args: prepare|schema|run ...
call "%~dp0..\_shared\common.bat" || exit /b 1
if "%~1"=="" (
    "%UO_PYTHON%" "%UO_TOOLS%\mapgen\run.py" --help
) else (
    "%UO_PYTHON%" "%UO_TOOLS%\mapgen\run.py" %*
)
exit /b %ERRORLEVEL%
