@echo off
rem Measures how far ported files have drifted from their upstream originals and prints a report.
rem args: --strict
REM ============================================================================
REM  Measure how far ported files have drifted from their upstream originals.
REM  See tools\port_drift\README.md.
REM
REM      launchers\dev\port_drift.bat
REM      launchers\dev\port_drift.bat --strict
REM ============================================================================
call "%~dp0..\_shared\common.bat" || exit /b 1

"%UO_PYTHON%" "%UO_ROOT%\tools\port_drift\run.py" %*
exit /b %ERRORLEVEL%
