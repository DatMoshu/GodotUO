@echo off
rem Opens the local GUO Asset Store page (UO_STORE_URL) in your browser, which detaches (start) so this returns at once.
call "%~dp0..\_shared\common.bat" || exit /b 1
pushd "%UO_ROOT%" || exit /b 1
start "GUO Asset Store" "%UO_STORE_URL%"
set "STORE_EXIT=%ERRORLEVEL%"
popd
exit /b %STORE_EXIT%
