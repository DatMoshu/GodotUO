@echo off
rem Downloads Pixelorama's pinned source and release build into tools/pixelorama (gitignored) and prints what it fetched.
rem args: --source | --binary
REM ============================================================================
REM  Fetch Pixelorama (MIT, ADR-0029): the source from the GUO fork at the
REM  pinned tag into tools\pixelorama\src, and the release build into
REM  tools\pixelorama\bin. Both are gitignored.
REM
REM      fetch_pixelorama.bat              both
REM      fetch_pixelorama.bat --source     only the source
REM      fetch_pixelorama.bat --binary     only the release build
REM ============================================================================
call "%~dp0..\_shared\common.bat" || exit /b 1
"%UO_PYTHON%" "%UO_TOOLS%\pixelorama\run.py" fetch %*
exit /b %ERRORLEVEL%
