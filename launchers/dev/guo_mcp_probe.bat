@echo off
rem Self-tests the client automation MCP bridge and prints each check; --client also starts a client to drive.
rem args: --headed | --client
call "%~dp0..\_shared\common.bat" || exit /b 1
"%UO_PYTHON%" "%UO_TOOLS%\guo_mcp\run.py" probe %*
exit /b %ERRORLEVEL%
