@echo off
rem Runs the frozen competition GPU benchmark (Labs CompetitionBench probe): timed or capture, interleaved, scored against the frozen common baseline.
rem args: --label L --out EVIDENCE_DIR [--builds A=path ...] [--variants ...] [--mode timed|capture] [--baseline DIR]
call "%~dp0..\_shared\common.bat" || exit /b 1
"%UO_PYTHON%" "%UO_TOOLS%\competition_bench\run.py" %*
exit /b %ERRORLEVEL%
