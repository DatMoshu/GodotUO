"""The proof's --out and --clip stay inside build/ (SF5).

    python tools/editor_objects_proof/test_run.py

Standard library only, on temporary folders: no shard, client, engine or client install.
"""
from __future__ import annotations

import tempfile
import unittest
from pathlib import Path
from types import SimpleNamespace

import run


class ProofOutTests(unittest.TestCase):
    def setUp(self) -> None:
        tmp = tempfile.TemporaryDirectory()
        self.addCleanup(tmp.cleanup)
        self.root = Path(tmp.name).resolve()
        self.build = self.root / "repo" / "build"
        self.client = self.root / "uo"
        self.build.mkdir(parents=True)
        self.client.mkdir()
        (self.client / "art.mul").write_bytes(b"x")
        self.cfg = SimpleNamespace(build=self.build, client_data=self.client)

    def refused(self, fn, *args) -> None:
        with self.assertRaises(SystemExit) as cm:
            fn(self.cfg, *args)
        self.assertEqual(cm.exception.code, 2)

    def test_default_is_under_build(self) -> None:
        self.assertEqual(run.proof_out(self.cfg, None, "editor_objects_proof_live"),
                         self.build / "editor_objects_proof_live")

    def test_given_folder_under_build(self) -> None:
        self.assertEqual(run.proof_out(self.cfg, self.build / "a" / "b", "x"), self.build / "a" / "b")

    def test_refuses_outside_build(self) -> None:
        for out in (self.root, self.root / "repo", self.root / "repo" / "tools", self.root / "elsewhere"):
            with self.subTest(out=out):
                self.refused(run.proof_out, out, "x")

    def test_refuses_build_itself(self) -> None:
        self.refused(run.proof_out, self.build, "x")

    def test_refuses_dotdot_escape(self) -> None:
        self.refused(run.proof_out, self.build / "proof" / ".." / ".." / "src", "x")

    def test_refuses_client_install(self) -> None:
        self.refused(run.proof_out, self.client, "x")
        self.refused(run.proof_out, self.client / "sub", "x")

    def test_refuses_client_install_inside_build(self) -> None:
        # A client install kept under build/ is still never removed or written.
        client = self.build / "uo"
        client.mkdir()
        self.cfg.client_data = client
        self.refused(run.proof_out, client, "x")
        self.refused(run.proof_out, client / "proof", "x")
        self.assertEqual(run.proof_out(self.cfg, self.build / "proof", "x"), self.build / "proof")

    def test_refuses_link_out_of_build(self) -> None:
        target = self.root / "victim"
        target.mkdir()
        link = self.build / "link"
        try:
            link.symlink_to(target, target_is_directory=True)
        except OSError:
            self.skipTest("this account cannot create symlinks")
        self.refused(run.proof_out, link, "x")

    def test_refusal_touches_nothing(self) -> None:
        victim = self.root / "keep"
        victim.mkdir()
        (victim / "file.txt").write_text("keep", encoding="utf-8")
        self.refused(run.proof_out, victim, "x")
        self.assertEqual((victim / "file.txt").read_text(encoding="utf-8"), "keep")

    def test_clip_inside_build(self) -> None:
        self.assertEqual(run.proof_clip(self.cfg, self.build / "clip.mp4"), self.build / "clip.mp4")
        self.assertEqual(run.proof_clip(self.cfg, self.build / "clips" / "c.mp4"), self.build / "clips" / "c.mp4")

    def test_clip_refused_outside_build(self) -> None:
        self.refused(run.proof_clip, self.root / "clip.mp4")
        self.refused(run.proof_clip, self.client / "clip.mp4")
        self.refused(run.proof_clip, self.build / ".." / "clip.mp4")


if __name__ == "__main__":
    unittest.main()
