@echo off
REM ============================================================================
REM  Generate the dev shard's world: spawners and decorations.
REM
REM  A fresh ModernUO save is an empty map -- correct terrain, nobody on it.
REM  The commands that populate it are in-game commands, so this logs the
REM  owner account in with the client and types them. Run it once against a
REM  new world; it takes a few minutes and the shard saves the result.
REM
REM  Needs launchers\shardun.bat going in another terminal, and
REM  UO_SHARD_OWNER (config.bat) to be the account the client logs in as.
REM ============================================================================
call "%~dp0..\_shared\common.bat" || exit /b 1

echo [populate] Generating the world on %UO_SHARD_HOST%:%UO_SHARD_PORT%
echo [populate] This takes a few minutes.

"%GODOT_CONSOLE%" --path "%UO_GODOT_PROJECT%" -- --play ^
    --shard-command "[GenerateSpawners Data/Spawns/**/*.json" ^
    --shard-command "[Decorate" ^
    --shard-command "[Save" %*

if errorlevel 1 (
    echo [populate] FAILED
    exit /b 1
)

echo [populate] OK
exit /b 0
