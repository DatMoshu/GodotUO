"""Windows play launcher: preserve argv and move --frames to Godot's side."""
from __future__ import annotations

import subprocess
import sys


def command(argv: list[str]) -> list[str]:
    separator = argv.index("--")
    engine = argv[:separator]
    client = []
    quit_after = []
    args = iter(argv[separator + 1:])
    for arg in args:
        if arg.lower() == "--frames":
            value = next(args, None)
            if value is None:
                raise ValueError("--frames requires a frame count")
            quit_after = ["--quit-after", value]
        else:
            client.append(arg)
    # play.bat supplies executable, --path and project first. Keep --frames
    # before GUO_ENGINE_ARGS, matching the original launcher's precedence.
    return [*engine[:3], *quit_after, *engine[3:], "--", *client]


def main(argv: list[str]) -> int:
    try:
        return subprocess.call(command(argv))
    except (ValueError, OSError) as error:
        print(f"[play] FATAL: {error}", file=sys.stderr)
        return 1


if __name__ == "__main__":
    sys.exit(main(sys.argv[1:]))
