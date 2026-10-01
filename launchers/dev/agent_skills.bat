@echo off
call "%~dp0..\_shared\common.bat" || exit /b 1
pushd "%UO_ROOT%"
"%UO_PYTHON%" "%UO_TOOLS%\agent_skills\run.py" %*
set "RESULT=%ERRORLEVEL%"
popd
exit /b %RESULT%
