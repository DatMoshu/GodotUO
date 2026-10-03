"""Entry point for the GUO automation MCP.

    python tools/guo_mcp/run.py          stdio MCP bridge to a running GUO
    python tools/guo_mcp/run.py probe    self-test on a synthetic scene [--headed|--client]
"""
import sys
from pathlib import Path

sys.path.insert(0, str(Path(__file__).resolve().parent))

if __name__ == "__main__":
    if len(sys.argv) > 1 and sys.argv[1] == "probe":
        del sys.argv[1]
        import probe
        probe.main()
    else:
        import bridge
        bridge.run()
