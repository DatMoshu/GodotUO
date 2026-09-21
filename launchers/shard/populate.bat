@echo off
REM ============================================================================
REM  Generate the dev shard's world: everything the admin gump's
REM  "Do everything" button generates.
REM
REM  A fresh ModernUO save is an empty map -- correct terrain, nobody on it.
REM  The commands that populate it are in-game commands, so this logs the
REM  owner account in with the client and types them. Run it once against a
REM  new world; it takes a few minutes and the shard saves the result.
REM
REM  Spawners and decorations alone are NOT a world. Doors, signs, moongates
REM  and teleporters each have their own generator, and a town generated
REM  without DoorGen has doorways with nothing in them -- which is how this
REM  was found. The list below is the one AdminGump's button 114 runs, in
REM  its order, and it is reachable in game as [admin > Administer >
REM  World Building > Generating > "Do everything".
REM
REM  Needs launchers\shard\run.bat going in another terminal, and
REM  UO_SHARD_OWNER (config.bat) to be the account the client logs in as.
REM ============================================================================
call "%~dp0..\_shared\common.bat" || exit /b 1

echo [populate] Generating the world on %UO_SHARD_HOST%:%UO_SHARD_PORT%
echo [populate] This takes a few minutes.

"%GODOT_CONSOLE%" --path "%UO_GODOT_PROJECT%" -- --play ^
    --shard-command "[TelGen" ^
    --shard-command "[MoonGen" ^
    --shard-command "[DoorGen" ^
    --shard-command "[GenerateSpawners Data/Spawns/**/*.json" ^
    --shard-command "[SignGen" ^
    --shard-command "[Decorate" ^
    --shard-command "[GenChamps" ^
    --shard-command "[DecorateMag" ^
    --shard-command "[GenStealArties" ^
    --shard-command "[SHTelGen" ^
    --shard-command "[SecretLocGen" ^
    --shard-command "[GenLeverPuzzle" ^
    --shard-command "[GenGauntlet" ^
    --shard-command "[GenKhaldun" ^
    --shard-command "[Save" %*

if errorlevel 1 (
    echo [populate] FAILED
    exit /b 1
)

echo [populate] OK
exit /b 0
