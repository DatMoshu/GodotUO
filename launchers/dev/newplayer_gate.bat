@echo off
rem Runs the new-player gate: a fresh account creates a character, equips, fights, survives a dropped connection and comes back; exit 0 only if every link passes (needs a shard).
rem args: [--list] [--only LINK] [--account NAME] [--fixture-account NAME] [--no-fixture] [scenario runner options]
call "%~dp0..\_shared\common.bat" || exit /b 1
"%UO_PYTHON%" "%UO_TOOLS%\newplayer_gate\run.py" %*
exit /b %ERRORLEVEL%
