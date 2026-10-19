@echo off
rem Proves a hosted catalogue on this computer: signs and pushes to a local Pages branch, serves it, and installs from it with the client's C#.
call "%~dp0..\_shared\common.bat" || exit /b 1
pushd "%UO_ROOT%" || exit /b 1
"%UO_PYTHON%" "%UO_TOOLS%\asset_store\hosted_proof.py" %*
set "STORE_EXIT=%ERRORLEVEL%"
popd
exit /b %STORE_EXIT%
