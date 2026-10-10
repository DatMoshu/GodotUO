@echo off
rem Exports the editor's world project into patched map and statics files under <project>/export, then reads them back to check them.
rem args: --project <dir>
REM ============================================================================
REM  PIPELINE STEP 04 - export the world project.
REM  Turns the editor's map edits (UO_WORLD_PROJECT) into patched copies of
REM  the map, staidx and statics files in <project>\export, then reads them
REM  back to check them. The install is never written. Point a server at the
REM  export by listing it first in its data directories; point the client at
REM  it with the files_override.txt written beside it.
REM
REM    04_world_export.bat                    export UO_WORLD_PROJECT
REM    04_world_export.bat --project DIR      another project
REM
REM  See tools\world\run.py and docs\data_formats.md section 9.
REM ============================================================================
call "%~dp0..\_shared\common.bat" || exit /b 1
"%UO_PYTHON%" "%UO_TOOLS%\world\run.py" export %* || exit /b 1
"%UO_PYTHON%" "%UO_TOOLS%\world\run.py" verify %*
exit /b %ERRORLEVEL%
