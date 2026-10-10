@echo off
rem Runs the WebSocket-to-TCP bridge between the browser client and the shard in the foreground until Ctrl+C, or tests a login through it.
rem args: test [--fake]
REM ============================================================================
REM  The WebSocket bridge for the web client: ws://127.0.0.1:UO_WS_BRIDGE_PORT
REM  relays to the shard at UO_SHARD_HOST:UO_SHARD_PORT. Ctrl+C stops it.
REM  "ws_bridge.bat test" relays a login through it; "test --fake" needs no shard.
REM ============================================================================
call "%~dp0..\_shared\common.bat" || exit /b 1

if "%~1"=="" (
    "%UO_PYTHON%" "%UO_ROOT%\tools\ws_bridge\run.py" serve
) else (
    "%UO_PYTHON%" "%UO_ROOT%\tools\ws_bridge\run.py" %*
)
exit /b %ERRORLEVEL%
