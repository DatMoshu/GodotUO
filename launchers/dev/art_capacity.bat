@echo off
rem Runs the synthetic art-capacity Labs contract or checks its .NET dependency (doctor).
setlocal
call "%~dp0..\_shared\common.bat" || exit /b 1
if "%~1"=="" (
    "%UO_PYTHON%" "%UO_TOOLS%\art_capacity\run.py" test
) else (
    "%UO_PYTHON%" "%UO_TOOLS%\art_capacity\run.py" %*
)
exit /b %ERRORLEVEL%
