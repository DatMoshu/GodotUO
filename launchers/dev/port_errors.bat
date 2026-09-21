@echo off
REM ============================================================================
REM  How much of the port is actually left.
REM
REM  GUO.csproj excludes the areas that do not compile yet, so the ordinary
REM  build says nothing about the remaining work. This turns every exclusion
REM  off at once (-p:GuoAllAreas=true), forces a FULL rebuild, and groups the
REM  errors by the symbol that is missing rather than by call site.
REM
REM  Use this rather than stripping the exclusions by hand and running
REM  `dotnet build`: an incremental build skips the compile and reports a
REM  handful of errors from a partial pass, which reads like progress. The
REM  same tree measured 9 errors that way and 1,600 on a rebuild.
REM
REM  This never changes what gets committed -- the switch is a build property.
REM ============================================================================
call "%~dp0..\_shared\common.bat" || exit /b 1
"%UO_PYTHON%" "%UO_TOOLS%\port_errors\run.py" %*
exit /b %ERRORLEVEL%
