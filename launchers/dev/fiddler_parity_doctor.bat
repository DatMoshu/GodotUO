@echo off
rem Checks or builds the independent static-art reader used to verify Fiddler-parity staged output.
call "%~dp0..\_shared\common.bat" || exit /b 1
python "%UO_TOOLS%\fiddler_parity\run.py" doctor %*
exit /b %errorlevel%
