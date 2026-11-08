@echo off
rem Launches the injected ClassicUO unfocused with its automation MCP and drives it through login, a [go and a screenshot (proof), or runs the tool self-test (selftest); needs the shard for proof.
rem args: proof [--build] [--run-dir DIR] [--size W,H] | selftest
REM ============================================================================
REM  Test-only: GUO test tooling driving stock ClassicUO through the same MCP
REM  tools as GUO (tools\cuo_automation). Needs launchers\shard\run.bat going;
REM  the window is never focused and nothing is typed on the desktop.
REM ============================================================================
call "%~dp0..\_shared\common.bat" || exit /b 1

"%UO_PYTHON%" "%UO_ROOT%\tools\cuo_automation\run.py" %*
exit /b %ERRORLEVEL%
