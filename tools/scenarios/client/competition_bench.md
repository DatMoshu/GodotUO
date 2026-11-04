# Competition benchmark qualification (L4)

The benchmark is Debug-only with `GuoLabs=true`; default builds and exports must
exclude it. It is an offline fixture, with no shard or account.

1. Run `launchers/dev/competition_bench.bat --doctor` (or `.sh`).
2. Run `python -m pytest tools/competition_bench/test_run.py`. Invalid coverage,
   missing scenes, mismatched controls, noisy repeats and GPU clock drift must
   remain invalid, uncertified or provisional, never certified scores.
3. Build `dotnet build godot/GUO/GUO.csproj -c Debug -p:GuoLabs=true`.
4. Capture classic and enhanced-off twice across all three scenes and both times
   into separate external output folders. `run.py --offparity FIRST SECOND` must
   pass pixel-exactly. Keep a frozen source and DLL hash with these captures.
5. Run the enhanced-off timed baseline, three interleaved rounds across six
   scene/time pairs. Keep the default 20-second wall-clock precondition, 1200
   deterministic warmup frames and 18000 measured frames. Record actual wall
   durations; simulation-frame counts are not wall seconds. Scoring requires the
   pinned common SHA and coordinator DLL hash. The default is one repeat per
   process; use three rounds, not three rounds times three repeats.
6. Verify every GPU validity/coverage gate and zero fallback frames. Any repeat
   with clock drift remains provisional. An under-load or short diagnostic run is
   not a frozen performance baseline. Rebuild default Debug and verify Labs is
   excluded before returning the normal client to the owner.

Full smoke includes synthetic scoring tests only. No competitor launch or
performance claim follows from a passing smoke run. See the runner README for
commands and the output metadata contract in data_formats section 53.
