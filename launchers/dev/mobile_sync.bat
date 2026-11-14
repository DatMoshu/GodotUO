@echo off
rem Tests two GUO clients for movement visibility and pending atlas uploads on the local dev shard.
call "%~dp0..\_shared\common.bat" || exit /b 1
"%UO_PYTHON%" "%UO_ROOT%\tools\mobile_sync\run.py" %*
exit /b %ERRORLEVEL%
