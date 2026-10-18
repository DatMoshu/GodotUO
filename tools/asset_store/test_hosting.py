"""A catalogue of your own on a Pages branch (hosting.py): the repository template, signing, fast-forward pushes."""
import json
import os
from pathlib import Path
import sys
import tempfile
import unittest

sys.path.insert(0, str(Path(__file__).resolve().parents[1]))
from asset_store import catalogue, hosting
from asset_store.hosted_proof import pack

META = {"id": "test-catalogue", "title": "Test catalogue"}


class HostingTests(unittest.TestCase):
    def setUp(self):
        temp = tempfile.TemporaryDirectory(ignore_cleanup_errors=True)
        self.addCleanup(temp.cleanup)
        self.root = Path(temp.name)
        for key in ("GIT_AUTHOR_NAME", "GIT_COMMITTER_NAME"):
            os.environ.setdefault(key, "GUO test")
        for key in ("GIT_AUTHOR_EMAIL", "GIT_COMMITTER_EMAIL"):
            os.environ.setdefault(key, "test@example.com")
        self.public = catalogue.keygen(self.root / "k.key")
        self.secret = catalogue.read_secret(self.root / "k.key")
        self.remote = self.root / "remote.git"
        hosting.git("init", "--quiet", "--bare", str(self.remote))

    def branch_file(self, name):
        return hosting.git("--git-dir", str(self.remote), "show", f"gh-pages:{name}").stdout

    def test_init_writes_a_repository_of_your_own(self):
        written = hosting.init_repo(self.root / "repo", "shard-packs", "Shard's packs", self.public)
        publish = (self.root / "repo/.github/workflows/publish.yml").read_text()
        self.assertIn("UO_STORE_CATALOGUE_ID: shard-packs", publish)
        self.assertIn("UO_STORE_CATALOGUE_TITLE: 'Shard''s packs'", publish)
        self.assertNotIn("guo-official", publish)
        self.assertIn("*.key", (self.root / "repo/.gitignore").read_text())
        self.assertFalse(any(p.suffix == ".key" for p in written))
        with self.assertRaisesRegex(ValueError, "not empty"):
            hosting.init_repo(self.root / "repo", "shard-packs", "again")
        with self.assertRaisesRegex(ValueError, "official"):
            hosting.init_repo(self.root / "other", "guo-official", "GUO packs")

    def test_push_signs_rises_and_keeps_history(self):
        zips = self.root / "zips"
        zips.mkdir()
        first = hosting.push(str(self.remote), self.secret, META, packs=[pack(zips, "a-pack", "1.0.0")])
        second = hosting.push(str(self.remote), self.secret, META, packs=[pack(zips, "a-pack", "1.1.0")])
        self.assertTrue(first["pushed"] and not first["existed"] and second["existed"])
        raw = self.branch_file("index.json").encode()
        index = catalogue.check(raw, self.branch_file("index.json.sig"), self.public, first["index"]["sequence"] + 1)
        self.assertEqual([p["manifest"]["version"] for p in index["packs"]], ["1.0.0", "1.1.0"])
        self.assertEqual(self.branch_file(".gitattributes"), "* -text\n")
        self.assertIn("index.html", hosting.git("--git-dir", str(self.remote), "ls-tree", "--name-only", "gh-pages").stdout)

    def test_dry_run_pushes_nothing_and_keeps_the_site(self):
        keep = self.root / "kept"
        result = hosting.push(str(self.remote), self.secret, META, packs=[pack(self.root, "a-pack", "1.0.0")], dry_run=True, keep=keep)
        self.assertFalse(result["pushed"])
        self.assertTrue((keep / "index.json.sig").is_file())
        self.assertEqual(hosting.git("ls-remote", "--heads", str(self.remote), "gh-pages").stdout.strip(), "")

    def test_a_branch_that_moved_is_never_overwritten(self):
        hosting.push(str(self.remote), self.secret, META, packs=[pack(self.root, "a-pack", "1.0.0")])
        real = hosting._checkout

        def racing(remote, branch, work):
            existed = real(remote, branch, work)
            # Another publisher pushes after this one read the branch.
            other = self.root / "other"
            hosting.git("clone", "--quiet", "--branch", branch, remote, str(other))
            (other / "theirs.txt").write_text("theirs")
            hosting.git("add", "theirs.txt", cwd=other)
            hosting.git("commit", "--quiet", "-m", "theirs", cwd=other)
            hosting.git("push", "--quiet", "origin", branch, cwd=other)
            return existed

        hosting._checkout = racing
        self.addCleanup(setattr, hosting, "_checkout", real)
        with self.assertRaisesRegex(ValueError, "moved since it was read"):
            hosting.push(str(self.remote), self.secret, META, packs=[pack(self.root, "a-pack", "1.1.0")])
        self.assertEqual(self.branch_file("theirs.txt"), "theirs")

    def test_a_listed_pack_cannot_also_be_hosted(self):
        zip_path = pack(self.root, "a-pack", "1.0.0")
        listing = self.root / "listing/a-pack"
        listing.mkdir(parents=True)
        (listing / "1.0.0.json").write_text(json.dumps({"urls": ["http://127.0.0.1:9/a.zip"], "sha256": catalogue.sha256(zip_path),
                                                        "size": zip_path.stat().st_size}))
        cache = self.root / "cache"
        cache.mkdir()
        (cache / f"{catalogue.sha256(zip_path)}.zip").write_bytes(zip_path.read_bytes())
        with self.assertRaisesRegex(ValueError, "also hosted"):
            hosting.build_site(self.root / "site", self.secret, META, listing=self.root / "listing", packs=[zip_path],
                               cache=cache, require_https=False)


if __name__ == "__main__":
    unittest.main()
