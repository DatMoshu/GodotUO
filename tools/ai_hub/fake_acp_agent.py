#!/usr/bin/env python3
"""A tiny fake ACP agent, for tests (ADR-0028). It spends nothing and logs into nothing.

Speaks the Agent Client Protocol over stdio the way a real agent does: JSON-RPC 2.0,
one JSON message per line.

    initialize          -> protocolVersion 1, agentInfo, no auth methods
    session/new         -> sessionId "fake-1"
    session/prompt      -> streams session/update notifications, then {"stopReason": "end_turn"}
    session/cancel      -> (notification) the running turn ends with "cancelled"

What a prompt does depends on its text:
    contains "permission"  the agent sends session/request_permission and reports the outcome
    contains "slow"        it streams for a long while (30 s), for cancel and timeout tests
    contains "noreply"     it never answers the prompt, for timeout tests
    anything else          it streams "echo: <text>" in word chunks, after a thought and a tool call

Options:
    --banner   print a non-JSON line on stdout first (a client must skip it)
    --silent   never answer anything (the client's handshake must time out)
    --no-session-new   answer session/new with an error (-32000 auth_required)
"""

from __future__ import annotations

import json
import sys
import time


def send(msg: dict) -> None:
    sys.stdout.write(json.dumps(msg, ensure_ascii=False) + "\n")
    sys.stdout.flush()


def update(session: str, what: str, **fields) -> None:
    send({"jsonrpc": "2.0", "method": "session/update",
          "params": {"sessionId": session, "update": {"sessionUpdate": what, **fields}}})


def text_block(text: str) -> dict:
    return {"type": "text", "text": text}


class Agent:
    def __init__(self, args: list[str]) -> None:
        self.silent = "--silent" in args
        self.no_session = "--no-session-new" in args
        self.cancelled = False
        self.backlog: list[dict] = []
        self.next_id = 1000

    def read(self) -> dict | None:
        if self.backlog:
            return self.backlog.pop(0)
        line = sys.stdin.readline()
        if not line:
            return None
        line = line.strip()
        return json.loads(line) if line else {}

    def run(self) -> None:
        while True:
            msg = self.read()
            if msg is None:
                return
            if msg:
                self.handle(msg)

    def handle(self, msg: dict) -> None:
        method, mid = msg.get("method"), msg.get("id")
        if self.silent:
            return
        if method == "initialize":
            send({"jsonrpc": "2.0", "id": mid, "result": {
                "protocolVersion": 1,
                "agentCapabilities": {"loadSession": False, "promptCapabilities": {}},
                "agentInfo": {"name": "fake-acp-agent", "title": "Fake ACP agent", "version": "0.1"},
                "authMethods": []}})
        elif method == "session/new":
            if self.no_session:
                send({"jsonrpc": "2.0", "id": mid, "error": {"code": -32000, "message": "auth_required: sign in first"}})
            else:
                send({"jsonrpc": "2.0", "id": mid, "result": {"sessionId": "fake-1"}})
        elif method == "session/prompt":
            self.prompt(mid, msg["params"])
        elif method == "session/cancel":
            self.cancelled = True
        elif mid is not None:
            send({"jsonrpc": "2.0", "id": mid, "error": {"code": -32601, "message": f"method not found: {method}"}})

    def poll_cancel(self) -> bool:
        """Between chunks: take any waiting message (a cancel) without blocking forever."""
        return self.cancelled

    def prompt(self, mid, params: dict) -> None:
        self.cancelled = False
        session = params["sessionId"]
        text = " ".join(b.get("text", "") for b in params.get("prompt", []) if b.get("type") == "text")
        if "noreply" in text:
            return
        if "slow" in text:
            update(session, "agent_message_chunk", content=text_block("working"))
            # Wait for a cancel (or 30 s), reading stdin like a real agent's event loop.
            deadline = time.monotonic() + 30
            while time.monotonic() < deadline and not self.cancelled:
                m = self.read()
                if m is None:
                    return
                if m.get("method") == "session/cancel":
                    self.cancelled = True
            send({"jsonrpc": "2.0", "id": mid, "result": {"stopReason": "cancelled" if self.cancelled else "end_turn"}})
            return

        update(session, "agent_thought_chunk", content=text_block("thinking about it"))
        update(session, "tool_call", toolCallId="t1", title="Read notes.txt", kind="read", status="pending")
        update(session, "tool_call_update", toolCallId="t1", status="completed")

        if "permission" in text:
            self.next_id += 1
            rid = self.next_id
            send({"jsonrpc": "2.0", "id": rid, "method": "session/request_permission", "params": {
                "sessionId": session,
                "toolCall": {"toolCallId": "t2", "title": "Edit notes.txt", "kind": "edit", "status": "pending",
                             "rawInput": {"path": "notes.txt"}},
                "options": [
                    {"optionId": "allow-always", "name": "Always allow", "kind": "allow_always"},
                    {"optionId": "allow-once", "name": "Allow once", "kind": "allow_once"},
                    {"optionId": "reject-once", "name": "Reject", "kind": "reject_once"}]}})
            while True:
                m = self.read()
                if m is None:
                    return
                if m.get("id") == rid and "method" not in m:
                    outcome = (m.get("result") or {}).get("outcome", {})
                    update(session, "agent_message_chunk",
                           content=text_block(f"permission outcome: {outcome.get('outcome')} {outcome.get('optionId', '')}".strip()))
                    break
        else:
            words = f"echo: {text}".split(" ")
            for i, w in enumerate(words):
                update(session, "agent_message_chunk", content=text_block(w + (" " if i < len(words) - 1 else "")))

        send({"jsonrpc": "2.0", "id": mid, "result": {"stopReason": "end_turn"}})


def main() -> int:
    args = sys.argv[1:]
    sys.stdin.reconfigure(encoding="utf-8")
    sys.stdout.reconfigure(encoding="utf-8")
    if "--banner" in args:
        sys.stdout.write("fake-acp-agent starting (this line is not JSON)\n")
        sys.stdout.flush()
    Agent(args).run()
    return 0


if __name__ == "__main__":
    sys.exit(main())
