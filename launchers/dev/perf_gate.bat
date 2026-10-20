@echo off
rem Runs the real performance probe against a machine-local baseline; --record-baseline records it explicitly and --doctor checks prerequisites.
call "%~dp0..\_shared\common.bat" || exit /b 1
"%UO_PYTHON%" "%UO_TOOLS%\perf_probe\local_gate.py" %*
exit /b %ERRORLEVEL%
