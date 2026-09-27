"""Starting GUO's Godot processes from tools without disturbing whoever is at the machine.

A window a tool opens (an editor for screenshots, a client for a parity
shot) must not take the keyboard or the foreground from the person working.
Two layers do that:

- here, at launch: Windows is asked to show the new process's first window
  without activating it (STARTUPINFO wShowWindow = SW_SHOWNOACTIVATE);
- in the process itself: scripted game runs keep their window NoFocus
  (Bootstrap/Main.cs), and a tool-launched editor does the same for its own
  window (addons/guo_editor/GuoEditorPlugin.cs).

Headless runs open no window at all, and are what tools use by default.
"""

from __future__ import annotations

import subprocess
import sys

SW_SHOWNOACTIVATE = 4


def no_activate() -> dict:
    """Keyword arguments for subprocess.Popen/run: show the first window without activating it (Windows)."""
    if sys.platform != "win32":
        return {}
    info = subprocess.STARTUPINFO()
    info.dwFlags |= subprocess.STARTF_USESHOWWINDOW
    info.wShowWindow = SW_SHOWNOACTIVATE
    return {"startupinfo": info}
