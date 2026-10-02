@echo off
REM ============================================================================
REM  The agent request queue: post, tail, reply, list. See
REM  tools\agent_queue\README.md.
REM
REM      launchers\dev\agent_queue.bat post --to claude --from chat "hello"
REM      launchers\dev\agent_queue.bat tail --as claude
REM ============================================================================
call "%~dp0..\_shared\common.bat" || exit /b 1

"%UO_PYTHON%" "%UO_ROOT%\tools\agent_queue\run.py" %*
exit /b %ERRORLEVEL%
