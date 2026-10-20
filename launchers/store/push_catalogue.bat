@echo off
rem Signs a catalogue index over a GitHub Pages branch and pushes it, fast-forward only (needs UO_STORE_SIGNING_KEY).
call "%~dp0..\_shared\common.bat" || exit /b 1
pushd "%UO_ROOT%" || exit /b 1
"%UO_PYTHON%" "%UO_TOOLS%\asset_store\run.py" push-catalogue %*
set "STORE_EXIT=%ERRORLEVEL%"
popd
exit /b %STORE_EXIT%
