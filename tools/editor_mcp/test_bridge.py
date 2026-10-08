"""Bounded framing and an actual stdio-to-loopback round trip; no provider login."""
from __future__ import annotations

import io
import os
from pathlib import Path
import socket
import subprocess
import sys
import threading
import unittest

from bridge import forward


class BridgeTests(unittest.TestCase):
    def test_forward_preserves_json_lines(self):
        source = io.BytesIO(b'{"id":1}\n{"id":2}\n')
        target = io.BytesIO()
        forward(source, target)
        self.assertEqual(target.getvalue(), source.getvalue())

    def test_forward_refuses_oversized_or_unterminated_line(self):
        for data in (b'x' * 65536 + b'\n', b'{"id":1}'):
            with self.subTest(length=len(data)), self.assertRaises(ValueError):
                forward(io.BytesIO(data), io.BytesIO())

    def test_response_limit_allows_larger_bounded_utf8_results(self):
        payload = b'x' * 70000 + b'\n'
        output = io.BytesIO()
        forward(io.BytesIO(payload), output, max_bytes=1048576)
        self.assertEqual(output.getvalue(), payload)

    def test_stdio_loopback_round_trip_keeps_token_off_stdout(self):
        token = "editor-bridge-test-secret-" + "x" * 32
        listener = socket.socket()
        listener.bind(("127.0.0.1", 0))
        listener.listen(1)
        listener.settimeout(5)
        received = []
        def serve():
            with listener:
                with listener.accept()[0] as client:
                    client.settimeout(5)
                    with client.makefile("rb") as reader:
                        received.append(reader.readline())
                        received.append(reader.readline())
                        client.sendall(b'{"jsonrpc":"2.0","id":1,"result":{}}\n')
        worker = threading.Thread(target=serve)
        worker.start()
        env = {**os.environ, "GUO_EDITOR_MCP_PORT": str(listener.getsockname()[1]), "GUO_EDITOR_MCP_TOKEN": token}
        proc = subprocess.Popen([sys.executable, str(Path(__file__).with_name("bridge.py"))], env=env,
                                stdin=subprocess.PIPE, stdout=subprocess.PIPE, stderr=subprocess.PIPE)
        request = b'{"jsonrpc":"2.0","id":1,"method":"ping"}\n'
        try:
            proc.stdin.write(request)
            proc.stdin.flush()
            # The server closes its socket and the receive thread ends the bridge.
            proc.wait(timeout=8)
            output = proc.stdout.read()
            self.assertEqual(output, b'{"jsonrpc":"2.0","id":1,"result":{}}\n')
            self.assertNotIn(token.encode(), output + proc.stderr.read())
            self.assertEqual(received, [token.encode() + b'\n', request])
        finally:
            if proc.poll() is None:
                proc.kill()
                proc.wait(timeout=5)
            for pipe in (proc.stdin, proc.stdout, proc.stderr):
                pipe.close()
            worker.join(timeout=5)


if __name__ == "__main__":
    unittest.main()
