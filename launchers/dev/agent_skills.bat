@echo off
rem Links .agents/skills to .claude/skills so Codex finds the project skills, and prints what it linked.
call "%~dp0..\_shared\common.bat" || exit /b 1
pushd "%UO_ROOT%"
"%UO_PYTHON%" "%UO_TOOLS%\agent_skills\run.py" %*
set "RESULT=%ERRORLEVEL%"
popd
exit /b %RESULT%
