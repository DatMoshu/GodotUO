@echo off
rem Runs the benchmark tour (GUO plain, defaults and UO+ max against ClassicUO at the same spots and zooms) and writes tour.md, tour.json and tour.png under build/perf_tour/<label>.
rem args: [--label <name>] [--preset gate|community|far] [--mode plain|defaults|uoplus-max] [--scene <name>] [--no-cuo] [--as-built] | --report <dir>
REM ============================================================================
REM  The benchmark tour (perf lane): every number a frame-rate claim needs in
REM  one run. GUO through the perf probe in three modes (plain = what ClassicUO
REM  draws, defaults, uoplus-max) and ClassicUO through its own profiler at
REM  the same [go spots, at three window/zoom presets. Needs a shard where the
REM  probe account is a GM, and the Godot runtime lease --benchmark --exclusive.
REM      launchers\dev\perf_tour.bat --label before
REM      launchers\dev\perf_tour.bat --preset far --mode plain --no-cuo
REM      launchers\dev\perf_tour.bat --report build\perf_tour\before
REM ============================================================================
call "%~dp0..\_shared\common.bat" || exit /b 1
"%UO_PYTHON%" "%UO_TOOLS%\perf_tour\run.py" %*
exit /b %ERRORLEVEL%
