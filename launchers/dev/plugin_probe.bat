@echo off
rem Builds a native (MSVC) and a .NET Framework test plugin, plays a session with both loaded and prints whether each saw packets and movement.
REM ============================================================================
REM  Load a native and a managed test plugin into the client and check them.
REM
REM  Builds a tiny native plugin (MSVC) and a Razor-shaped .NET Framework one,
REM  plays a session against the configured shard with both listed, and
REM  checks each was installed, saw packets both ways and saw the player move.
REM  The managed one only loads through tools\plugin_host, so this is the
REM  check that assistants such as Razor have a way in.
REM
REM  Needs launchers\shard\run.bat going in another terminal. Your own
REM  settings.json is not touched: the client runs from build\plugin_probe.
REM ============================================================================
call "%~dp0..\_shared\common.bat" || exit /b 1

"%UO_PYTHON%" "%UO_ROOT%\tools\plugin_probe\run.py" %*
exit /b %ERRORLEVEL%
