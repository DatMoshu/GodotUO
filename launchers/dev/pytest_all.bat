@echo off
REM Runs every Python test folder under tools\ in one pooled pytest run (pytest.ini, conftest.py).
REM Extra arguments go to pytest, e.g. pytest_all.bat -x or pytest_all.bat tools\guo
setlocal
call "%~dp0..\_shared\common.bat" || exit /b 1
pushd "%UO_ROOT%" || exit /b 1
echo [pytest_all] Running the pooled Python tests in %UO_ROOT%
"%UO_PYTHON%" -m pytest %*
set "RC=%ERRORLEVEL%"
popd
if "%RC%"=="0" (echo [pytest_all] OK) else (echo [pytest_all] FAILED)
exit /b %RC%
