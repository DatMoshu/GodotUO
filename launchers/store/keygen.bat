@echo off
rem Makes the catalogue's signing key: writes the secret key file and prints
rem the public key and its fingerprint. Run once. It never overwrites a key.
rem   keygen.bat                 writes to UO_STORE_SIGNING_KEY (config.local.bat)
rem   keygen.bat PATH\to\x.key   writes there instead
call "%~dp0..\_shared\common.bat" || goto :fail
set "KEY_FILE=%~1"
if "%KEY_FILE%"=="" set "KEY_FILE=%UO_STORE_SIGNING_KEY%"
if "%KEY_FILE%"=="" (
  echo Give the key file's path, e.g.  keygen.bat D:\backups\catalogue\guo-official.key
  echo or set UO_STORE_SIGNING_KEY in launchers\_shared\config.local.bat.
  goto :fail
)
if exist "%KEY_FILE%" (
  echo %KEY_FILE% already exists. Keygen never overwrites a key.
  goto :fail
)
for %%D in ("%KEY_FILE%") do if not exist "%%~dpD" mkdir "%%~dpD"
pushd "%UO_ROOT%" || goto :fail
"%UO_PYTHON%" "%UO_TOOLS%\asset_store\run.py" keygen "%KEY_FILE%"
set "STORE_EXIT=%ERRORLEVEL%"
popd
echo.
echo Send the Public key and Fingerprint lines (never the key file).
pause
exit /b %STORE_EXIT%
:fail
pause
exit /b 1
