@echo off
REM ============================================================================
REM  What is fetched and installed of Pixelorama, and where the exchange folder
REM  is. Add "check" instead to parse the extension against Pixelorama's source.
REM ============================================================================
call "%~dp0..\_shared\common.bat" || exit /b 1
"%UO_PYTHON%" "%UO_TOOLS%\pixelorama\run.py" status
exit /b %ERRORLEVEL%
