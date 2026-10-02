@echo off
REM ============================================================================
REM  A scripted tour of every GUO editor feature, recorded as an MP4.
REM  Opens the editor window (without taking focus), walks the features with a
REM  caption each, and writes build\editor_tour\<stamp>\editor_tour.mp4 and
REM  summary.md. The live segment uses this checkout's private shard only.
REM
REM    editor_tour.bat               the whole tour
REM    editor_tour.bat --no-live     skip the private shard
REM
REM  See tools\editor_tour\README.md.
REM ============================================================================
call "%~dp0..\_shared\common.bat" || exit /b 1
"%UO_PYTHON%" "%UO_TOOLS%\editor_tour\run.py" %*
exit /b %ERRORLEVEL%
