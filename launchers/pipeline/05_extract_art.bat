@echo off
REM ============================================================================
REM  PIPELINE STEP 05 - extract your install's art into a local art set.
REM  Optional. Writes atlas pages (land, static art, gumps, texmaps, lights,
REM  animations) into UO_ART_EXTRACT_DIR, then re-reads the install and checks
REM  every image against the pages. The install is never written. The set is
REM  your own data: local, gitignored, never committed or bundled. Turn it on
REM  with  play.bat --art-set  (or UO_ART_SET=1); delete the folder to remove it.
REM
REM    05_extract_art.bat                     export everything, then verify
REM    05_extract_art.bat --what art,gumps    only those classes
REM    05_extract_art.bat --from DIR          another client folder
REM
REM  See docs\art_extract.md and tools\art_extract\README.md.
REM ============================================================================
call "%~dp0..\_shared\common.bat" || exit /b 1
"%UO_PYTHON%" "%UO_TOOLS%\art_extract\run.py" export %* || exit /b 1
"%UO_PYTHON%" "%UO_TOOLS%\art_extract\run.py" verify %*
exit /b %ERRORLEVEL%
