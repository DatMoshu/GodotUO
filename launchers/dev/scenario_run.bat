@echo off
rem Runs a scripted scenario with the AI or human driver and records it under build/runs, or lists and validates scenarios (lists them when given nothing).
rem args: <scenario id> [--driver ai|human] | list | validate <id>
call "%~dp0..\_shared\common.bat" || exit /b 1
if "%~1"=="" (
    "%UO_PYTHON%" "%UO_TOOLS%\scenario_run\run.py" list
) else (
    "%UO_PYTHON%" "%UO_TOOLS%\scenario_run\run.py" %*
)
exit /b %ERRORLEVEL%
