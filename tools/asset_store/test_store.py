"""Adversarial pack, publication and HTTP contract checks."""
import hashlib
import json
from pathlib import Path
import shutil
import subprocess
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
        for name in ("../escape.png", "/root.png", "a\\b.png", "a:b.png", "CON.png", "foo./x.png", "cliloc.enu.png", "ART.MUL.png", "a//b.png", "a/../b.png",
                     ".mul.png", ".UOP.png", ".idx.txt", ".def.json", "nested/.mul.png", ".mul.png/still.png"):
            # Declare the malicious entry so failure proves path validation,
            # rather than the separate undeclared-payload check.
            self.manifest["files"][name] = hashlib.sha256(b"bad").hexdigest()
            with self.subTest(name=name), self.assertRaises(ValueError):
                verify(self.pack({name: b"bad"}))
            del self.manifest["files"][name]

    def test_reject_metadata(self):
        for key, value in (("licence", "Proprietary"), ("kind", "art-override"), ("version", "1.02.0"), ("id", "../a"), ("min_profile_version", True), ("preview", "missing.png"),
                           ("title", " " * 200 + "x"), ("author", "x" + " " * 200)):
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

    def screensaver(self, min_profile=11, loops=("loop.ogv",)):
        self.manifest.update(kind="screensaver", id="test-saver", min_profile_version=min_profile)
        extra = {}
        for name in loops:
            data = b"ogv " + name.encode()
            self.manifest["files"][name] = hashlib.sha256(data).hexdigest()
            extra[name] = data
        return self.pack(extra)

    def test_screensaver_kind(self):
        self.assertEqual(verify(self.screensaver())["kind"], "screensaver")

    def test_reject_screensaver_contract(self):
        for label, kwargs in (("no loop", dict(loops=())), ("two loops", dict(loops=("a.ogv", "b.ogv"))),
                              ("old profile", dict(min_profile=10))):
            self.setUp()
            with self.subTest(label), self.assertRaises(ValueError):
                verify(self.screensaver(**kwargs))

    def postfx(self, files):
        self.manifest.update(kind="postfx", id="test-looks")
        for name, data in files.items():
            self.manifest["files"][name] = hashlib.sha256(data).hexdigest()
        return self.pack(files)

    def test_postfx_kind(self):
        # ADR-0023: presets and shaders; a LUT image rides along.
        pack = self.postfx({"looks/dusk.json": b'{"name": "Dusk", "passes": []}',
                            "looks/dusk.gdshader": b"shader_type canvas_item;", "looks/dusk_lut.png": b"png"})
        self.assertEqual(verify(pack)["kind"], "postfx")

    def test_reject_postfx_contract(self):
        with self.subTest("no preset"), self.assertRaises(ValueError):
            verify(self.postfx({"only.gdshader": b"shader_type canvas_item;"}))
        self.setUp()
        # Shader code is accepted in a postfx pack only.
        self.manifest["files"]["sneaky.gdshader"] = hashlib.sha256(b"x").hexdigest()
        with self.subTest("shader in a background pack"), self.assertRaises(ValueError):
            verify(self.pack({"sneaky.gdshader": b"x"}))

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

    def test_web_catalogue_headless(self):
        self.web_catalogue(signer=None)

    def test_web_catalogue_headless_signed(self):
        # A signed (v2, ADR-0026) index renders the same page.
        from asset_store import catalogue
        key = self.root / "catalogue.key"
        catalogue.keygen(key)
        self.web_catalogue(signer=(catalogue.read_secret(key), {"id": "test", "title": "Test"}, ""))

    def web_catalogue(self, signer):
        node = shutil.which("node")
        self.assertIsNotNone(node, "Web contract test requires Node 18+ (built-ins only; no browser/npm packages)")
        root = self.root / "cdn"
        for kind in ("background", "theme", "sound", "profile-preset", "razor-script", "screensaver"):
            self.manifest["files"] = {"still.png": hashlib.sha256(self.payload).hexdigest()}
            self.manifest.update(kind=kind, id="moongate-shimmer" if kind == "background" else "sample-" + kind,
                                 title='<img src=x onerror="throw 1"> ' + kind, author="<b>Fixture creator</b>")
            if kind == "razor-script":
                source = b"sysmsg 'Store script'\n"
                self.manifest["files"]["hello.razor"] = hashlib.sha256(source).hexdigest()
                publish(self.pack({"hello.razor": source}), root, signer)
            elif kind == "screensaver":
                self.manifest.update(min_profile_version=11)
                self.manifest["files"]["loop.ogv"] = hashlib.sha256(b"loop").hexdigest()
                publish(self.pack({"loop.ogv": b"loop"}), root, signer)
            else:
                publish(self.pack(), root, signer)
        httpd = server(root, port=0)
        thread = threading.Thread(target=httpd.serve_forever, daemon=True)
        thread.start()
        self.addCleanup(httpd.server_close)
        self.addCleanup(httpd.shutdown)
        result = subprocess.run([node, str(Path(__file__).with_name("web_check.mjs")),
                                 f"http://127.0.0.1:{httpd.server_port}/"], capture_output=True, text=True,
                                encoding="utf-8", errors="replace", timeout=30)
        self.assertEqual(result.returncode, 0, result.stdout + result.stderr)
        print(result.stdout.strip())


if __name__ == "__main__":
    unittest.main()
