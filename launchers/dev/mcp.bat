@echo off
setlocal
REM Opt-in agent-controlled GUO. Set the same port/token in the MCP client.
call "%~dp0..\_shared\common.bat" || exit /b 1
if not defined GUO_MCP_PORT (
    echo [mcp] Set GUO_MCP_PORT to an unused local port, e.g. 18670.
    exit /b 1
)
if not defined GUO_MCP_TOKEN (
    echo [mcp] Set GUO_MCP_TOKEN to a random secret of at least 32 characters.
    exit /b 1
)
set "GUO_MCP_DISPLAY="
if /I "%~1"=="--headless" (
    set "GUO_MCP_DISPLAY=--headless"
    shift
)
set "GUO_MCP_ARGS="
:parse
if "%~1"=="" goto run
set "GUO_MCP_ARGS=%GUO_MCP_ARGS% %1"
shift
goto parse
:run
"%GODOT_CONSOLE%" --path "%UO_GODOT_PROJECT%" %GUO_MCP_DISPLAY% -- --no-focus --silent %GUO_MCP_ARGS%
exit /b %ERRORLEVEL%
