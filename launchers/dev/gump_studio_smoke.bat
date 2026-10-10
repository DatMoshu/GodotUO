@echo off
rem Builds the project and checks Gump Studio in a headless editor, printing pass or fail per check.
rem args: --reload | --windowed
call "%~dp0..\_shared\common.bat" || exit /b 1
"%UO_PYTHON%" "%UO_TOOLS%\gump_studio_smoke\run.py" %*
exit /b %ERRORLEVEL%
