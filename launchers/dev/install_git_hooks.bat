@echo off
call "%~dp0..\_shared\common.bat" || exit /b 1
pushd "%UO_ROOT%"
"%UO_PYTHON%" "%UO_TOOLS%\git_hooks\install.py" %*
set "RESULT=%ERRORLEVEL%"
popd
exit /b %RESULT%
