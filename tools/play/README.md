# Play argument forwarding

`launchers/game/play.bat` passes its original `%*` directly to `run.py`.
Unlike CMD's `%1` / `shift`, this preserves unquoted equals signs in client
switches such as `--gpu-world=off` and `--multimesh-sprites=off`.
The helper only moves `--frames N` to Godot's `--quit-after N` before `--`;
it does not interpret or repair client flags. The Bash launcher already uses
an argument array and needs no helper.

Run `python -m pytest tools/play -q -p no:cacheprovider`. On Windows this
executes the actual batch launcher against synthetic configuration, data-source
and engine stubs, capturing the final argv without launching a game.
