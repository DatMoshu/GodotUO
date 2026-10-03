"""Pack inspection, the automatic content-policy verdict, and the catalogue listing a pull request carries
(the editor's UO Store > Publish tab calls the same code through `run.py check` and `run.py prepare-listing`)."""
import hashlib
import json
from pathlib import Path
import subprocess
import sys
import tempfile
import unittest
import zipfile

sys.path.insert(0, str(Path(__file__).resolve().parents[1]))
from asset_store import content_examples
from asset_store.policy import inspect_pack, listing_for

RUN = Path(__file__).with_name("run.py")


def make_pack(path, files, **manifest):
    m = dict(schema="guo/store-pack@1", id="policy-test", version="1.0.0", kind="background", title="Policy test",
             author="Tester", licence="CC0-1.0", min_profile_version=6, preview="still.png",
             files={name: hashlib.sha256(data).hexdigest() for name, data in files.items()})
    m.update(manifest)
    with zipfile.ZipFile(path, "w") as z:
        z.writestr("manifest.json", json.dumps(m))
        for name, data in files.items():
            z.writestr(name, data)
    return path


PNG = content_examples.png(8, 8, (1, 2, 3, 255))


class PolicyTests(unittest.TestCase):
    @classmethod
    def setUpClass(cls):
        cls.temp = tempfile.TemporaryDirectory()
        cls.root = Path(cls.temp.name)
        cls.examples = content_examples.build(cls.root / "examples")

    @classmethod
    def tearDownClass(cls):
        cls.temp.cleanup()

    def run_tool(self, *args):
        return subprocess.run([sys.executable, str(RUN), *map(str, args)], capture_output=True, text=True)

    def test_a_starter_pack_passes_and_lists_what_it_needs(self):
        combined = next(p for p in self.examples if p.name == "sample-content-server.zip")
        info = inspect_pack(combined, self.root / "empty-store")
        self.assertEqual(info["policy"]["verdict"], "pass")
        self.assertEqual(info["kind"], "content")
        self.assertEqual(info["target"], "server")
        self.assertEqual(info["dependencies"], {"sample-content-art": {"version": "1.0.0", "status": "not in the local store"}})
        self.assertIn("started from the Ultima Online client's files", info["policy"]["note"])

    def test_an_executable_renamed_png_is_refused(self):
        bad = make_pack(self.root / "exe.zip", {"still.png": PNG, "tool.png": b"MZ\x90\x00 not an image"})
        info = inspect_pack(bad)
        self.assertEqual(info["policy"]["verdict"], "refused")
        self.assertTrue(any("tool.png" in f["text"] for f in info["policy"]["findings"]))
        r = self.run_tool("check", bad)
        self.assertEqual(r.returncode, 1, r.stdout + r.stderr)

    def test_a_pack_claiming_to_be_official_is_refused(self):
        bad = make_pack(self.root / "official.zip", {"still.png": PNG}, title="Official Ultima Online Backgrounds")
        self.assertEqual(inspect_pack(bad)["policy"]["verdict"], "refused")
        review = make_pack(self.root / "mention.zip", {"still.png": PNG}, id="mention-test", title="Backgrounds for Ultima Online shards")
        self.assertEqual(inspect_pack(review)["policy"]["verdict"], "review")

    def test_a_file_named_like_a_client_file_is_for_review(self):
        pack = make_pack(self.root / "named.zip", {"still.png": PNG, "tiledata.json": b"{}"}, id="named-test")
        info = inspect_pack(pack)
        self.assertEqual(info["policy"]["verdict"], "review")
        self.assertEqual(self.run_tool("check", pack).returncode, 0)

    def test_a_pack_that_does_not_verify_is_reported(self):
        broken = self.root / "broken.zip"
        broken.write_bytes(b"not a zip")
        r = self.run_tool("check", "--json", broken)
        self.assertEqual(r.returncode, 1)
        self.assertFalse(json.loads(r.stdout)["ok"])

    def test_the_listing_is_written_and_nothing_is_sent(self):
        pack = make_pack(self.root / "listed.zip", {"still.png": PNG}, id="listed-test")
        out = self.root / "pr"
        r = self.run_tool("prepare-listing", pack, "--url", "https://example.org/listed-test-1.0.0.zip",
                          "--provenance", "Drawn by hand in a paint program", "--out", out)
        self.assertEqual(r.returncode, 0, r.stdout + r.stderr)
        listing = json.loads((out / "packs/listed-test/1.0.0.json").read_text(encoding="utf-8"))
        self.assertEqual(set(listing), {"urls", "sha256", "size", "provenance"})
        self.assertEqual(listing["size"], pack.stat().st_size)
        self.assertIn("gh pr create --repo DatMoshu/GodotUO-packs", r.stdout)
        self.assertIn("no signing key", r.stdout)
        self.assertTrue((out / "PULL_REQUEST.md").is_file())
        # The same again is fine; a different listing for the same version is not.
        again = self.run_tool("prepare-listing", pack, "--url", "https://example.org/listed-test-1.0.0.zip",
                              "--provenance", "Drawn by hand in a paint program", "--out", out)
        self.assertEqual(again.returncode, 0, again.stdout + again.stderr)
        changed = self.run_tool("prepare-listing", pack, "--url", "https://example.org/other.zip",
                                "--provenance", "Drawn by hand in a paint program", "--out", out)
        self.assertNotEqual(changed.returncode, 0)

    def test_listing_refuses_http_a_missing_provenance_and_a_refused_pack(self):
        pack = make_pack(self.root / "l2.zip", {"still.png": PNG}, id="listed-two")
        with self.assertRaises(ValueError):
            listing_for(pack, ["http://example.org/x.zip"], "Drawn")
        with self.assertRaises(ValueError):
            listing_for(pack, ["https://example.org/x.zip"], "  ")
        bad = make_pack(self.root / "l3.zip", {"still.png": PNG, "x.png": b"MZ"}, id="listed-three")
        with self.assertRaises(ValueError):
            listing_for(bad, ["https://example.org/x.zip"], "Drawn")


if __name__ == "__main__":
    unittest.main()
