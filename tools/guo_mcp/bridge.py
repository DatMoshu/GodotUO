"""Stdio MCP transport for the embedded GUO server (Python stdlib only)."""
import os
import socket
import sys
import threading


def main():
    port = int(os.environ.get("GUO_MCP_PORT", "0"))
    token = os.environ.get("GUO_MCP_TOKEN", "")
    if not 1024 <= port <= 65535 or len(token) < 32 or "\n" in token or "\r" in token:
        raise ValueError("Set GUO_MCP_PORT (1024..65535) and GUO_MCP_TOKEN (32+ characters).")
    with socket.create_connection(("127.0.0.1", port), timeout=10) as connection:
        connection.settimeout(None)
        connection.sendall(token.encode("utf-8") + b"\n")

        def receive():
            try:
                with connection.makefile("rb") as incoming:
                    for line in incoming:
                        sys.stdout.buffer.write(line)
                        sys.stdout.buffer.flush()
            finally:
                # stdin may be blocked after GUO exits. End the bridge as well.
                os._exit(0)

        threading.Thread(target=receive, daemon=True).start()
        for line in sys.stdin.buffer:
            if len(line) > 65536:
                raise ValueError("MCP request exceeds 64 KiB.")
            connection.sendall(line)


def run():
    try:
        main()
    except (OSError, ValueError) as error:
        print(f"GUO MCP bridge: {error}", file=sys.stderr)
        sys.exit(1)


if __name__ == "__main__":
    run()
