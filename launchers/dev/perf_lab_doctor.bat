@echo off
rem Checks what the perf lab needs (.NET, Godot, the UO client data, a running shard) and says how to fix what is missing; measures nothing.
call "%~dp0..\_shared\common.bat" || exit /b 1
"%UO_PYTHON%" "%UO_TOOLS%\perf_lab\run.py" --doctor %*
exit /b %ERRORLEVEL%
