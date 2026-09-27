"""Adversarial pack, publication and HTTP contract checks."""
import hashlib
import json
from pathlib import Path
import tempfile
import threading
import unittest
import urllib.error
import urllib.request
import zipfile

from pack import verify
from run import publish, server


class StoreTests(unittest.TestCase):
    def setUp(self):
        self.temp = tempfile.TemporaryDirectory()
        self.addCleanup(self.temp.cleanup)
        self.root = Path(self.temp.name)
        self.payload = b"test image bytes"
        self.manifest = dict(schema="guo/store-pack@1", id="test-pack", version="1.0.0", kind="background", title="Test", author="GUO", licence="CC0-1.0", min_profile_version=6, preview="still.png", files={"still.png": hashlib.sha256(self.payload).hexdigest()})

    def pack(self, extra=None):
        path = self.root / "input.zip"
        with zipfile.ZipFile(path, "w") as z:
            z.writestr("manifest.json", json.dumps(self.manifest))
            z.writestr("still.png", self.payload)
            for name, data in (extra or {}).items():
                z.writestr(name, data)
        return path

    def test_roundtrip_and_immutable_publication(self):
        pack = self.pack()
        self.assertEqual(verify(pack)["id"], "test-pack")
        root = self.root / "cdn"
        publish(pack, root)
        publish(pack, root)
        index = json.loads((root / "index.json").read_text())
        self.assertEqual(len(index["packs"]), 1)
        self.manifest["title"] = "Changed"
        with self.assertRaises(ValueError):
            publish(self.pack(), root)
        self.assertEqual(json.loads((root / "index.json").read_text()), index)

    def test_reject_payload_paths(self):
        for name in ("../escape.png", "/root.png", "a\\b.png", "a:b.png", "CON.png", "foo./x.png", "cliloc.enu.png", "ART.MUL.png", "a//b.png", "a/../b.png"):
            with self.subTest(name=name), self.assertRaises(ValueError):
                verify(self.pack({name: b"bad"}))

    def test_reject_metadata(self):
        for key, value in (("licence", "Proprietary"), ("kind", "art-override"), ("version", "1.02.0"), ("id", "../a"), ("min_profile_version", True), ("preview", "missing.png")):
            old = self.manifest[key]
            self.manifest[key] = value
            with self.subTest(key=key), self.assertRaises(ValueError):
                verify(self.pack())
            self.manifest[key] = old

    def test_reject_corruption_and_extra(self):
        self.manifest["files"]["still.png"] = "0" * 64
        with self.assertRaises(ValueError):
            verify(self.pack())
        self.manifest["files"]["still.png"] = hashlib.sha256(self.payload).hexdigest()
        with self.assertRaises(ValueError):
            verify(self.pack({"extra.txt": b"extra"}))

    def test_http_ranges_and_head(self):
        root = self.root / "cdn"
        publish(self.pack(), root)
        httpd = server(root, port=0)
        thread = threading.Thread(target=httpd.serve_forever, daemon=True)
        thread.start()
        self.addCleanup(httpd.server_close)
        self.addCleanup(httpd.shutdown)
        base = f"http://127.0.0.1:{httpd.server_port}/packs/test-pack/1.0.0.zip"
        original = (root / "packs/test-pack/1.0.0.zip").read_bytes()
        for spec, expected in (("bytes=0-3", original[:4]), ("bytes=-5", original[-5:]), ("bytes=5-", original[5:])):
            with urllib.request.urlopen(urllib.request.Request(base, headers={"Range": spec})) as response:
                self.assertEqual(response.status, 206)
                self.assertEqual(response.read(), expected)
        with urllib.request.urlopen(urllib.request.Request(base, method="HEAD")) as response:
            self.assertEqual(response.read(), b"")
            self.assertEqual(int(response.headers["Content-Length"]), len(original))
        with self.assertRaises(urllib.error.HTTPError) as error:
            urllib.request.urlopen(urllib.request.Request(base, headers={"Range": "bytes=999999-"}))
        self.assertEqual(error.exception.code, 416)


if __name__ == "__main__":
    unittest.main()
