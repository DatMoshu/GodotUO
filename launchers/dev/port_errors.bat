@echo off
REM ============================================================================
REM  Every C# error in the whole client, grouped by the symbol that is
REM  missing rather than by call site.
REM
REM  Forces a FULL rebuild (-t:Rebuild) of godot\GUO\GUO.csproj and clusters
REM  what the compiler says (tools\port_errors\run.py). Use it rather than a
REM  plain `dotnet build` when counting errors: an incremental build skips
REM  the compile and reports a handful of errors from a partial pass, which
REM  reads like progress. The same tree measured 9 errors that way and 1,600
REM  on a rebuild.
REM
REM  The staged build (per-area exclusions in GUO.csproj) is retired; the
REM  -p:GuoAllAreas=true property the tool still passes is inert and harmless.
REM  Nothing here changes what gets committed.
REM ============================================================================
call "%~dp0..\_shared\common.bat" || exit /b 1
"%UO_PYTHON%" "%UO_TOOLS%\port_errors\run.py" %*
exit /b %ERRORLEVEL%
