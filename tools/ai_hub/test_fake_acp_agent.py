"""The fake ACP agent's framing: initialize, session/new, prompt with streamed updates, permission, cancel."""
import json
import subprocess
import sys
import unittest
from pathlib import Path

AGENT = Path(__file__).resolve().parent / "fake_acp_agent.py"


class Agent:
    def __init__(self, *flags):
        self.p = subprocess.Popen([sys.executable, str(AGENT), *flags], stdin=subprocess.PIPE,
                                  stdout=subprocess.PIPE, text=True, encoding="utf-8")

    def send(self, **msg):
        self.p.stdin.write(json.dumps({"jsonrpc": "2.0", **msg}) + "\n")
        self.p.stdin.flush()

    def read(self):
        while True:
            line = self.p.stdout.readline()
            if not line:
                return None
            try:
                return json.loads(line)
            except json.JSONDecodeError:
                continue  # the banner

    def close(self):
        self.p.kill()
        self.p.wait()
        self.p.stdin.close()
        self.p.stdout.close()


class FakeAgentTests(unittest.TestCase):
    def start(self, *flags):
        a = Agent(*flags)
        self.addCleanup(a.close)
        return a

    def handshake(self, a):
        a.send(id=1, method="initialize", params={"protocolVersion": 1})
        self.assertEqual(a.read()["result"]["protocolVersion"], 1)
        a.send(id=2, method="session/new", params={"cwd": ".", "mcpServers": []})
        return a.read()["result"]["sessionId"]

    def test_prompt_streams_then_ends(self):
        a = self.start("--banner")
        sid = self.handshake(a)
        a.send(id=3, method="session/prompt", params={"sessionId": sid, "prompt": [{"type": "text", "text": "hello there"}]})
        text, kinds = "", []
        while True:
            m = a.read()
            if m.get("id") == 3:
                self.assertEqual(m["result"]["stopReason"], "end_turn")
                break
            u = m["params"]["update"]
            kinds.append(u["sessionUpdate"])
            if u["sessionUpdate"] == "agent_message_chunk":
                text += u["content"]["text"]
        self.assertEqual(text, "echo: hello there")
        self.assertIn("tool_call", kinds)

    def test_permission_round_trip(self):
        a = self.start()
        sid = self.handshake(a)
        a.send(id=3, method="session/prompt", params={"sessionId": sid, "prompt": [{"type": "text", "text": "permission please"}]})
        while True:
            m = a.read()
            if m.get("method") == "session/request_permission":
                a.send(id=m["id"], result={"outcome": {"outcome": "selected", "optionId": "allow-once"}})
            elif m.get("id") == 3:
                break
            elif m["params"]["update"]["sessionUpdate"] == "agent_message_chunk":
                self.assertIn("selected allow-once", m["params"]["update"]["content"]["text"])

    def test_cancel_ends_slow_turn(self):
        a = self.start()
        sid = self.handshake(a)
        a.send(id=3, method="session/prompt", params={"sessionId": sid, "prompt": [{"type": "text", "text": "slow one"}]})
        a.read()
        a.send(method="session/cancel", params={"sessionId": sid})
        self.assertEqual(a.read()["result"]["stopReason"], "cancelled")

    def test_unknown_method_is_an_error(self):
        a = self.start()
        a.send(id=9, method="fs/nonsense", params={})
        self.assertEqual(a.read()["error"]["code"], -32601)


if __name__ == "__main__":
    unittest.main()
