@echo off
REM ============================================================================
REM  PIPELINE STEP 01 - verify the UO client data the runtime will read.
REM  Checks every required .mul/.uop/.idx is present, detects the client
REM  version, and writes a manifest the runtime and the port audit both read.
REM  Reads only; never writes into the UO install.
REM ============================================================================
call "%~dp0..\_shared\common.bat" || exit /b 1
"%UO_PYTHON%" "%UO_TOOLS%\uodata\run.py" verify ^
    --data-dir "%UO_CLIENT_DATA%" ^
    --client-version "%UO_CLIENT_VERSION%" ^
    --out "%UO_BUILD%\client_manifest.json" %*
exit /b %ERRORLEVEL%
