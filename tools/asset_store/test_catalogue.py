"""Signed catalogue indexes (ADR-0026): signing, refusal, mirrors, and the C# verifier agreeing."""
import datetime as dt
import functools
import hashlib
import json
from http.server import SimpleHTTPRequestHandler, ThreadingHTTPServer
import os
from pathlib import Path
import subprocess
import sys
import tempfile
import threading
import unittest
import zipfile

sys.path.insert(0, str(Path(__file__).resolve().parents[1]))
from asset_store import catalogue, ed25519
from asset_store.run import make_index, publish

ROOT = Path(__file__).resolve().parents[2]
HEADLESS = ROOT / "tools/asset_store/headless/bin/Debug/net8.0/StoreSmoke.dll"


class Quiet(SimpleHTTPRequestHandler):
    def log_message(self, *args):
        pass


class CatalogueTests(unittest.TestCase):
    @classmethod
    def setUpClass(cls):
        # Build the headless checker first: a stale build would test yesterday's client.
        project = ROOT / "tools/asset_store/headless/StoreSmoke.csproj"
        built = subprocess.run(["dotnet", "build", str(project), "-v", "q", "-nologo"], capture_output=True, text=True)
        if built.returncode:
            raise RuntimeError("the headless store checker does not build: " + built.stdout[-2000:])

    def setUp(self):
        temp = tempfile.TemporaryDirectory()
        self.addCleanup(temp.cleanup)
        self.root = Path(temp.name)
        self.key_path = self.root / "catalogue.key"
        self.public = catalogue.keygen(self.key_path)
        self.secret = catalogue.read_secret(self.key_path)
        self.meta = {"id": "test-catalogue", "title": "Test catalogue"}

    def pack(self, pack_id="test-pack", ver="1.0.0"):
        payload = b"test image bytes " + pack_id.encode()
        manifest = dict(schema="guo/store-pack@1", id=pack_id, version=ver, kind="background", title="Test", author="GUO",
                        licence="CC0-1.0", min_profile_version=6, preview="still.png",
                        files={"still.png": hashlib.sha256(payload).hexdigest()})
        path = self.root / f"{pack_id}-{ver}.zip"
        with zipfile.ZipFile(path, "w") as z:
            z.writestr("manifest.json", json.dumps(manifest))
            z.writestr("still.png", payload)
        return path

    def signed_store(self):
        store = self.root / "cdn"
        publish(self.pack(), store, (self.secret, self.meta, ""))
        return store

    def test_keygen_refuses_to_overwrite_and_round_trips(self):
        with self.assertRaises(FileExistsError):
            catalogue.keygen(self.key_path)
        self.assertEqual(ed25519.public_key(self.secret), self.public)

    def test_signed_store_verifies_and_names_its_key(self):
        store = self.signed_store()
        index = catalogue.check((store / "index.json").read_bytes(), (store / "index.json.sig").read_text(), self.public)
        self.assertEqual(index["catalogue"]["id"], "test-catalogue")
        entry = index["packs"][0]
        self.assertEqual(entry["urls"], ["packs/test-pack/1.0.0.zip"])
        self.assertEqual(entry["sha256"], hashlib.sha256((store / entry["urls"][0]).read_bytes()).hexdigest())
        self.assertTrue((store / entry["preview_url"]).is_file())

    def test_tampering_rollback_expiry_and_wrong_key_are_refused(self):
        store = self.signed_store()
        raw, sig = (store / "index.json").read_bytes(), (store / "index.json.sig").read_text()
        with self.assertRaisesRegex(ValueError, "does not verify"):
            catalogue.check(raw.replace(b"Test catalogue", b"Evil catalogue"), sig, self.public)
        with self.assertRaisesRegex(ValueError, "different key"):
            catalogue.check(raw, sig, ed25519.public_key(os.urandom(32)))
        sequence = json.loads(raw)["sequence"]
        with self.assertRaisesRegex(ValueError, "rollback"):
            catalogue.check(raw, sig, self.public, seen_sequence=sequence + 1)
        catalogue.build(store, catalogue.entries_from_store(store, None), self.meta, self.secret, expires_days=1)
        raw, sig = (store / "index.json").read_bytes(), (store / "index.json.sig").read_text()
        self.assertGreater(json.loads(raw)["sequence"], sequence)
        catalogue.check(raw, sig, self.public)
        with self.assertRaisesRegex(ValueError, "expired"):
            catalogue.check(raw, sig, self.public, at=catalogue.now() + dt.timedelta(days=2))

    def test_unsigned_store_drops_a_stale_signature(self):
        store = self.signed_store()
        make_index(store)
        self.assertFalse((store / "index.json.sig").exists())
        self.assertEqual(json.loads((store / "index.json").read_text())["schema"], "guo/store-index@1")

    def test_base_url_and_listing_mirrors(self):
        store = self.root / "cdn"
        publish(self.pack(), store)
        listing = store / "listing/test-pack"
        listing.mkdir(parents=True)
        (listing / "1.0.0.json").write_text(json.dumps({"urls": ["https://mirror.example.org/test-pack-1.0.0.zip"],
                                                        "provenance": "drawn for the test"}))
        entries = catalogue.entries_from_store(store, "https://packs.example.org/")
        self.assertEqual(entries[0]["urls"], ["https://packs.example.org/packs/test-pack/1.0.0.zip",
                                              "https://mirror.example.org/test-pack-1.0.0.zip"])
        self.assertEqual(entries[0]["provenance"], "drawn for the test")

    def test_urls_must_be_https_or_local(self):
        for good in ("https://packs.example.org/a.zip", "http://127.0.0.1:18865/a.zip", "http://192.168.1.20/a.zip",
                     "packs/a/1.0.0.zip"):
            catalogue.check_url(good)
        for bad in ("http://packs.example.org/a.zip", "https://user:pw@example.org/a.zip", "ftp://example.org/a.zip",
                    "../escape.zip", "/absolute.zip", "https://example.org/a.zip#x"):
            with self.assertRaises(ValueError, msg=bad):
                catalogue.check_url(bad)

    def test_listing_mode_uses_the_mirror_that_serves_the_listed_bytes(self):
        hosted = self.root / "host"
        (hosted / "good").mkdir(parents=True)
        (hosted / "bad").mkdir(parents=True)
        zip_path = self.pack()
        (hosted / "good/test-pack.zip").write_bytes(zip_path.read_bytes())
        (hosted / "bad/test-pack.zip").write_bytes(b"not the pack")
        httpd = ThreadingHTTPServer(("127.0.0.1", 0), functools.partial(Quiet, directory=str(hosted)))
        threading.Thread(target=httpd.serve_forever, daemon=True).start()
        self.addCleanup(httpd.server_close)
        self.addCleanup(httpd.shutdown)
        base = f"http://127.0.0.1:{httpd.server_port}"
        listing = self.root / "listing/test-pack"
        listing.mkdir(parents=True)
        (listing / "1.0.0.json").write_text(json.dumps({
            "urls": [f"{base}/bad/test-pack.zip", f"{base}/missing.zip", f"{base}/good/test-pack.zip"],
            "sha256": hashlib.sha256(zip_path.read_bytes()).hexdigest(), "size": zip_path.stat().st_size,
            "provenance": "drawn for the test"}))
        site = self.root / "site"
        entries = catalogue.entries_from_listing(self.root / "listing", self.root / "cache", site, require_https=False)
        index = catalogue.build(site, entries, self.meta, self.secret)
        self.assertEqual(index["packs"][0]["provenance"], "drawn for the test")
        catalogue.check((site / "index.json").read_bytes(), (site / "index.json.sig").read_text(), self.public)
        with self.assertRaisesRegex(ValueError, "HTTPS"):
            catalogue.entries_from_listing(self.root / "listing", self.root / "cache2", site)

    def serve(self, folder):
        httpd = ThreadingHTTPServer(("127.0.0.1", 0), functools.partial(Quiet, directory=str(folder)))
        threading.Thread(target=httpd.serve_forever, daemon=True).start()
        self.addCleanup(httpd.server_close)
        self.addCleanup(httpd.shutdown)
        return f"http://127.0.0.1:{httpd.server_port}"

    def test_csharp_client_end_to_end(self):
        """The real C# client: approval, a dead first URL and a working mirror, tampering, rollback, a new key."""
        store, mirror, installed = self.root / "cdn", self.root / "mirror", self.root / "installed"
        mirror.mkdir()
        publish(self.pack(), store)
        mirror_url = self.serve(mirror)
        (store / "listing/test-pack").mkdir(parents=True)
        (store / "listing/test-pack/1.0.0.json").write_text(json.dumps({"urls": [mirror_url + "/test-pack.zip"], "provenance": "drawn for the test"}))
        catalogue.build(store, catalogue.entries_from_store(store, None), self.meta, self.secret)
        # The catalogue's own copy goes away: installing has to fall through to the mirror.
        os.replace(store / "packs/test-pack/1.0.0.zip", mirror / "test-pack.zip")
        url = self.serve(store)

        def step(*args, expect=0):
            out = subprocess.run(["dotnet", str(HEADLESS), "catalogue", args[0], url, str(installed), *args[1:]],
                                 capture_output=True, text=True, timeout=120)
            self.assertEqual(out.returncode, expect, f"{args}: {out.stdout}{out.stderr}")
            return out.stdout

        self.assertIn("PASS expect-approval", step("expect-approval"))
        step("approve")
        self.assertIn("signed True", step("list"))
        self.assertIn("PASS install: test-pack 1.0.0", step("install", "test-pack"))
        self.assertTrue((installed / "test-pack/1.0.0/still.png").is_file())

        old = (store / "index.json").read_bytes(), (store / "index.json.sig").read_bytes()
        (store / "index.json").write_bytes(old[0].replace(b"Test catalogue", b"Evil catalogue"))
        step("expect-refused", "does not verify")

        catalogue.build(store, [catalogue.entry(json.loads(old[0])["packs"][0]["manifest"], mirror / "test-pack.zip",
                                                [mirror_url + "/test-pack.zip"], json.loads(old[0])["packs"][0]["preview_url"], "")],
                        self.meta, self.secret)
        step("list")
        (store / "index.json").write_bytes(old[0]); (store / "index.json.sig").write_bytes(old[1])
        step("expect-refused", "rollback")

        other = self.root / "other.key"
        catalogue.keygen(other)
        catalogue.build(store, [catalogue.entry(json.loads(old[0])["packs"][0]["manifest"], mirror / "test-pack.zip",
                                                [mirror_url + "/test-pack.zip"], json.loads(old[0])["packs"][0]["preview_url"], "")],
                        self.meta, catalogue.read_secret(other))
        self.assertIn("PASS expect-key-changed", step("expect-key-changed"))

    def test_mirror_copies_a_signed_catalogue_byte_for_byte(self):
        hosted = self.root / "host"
        hosted.mkdir()
        zip_path = self.pack()
        (hosted / "test-pack.zip").write_bytes(zip_path.read_bytes())
        host_url = self.serve(hosted)
        listing = self.root / "listing/test-pack"
        listing.mkdir(parents=True)
        (listing / "1.0.0.json").write_text(json.dumps({"urls": [f"{host_url}/test-pack.zip"],
            "sha256": hashlib.sha256(zip_path.read_bytes()).hexdigest(), "size": zip_path.stat().st_size}))
        site = self.root / "site"
        out = self.root / "mirror"
        mirror_base = self.serve(out.parent) + "/mirror"
        entries = catalogue.entries_from_listing(self.root / "listing", self.root / "cache", site, require_https=False, mirrors=[mirror_base])
        self.assertEqual(entries[0]["urls"][0], mirror_base + "/packs/test-pack/1.0.0.zip")
        catalogue.build(site, entries, self.meta, self.secret)
        site_url = self.serve(site)
        index = catalogue.mirror(site_url, self.public, out)
        self.assertEqual(index["sequence"], json.loads((site / "index.json").read_text())["sequence"])
        self.assertEqual((out / "index.json").read_bytes(), (site / "index.json").read_bytes())
        self.assertEqual((out / "packs/test-pack/1.0.0.zip").read_bytes(), zip_path.read_bytes())
        self.assertTrue((out / entries[0]["preview_url"]).is_file())
        catalogue.mirror(site_url, self.public, out)  # a second sync with nothing new is a no-op
        with self.assertRaisesRegex(ValueError, "different key"):
            catalogue.mirror(site_url, ed25519.public_key(os.urandom(32)), self.root / "mirror2")

    def test_csharp_verifier_agrees(self):
        vectors = []
        for n in range(4):
            secret, message = os.urandom(32), os.urandom(n * 37)
            vectors.append({"public": ed25519.public_key(secret).hex(), "message": message.hex(),
                            "signature": ed25519.sign_reference(secret, message).hex()})
        path = self.root / "vectors.json"
        path.write_text(json.dumps({"vectors": vectors}))
        out = subprocess.run(["dotnet", str(HEADLESS), "ed25519", str(path)], capture_output=True, text=True)
        self.assertEqual(out.returncode, 0, out.stdout + out.stderr)


if __name__ == "__main__":
    unittest.main()
