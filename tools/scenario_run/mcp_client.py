"""A small MCP client for GUO's two loopback servers (the editor's and the running game's).

Both speak newline-delimited JSON-RPC 2.0 after a token line, exactly as tools/editor_mcp/bridge.py and
tools/guo_mcp/bridge.py forward it. The runner talks to the socket directly, so no bridge process runs.
"""

from __future__ import annotations

import json
import socket
import time


class McpError(Exception):
    pass


class McpTimeout(McpError):
    """No reply within the deadline: the watchdog treats it as a hang."""


class McpClient:
    def __init__(self, port: int, token: str, host: str = "127.0.0.1", connect_timeout: float = 10.0):
        self._sock = socket.create_connection((host, port), timeout=connect_timeout)
        self._buf = b""
        self._next_id = 0
        self._sock.sendall(token.encode("utf-8") + b"\n")
        self.request("initialize", {"protocolVersion": "2025-06-18", "capabilities": {},
                                    "clientInfo": {"name": "guo-scenario-run", "version": "0.1.0"}}, timeout=15)
        self._send({"jsonrpc": "2.0", "method": "notifications/initialized"})

    def _send(self, message: dict) -> None:
        self._sock.sendall(json.dumps(message).encode("utf-8") + b"\n")

    def _readline(self, deadline: float) -> dict:
        while b"\n" not in self._buf:
            remaining = deadline - time.monotonic()
            if remaining <= 0:
                raise McpTimeout("no MCP reply before the deadline")
            self._sock.settimeout(remaining)
            try:
                chunk = self._sock.recv(65536)
            except socket.timeout as ex:
                raise McpTimeout("no MCP reply before the deadline") from ex
            if not chunk:
                raise McpError("the MCP server closed the connection")
            self._buf += chunk
        line, self._buf = self._buf.split(b"\n", 1)
        return json.loads(line)

    def request(self, method: str, params: dict | None = None, timeout: float = 45.0) -> dict:
        self._next_id += 1
        mid = self._next_id
        self._send({"jsonrpc": "2.0", "id": mid, "method": method, "params": params or {}})
        deadline = time.monotonic() + timeout
        while True:
            reply = self._readline(deadline)
            if reply.get("id") == mid:
                break
        if "error" in reply:
            raise McpError(f"{method}: {reply['error'].get('message')}")
        return reply["result"]

    def call(self, tool: str, arguments: dict | None = None, timeout: float = 45.0) -> tuple[bool, str]:
        """Calls a tool. Returns (is_error, text); image content is reported as '<image>'."""
        result = self.request("tools/call", {"name": tool, "arguments": arguments or {}}, timeout=timeout)
        text = "\n".join(c.get("text", "<image>") for c in result.get("content", []))
        return bool(result.get("isError")), text

    def call_content(self, tool: str, arguments: dict | None = None, timeout: float = 45.0) -> tuple[bool, list[dict]]:
        """Calls a tool and returns (is_error, the raw content items), for image data."""
        result = self.request("tools/call", {"name": tool, "arguments": arguments or {}}, timeout=timeout)
        return bool(result.get("isError")), result.get("content", [])

    def call_json(self, tool: str, arguments: dict | None = None, timeout: float = 45.0) -> dict:
        is_error, text = self.call(tool, arguments, timeout)
        if is_error:
            raise McpError(f"{tool}: {text[:300]}")
        try:
            return json.loads(text)
        except ValueError as ex:
            raise McpError(f"{tool}: reply is not JSON: {text[:200]}") from ex

    def close(self) -> None:
        try:
            self._sock.close()
        except OSError:
            pass
