"""Stdio MCP bridge to the running GUO editor (stdlib only).

Configure GUO_EDITOR_MCP_PORT and GUO_EDITOR_MCP_TOKEN for both processes.
The token is sent only to the loopback socket, never stdout or stderr.
"""
from __future__ import annotations

import os
import socket
import sys
import threading


def forward(source, destination, max_bytes=65536):
    while True:
        line = source.readline(max_bytes + 1)
        if not line:
            return
        if len(line) > max_bytes or not line.endswith(b"\n"):
            raise ValueError("MCP message exceeds its limit or has incomplete framing")
        destination.write(line)
        destination.flush()


def main():
    port = int(os.environ.get("GUO_EDITOR_MCP_PORT", "0"))
    token = os.environ.get("GUO_EDITOR_MCP_TOKEN", "")
    if not 1024 <= port <= 65535 or not 32 <= len(token) <= 256 or "\n" in token or "\r" in token:
        raise ValueError("Set editor MCP port (1024..65535) and a single-line token (32..256 characters)")
    with socket.create_connection(("127.0.0.1", port), timeout=10) as connection:
        connection.settimeout(None)
        connection.sendall(token.encode("utf-8") + b"\n")
        def receive():
            try:
                with connection.makefile("rb") as incoming:
                    # A bounded tool catalog/result can exceed the request limit after UTF-8/JSON escaping.
                    forward(incoming, sys.stdout.buffer, max_bytes=1048576)
            finally:
                os._exit(0)
        threading.Thread(target=receive, daemon=True).start()
        with connection.makefile("wb") as outgoing:
            forward(sys.stdin.buffer, outgoing)


if __name__ == "__main__":
    try:
        main()
    except (OSError, ValueError):
        print("GUO editor MCP: connection or framing failed; check editor, port and token", file=sys.stderr)
        sys.exit(1)
