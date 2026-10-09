import subprocess
import tempfile
import unittest
from pathlib import Path

from run import ROOT, decode_text, scan

# Built from parts so this file holds no literal leak of its own.
LAN = "192.168." + "7.42"
HOME = "C:" + "\\Users\\" + "fixtureperson\\game"


def git(root, *args):
    return subprocess.run(["git", *args], cwd=root, check=True, capture_output=True, text=True).stdout


class PrivacyScanTests(unittest.TestCase):
    def setUp(self):
        scratch = ROOT / "build/privacy_scan"
        scratch.mkdir(parents=True, exist_ok=True)
        self.temp = tempfile.TemporaryDirectory(prefix="tests-", dir=scratch)
        self.addCleanup(self.temp.cleanup)
        self.root = Path(self.temp.name) / "repo"
        self.here = Path(self.temp.name) / "lists"
        self.root.mkdir()
        self.here.mkdir()
        git(self.root, "init", "--quiet")
        git(self.root, "config", "user.name", "Fixture")
        git(self.root, "config", "user.email", "test@example.com")
        git(self.root, "config", "commit.gpgsign", "false")
        git(self.root, "config", "core.autocrlf", "false")

    def write(self, rel, data):
        path = self.root / rel
        path.parent.mkdir(parents=True, exist_ok=True)
        if isinstance(data, str):
            data = data.encode("utf-8")
        path.write_bytes(data)

    def stage(self, rel, data):
        self.write(rel, data)
        git(self.root, "add", rel)

    def lists(self, allow=(), deny=()):
        (self.here / "allow.txt").write_text("".join(f"{a}\n" for a in allow), encoding="utf-8")
        (self.here / "deny.local.txt").write_text("".join(f"{d}\n" for d in deny), encoding="utf-8")

    def scan(self, staged):
        return scan(self.root, staged, here=self.here)

    # --staged reads the index

    def test_staged_leak_with_clean_working_copy_is_caught(self):
        self.lists()
        self.stage("notes.md", f"shard at {LAN}\n")
        self.write("notes.md", "shard at 127.0.0.1\n")
        hits = self.scan(staged=True)
        self.assertEqual(hits, [f"notes.md:1: private IPv4: {LAN}"])

    def test_clean_staged_copy_with_leaky_working_copy_passes(self):
        self.lists()
        self.stage("notes.md", "shard at 127.0.0.1\n")
        self.write("notes.md", f"shard at {LAN}\n")
        self.assertEqual(self.scan(staged=True), [])

    def test_staged_binary_is_read_from_the_index(self):
        self.lists()
        self.stage("blob.bin", b"\x00\x01" + HOME.encode("utf-16-le") + b"\x80")
        self.write("blob.bin", b"\x00\x01\x80 nothing here")
        hits = self.scan(staged=True)
        self.assertEqual(len(hits), 1)
        self.assertIn("blob.bin: home folder in binary", hits[0])

    def test_staged_deletion_and_unstaged_files_are_not_scanned(self):
        self.lists()
        self.stage("gone.md", f"{LAN}\n")
        git(self.root, "commit", "--quiet", "-m", "fixture")
        git(self.root, "rm", "--quiet", "gone.md")
        self.write("untracked.md", f"{LAN}\n")
        self.assertEqual(self.scan(staged=True), [])

    def test_full_scan_reads_the_working_copy(self):
        self.lists()
        self.stage("notes.md", "shard at 127.0.0.1\n")
        self.write("notes.md", f"shard at {LAN}\n")
        self.assertEqual(self.scan(staged=False), [f"notes.md:1: private IPv4: {LAN}"])

    # allow.txt excuses a line, never more

    def test_allow_excuses_only_the_matching_line(self):
        self.lists(allow=[r'^EXAMPLE = "192\.168\.7\.42"$'])
        self.stage("a.py", f'EXAMPLE = "{LAN}"\nreal = "{LAN}"\n')
        self.stage("b.py", f'real = "{LAN}"\n')
        hits = self.scan(staged=True)
        self.assertEqual(hits, [f"a.py:2: private IPv4: {LAN}", f"b.py:1: private IPv4: {LAN}"])

    def test_allow_does_not_excuse_a_binary(self):
        self.lists(allow=[r"Users"], deny=["fixtureperson"])
        self.stage("blob.bin", b"\x00\x80" + HOME.encode("ascii"))
        hits = self.scan(staged=True)
        self.assertEqual(len(hits), 2)

    def test_deny_list_is_case_insensitive(self):
        self.lists(deny=["FixturePerson"])
        self.stage("notes.md", "built by fixtureperson\n")
        self.assertEqual(self.scan(staged=True), ["notes.md:1: local deny list: fixtureperson"])

    # UTF-16 text is read as text

    def test_utf16_text_with_bom_is_scanned_line_by_line(self):
        self.lists(allow=[r"^allowed "])
        text = f"first line\r\nallowed {LAN}\r\nleak {LAN}\r\n"
        self.stage("le.txt", b"\xff\xfe" + text.encode("utf-16-le"))
        self.stage("be.txt", b"\xfe\xff" + text.encode("utf-16-be"))
        hits = self.scan(staged=True)
        self.assertEqual(hits, [f"be.txt:3: private IPv4: {LAN}", f"le.txt:3: private IPv4: {LAN}"])

    def test_utf16_text_finds_an_email_the_byte_scan_would_miss(self):
        self.lists()
        address = "someone" + "@" + "mail.test.net"
        self.stage("contacts.txt", b"\xff\xfe" + f"mail {address}\n".encode("utf-16-le"))
        self.assertEqual(self.scan(staged=True), [f"contacts.txt:1: email: {address}"])

    def test_decode_text(self):
        self.assertEqual(decode_text("café".encode("utf-8")), "café")
        self.assertEqual(decode_text(b"\xff\xfe" + "hi".encode("utf-16-le")), "hi")
        self.assertIsNone(decode_text(b"\x00\x80\xff"))
        self.assertIsNone(decode_text(b"\xff\xfe\x00"))

    # skips and programs

    def test_compiled_program_is_a_hit_and_skipped_paths_are_not(self):
        self.lists()
        self.stage("bin/tool.dll", b"MZ")
        self.stage("sources/up.cs", f"{LAN}\n")
        self.stage("art/pic.png", HOME.encode("ascii"))
        hits = self.scan(staged=True)
        self.assertEqual(len(hits), 1)
        self.assertIn("bin/tool.dll: compiled program tracked", hits[0])


if __name__ == "__main__":
    unittest.main()
