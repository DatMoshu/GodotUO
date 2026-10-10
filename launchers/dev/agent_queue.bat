@echo off
rem Posts to, tails, replies on or lists the editor's agent request queue and prints the messages.
rem args: post|tail|reply|list ...
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
