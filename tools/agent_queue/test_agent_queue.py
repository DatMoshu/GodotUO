"""Agent request queue: post, tail, reply, resume, races, broadcast, cancel, kill."""
import json
import subprocess
import sys
import tempfile
import time
import unittest
from pathlib import Path

RUN = Path(__file__).resolve().parent / "run.py"


class QueueTests(unittest.TestCase):
    def setUp(self):
        self.temp = tempfile.TemporaryDirectory(prefix="guo-queue-test-")
        self.addCleanup(self.temp.cleanup)
        self.db = str(Path(self.temp.name) / "sub" / "q.db")
        self.procs = []
        self.addCleanup(self.reap)

    def reap(self):
        for proc in self.procs:
            if proc.poll() is None:
                proc.kill()
            proc.wait()
            proc.stdout.close()
            proc.stderr.close()

    def run_cli(self, *args, expect=0):
        done = subprocess.run([sys.executable, str(RUN), "--db", self.db, *args],
                              capture_output=True, text=True, encoding="utf-8", timeout=60)
        self.assertEqual(done.returncode, expect, done.stderr)
        return done.stdout.strip()

    def spawn(self, *args):
        proc = subprocess.Popen([sys.executable, str(RUN), "--db", self.db, *args],
                                stdout=subprocess.PIPE, stderr=subprocess.PIPE, text=True, encoding="utf-8")
        self.procs.append(proc)
        return proc

    def post(self, to, text="hello", sender="chat"):
        return int(self.run_cli("post", "--to", to, "--from", sender, text))

    def lines(self, text):
        return [json.loads(line) for line in text.splitlines() if line.strip()]

    def test_post_tail_reply(self):
        rid = self.post("alpha", "do it\nnow", sender="chat")
        self.post("beta", "not yours")
        got = self.lines(self.run_cli("tail", "--as", "alpha", "--once", "--timeout", "10"))
        self.assertEqual([g["id"] for g in got], [rid])
        self.assertEqual(got[0]["text"], "do it\nnow")
        self.assertEqual(got[0]["status"], "taken")
        self.run_cli("reply", str(rid), "done", "--from", "alpha")
        shown = json.loads(self.run_cli("show", str(rid)))
        self.assertEqual(shown["status"], "answered")
        self.assertEqual(shown["replies"][0]["text"], "done")
        replies = self.lines(self.run_cli("watch-replies", str(rid), "--once", "--timeout", "10"))
        self.assertEqual(replies[0]["request_id"], rid)

    def test_offset_resume_no_loss_no_duplicates(self):
        first = [self.post("alpha", f"r{i}") for i in range(3)]
        seen = [g["id"] for g in self.lines(self.run_cli("tail", "--as", "alpha", "--once", "--timeout", "10"))]
        later = [self.post("alpha", f"s{i}") for i in range(2)]
        seen += [g["id"] for g in self.lines(self.run_cli("tail", "--as", "alpha", "--once", "--timeout", "10"))]
        self.assertEqual(seen, first + later)
        self.run_cli("tail", "--as", "alpha", "--once", "--timeout", "1", expect=3)

    def test_two_tailers_never_take_the_same_request(self):
        ids = [self.post("alpha", f"r{i}") for i in range(40)]
        a = self.spawn("tail", "--as", "alpha", "--interval", "0.05", "--timeout", "3")
        b = self.spawn("tail", "--as", "alpha", "--interval", "0.05", "--timeout", "3")
        out_a, _ = a.communicate(timeout=30)
        out_b, _ = b.communicate(timeout=30)
        got_a = [g["id"] for g in self.lines(out_a)]
        got_b = [g["id"] for g in self.lines(out_b)]
        self.assertEqual(sorted(got_a + got_b), ids)
        self.assertFalse(set(got_a) & set(got_b))

    def test_broadcast(self):
        rid = self.post("*", "everyone")
        self.assertEqual(self.lines(self.run_cli("tail", "--as", "alpha", "--once", "--timeout", "1", expect=3)), [])
        got = self.lines(self.run_cli("tail", "--as", "alpha", "--include-broadcast", "--once", "--timeout", "10"))
        self.assertEqual(got[0]["id"], rid)
        self.run_cli("tail", "--as", "beta", "--include-broadcast", "--once", "--timeout", "1", expect=3)

    def test_cancel(self):
        rid = self.post("alpha")
        self.run_cli("cancel", str(rid))
        self.run_cli("tail", "--as", "alpha", "--once", "--timeout", "1", expect=3)
        self.run_cli("reply", str(rid), "late", "--from", "alpha", expect=1)
        self.run_cli("cancel", str(rid), expect=1)
        done = self.post("alpha")
        self.run_cli("tail", "--as", "alpha", "--once", "--timeout", "10")
        self.run_cli("reply", str(done), "ok")
        self.run_cli("cancel", str(done), expect=1)
        self.assertIn("cancelled", self.run_cli("list", "--status", "cancelled"))

    def test_killed_tail_then_restart(self):
        before = [self.post("alpha", f"a{i}") for i in range(3)]
        proc = self.spawn("tail", "--as", "alpha", "--interval", "0.05")
        got = [json.loads(proc.stdout.readline())["id"] for _ in before]
        proc.kill()
        proc.wait()
        after = [self.post("alpha", f"b{i}") for i in range(3)]
        out = self.run_cli("tail", "--as", "alpha", "--once", "--timeout", "10")
        got += [g["id"] for g in self.lines(out)]
        self.assertEqual(got, before + after)
        # A session that died holding an unshown request can ask for it again.
        replay = self.lines(self.run_cli("tail", "--as", "alpha", "--once", "--replay-taken", "--timeout", "10"))
        self.assertEqual([r["id"] for r in replay], before + after)

    def test_validation(self):
        self.run_cli("post", "--to", "bad name", "--from", "chat", "x", expect=2)
        self.run_cli("post", "--to", "a", "--from", "chat", "x" * 9000, expect=2)
        self.run_cli("post", "--to", "a", "--from", "chat", "key sk-abcdefghijklmnopqrstuvwxyz123", expect=2)
        self.run_cli("post", "--to", "a", "--from", "chat", "x", "--attach", "https://example.com/a", expect=2)
        self.run_cli("post", "--to", "a", "--from", "chat", "x", "--attach", "/home/u/.env", expect=2)
        rid = self.run_cli("post", "--to", "a", "--from", "chat", "look", "--attach", "shot.png", "--attach", "b.txt")
        shown = json.loads(self.run_cli("show", rid))
        self.assertEqual(len(shown["attachments"]), 2)
        self.assertTrue(Path(shown["attachments"][0]).is_absolute())

    def test_watch_replies_by_agent_sees_only_new(self):
        rid = self.post("alpha")
        self.run_cli("tail", "--as", "alpha", "--once", "--timeout", "10")
        self.run_cli("reply", str(rid), "old", "--from", "alpha")
        watcher = self.spawn("watch-replies", "--from", "alpha", "--interval", "0.05", "--timeout", "5")
        time.sleep(1)
        self.run_cli("reply", str(rid), "new", "--from", "alpha")
        out, _ = watcher.communicate(timeout=30)
        self.assertEqual([r["text"] for r in self.lines(out)], ["new"])


if __name__ == "__main__":
    unittest.main()
