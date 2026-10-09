@echo off
rem Sets up or checks the editor's named local servers and clients and prints what is missing (runs doctor when given nothing).
rem args: init|clients|doctor
call "%~dp0..\_shared\common.bat" || exit /b 1
if "%~1"=="" (
    "%UO_PYTHON%" "%UO_TOOLS%\server_manager\run.py" doctor
) else (
    "%UO_PYTHON%" "%UO_TOOLS%\server_manager\run.py" %*
)
exit /b %ERRORLEVEL%
