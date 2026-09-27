@echo off
call "%~dp0..\_shared\common.bat" || exit /b 1
pushd "%UO_ROOT%" || exit /b 1
"%UO_PYTHON%" "%UO_TOOLS%\asset_store\run.py" serve %*
set "STORE_EXIT=%ERRORLEVEL%"
popd
exit /b %STORE_EXIT%
