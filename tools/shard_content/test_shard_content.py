"""A shard's content (ADR-0026 section 4): deploy writes the export and the descriptor, and a client
installs exactly what the descriptor names, from catalogues signed with the keys it names."""
import functools
import json
from http.server import SimpleHTTPRequestHandler, ThreadingHTTPServer
from pathlib import Path
import subprocess
import sys
import tempfile
import threading
import unittest

sys.path.insert(0, str(Path(__file__).resolve().parents[1]))
from asset_store import catalogue, content_examples, ed25519
from asset_store.run import make_index

ROOT = Path(__file__).resolve().parents[2]
TOOL = ROOT / "tools/shard_content/run.py"
HEADLESS = ROOT / "tools/asset_store/headless/bin/Debug/net8.0/StoreSmoke.dll"


class Quiet(SimpleHTTPRequestHandler):
    def log_message(self, *args):
        pass


def serve(test, folder):
    server = ThreadingHTTPServer(("127.0.0.1", 0), functools.partial(Quiet, directory=str(folder)))
    threading.Thread(target=server.serve_forever, daemon=True).start()
    test.addCleanup(server.server_close)
    test.addCleanup(server.shutdown)
    return f"http://127.0.0.1:{server.server_port}/"


class ShardContentTests(unittest.TestCase):
    @classmethod
    def setUpClass(cls):
        cls.temp = tempfile.TemporaryDirectory()
        cls.root = Path(cls.temp.name)
        cls.public = catalogue.keygen(cls.root / "catalogue.key")
        secret = catalogue.read_secret(cls.root / "catalogue.key")
        cls.store = cls.root / "cdn"
        content_examples.build(cls.root / "examples", cls.store)
        make_index(cls.store, (secret, {"id": "test-shard-packs", "title": "Test shard packs"}, ""))

    @classmethod
    def tearDownClass(cls):
        cls.temp.cleanup()

    def deploy(self, shard, url, *extra, pack="sample-content-combined", bind="sample-content-combined:stone=static:6001", ok=True):
        r = subprocess.run([sys.executable, str(TOOL), "deploy", "--name", "Test Shard", "--catalogue", url,
                            "--pack", pack, "--version", "1.0.0", "--bind", bind,
                            "--shard-dir", str(shard), "--work", str(shard.parent / "work"), *extra],
                           capture_output=True, text=True)
        self.assertEqual(r.returncode == 0, ok, r.stdout + r.stderr)
        return r.stdout + r.stderr

    def prepare(self, url, client, *expect):
        return subprocess.run(["dotnet", str(HEADLESS), "shard-content", "prepare", url, str(client), *expect],
                              capture_output=True, text=True)

    def test_deploy_then_a_client_installs_what_the_shard_runs(self):
        with tempfile.TemporaryDirectory() as temp:
            shard = Path(temp) / "shard"
            (shard / "Data").mkdir(parents=True)
            url = serve(self, self.store)
            out = self.deploy(shard, url, "--scripts", "forbidden")
            self.assertIn(ed25519.fingerprint(self.public), out)

            export = json.loads((shard / "Data/GUO/server-content.json").read_text(encoding="utf-8"))
            self.assertEqual(export["schema"], "guo/server-content@1")
            self.assertEqual([(i["identity"], i["graphic"]) for i in export["items"]], [("sample-content-combined:stone-item", 6001)])
            descriptor = json.loads((shard / "Data/GUO/public/shard-content.json").read_text(encoding="utf-8"))
            self.assertEqual(descriptor["schema"], "guo/shard-content@1")
            self.assertEqual(descriptor["catalogues"], [{"url": url, "key": ed25519.encode(self.public)}])
            self.assertEqual(descriptor["lock"]["identity_hash"], export["identity_hash"])
            self.assertEqual(descriptor["scripts"], "forbidden")

            # A player's client: fetches the descriptor, approves the named key, installs, verifies, locks.
            published = serve(self, shard / "Data/GUO/public")
            client = Path(temp) / "client"
            r = self.prepare(published + "shard-content.json", client)
            self.assertEqual(r.returncode, 0, r.stdout + r.stderr)
            self.assertIn("PASS prepare: " + export["identity_hash"], r.stdout)
            self.assertIn("Script packs are off", r.stdout)
            lock = json.loads((client / ".shard-content" / (export["identity_hash"][:16] + ".json")).read_text(encoding="utf-8"))
            self.assertEqual(lock["bindings"], {"sample-content-combined:stone": {"type": "static", "id": 6001}})
            trust = json.loads((client / ".catalogues.json").read_text(encoding="utf-8"))
            self.assertEqual([r["key"] for r in trust if r["url"] == url], [ed25519.encode(self.public)])
            # Again: already approved and installed, still verifies.
            r = self.prepare(published + "shard-content.json", client)
            self.assertEqual(r.returncode, 0, r.stdout + r.stderr)

    def test_deploy_refuses_a_lock_the_client_could_not_mount(self):
        # sample-content-server needs sample-content-art, whose eleven other client components get no slot.
        with tempfile.TemporaryDirectory() as temp:
            shard = Path(temp) / "shard"
            (shard / "Data").mkdir(parents=True)
            out = self.deploy(shard, serve(self, self.store), pack="sample-content-server",
                              bind="sample-content-art:stone=static:6001", ok=False)
            self.assertIn("no numeric slot", out)
            self.assertFalse((shard / "Data/GUO/server-content.json").exists())
            self.assertFalse((shard / "Data/GUO/public/shard-content.json").exists())

    def test_a_descriptor_whose_lock_leaves_components_unbound_is_refused(self):
        with tempfile.TemporaryDirectory() as temp:
            url = self.descriptor_variant(temp, lambda v: v["lock"]["bindings"].clear())
            r = self.prepare(url, Path(temp) / "client", "no numeric slot")
            self.assertEqual(r.returncode, 0, r.stdout + r.stderr)
            self.assertIn("PASS refused", r.stdout)
            self.assertFalse((Path(temp) / "client/.shard-content").exists())

    def descriptor_variant(self, temp, change):
        shard = Path(temp) / "shard"
        (shard / "Data").mkdir(parents=True, exist_ok=True)
        url = serve(self, self.store)
        self.deploy(shard, url)
        path = shard / "Data/GUO/public/shard-content.json"
        value = json.loads(path.read_text(encoding="utf-8"))
        change(value)
        path.write_text(json.dumps(value), encoding="utf-8")
        return serve(self, path.parent) + "shard-content.json"

    def test_a_catalogue_signed_with_another_key_is_refused(self):
        other = ed25519.encode(ed25519.public_key(bytes(range(32))))
        with tempfile.TemporaryDirectory() as temp:
            url = self.descriptor_variant(temp, lambda v: v["catalogues"][0].update(key=other))
            r = self.prepare(url, Path(temp) / "client", "approve")
            self.assertEqual(r.returncode, 0, r.stdout + r.stderr)
            self.assertIn("PASS refused", r.stdout)
            self.assertFalse((Path(temp) / "client/.shard-content").exists())

    def test_a_signed_catalogue_claimed_unsigned_is_refused(self):
        with tempfile.TemporaryDirectory() as temp:
            url = self.descriptor_variant(temp, lambda v: v["catalogues"][0].pop("key"))
            r = self.prepare(url, Path(temp) / "client", "approve")
            self.assertEqual(r.returncode, 0, r.stdout + r.stderr)
            self.assertIn("PASS refused", r.stdout)

    def test_a_lock_that_does_not_match_the_packs_is_refused(self):
        with tempfile.TemporaryDirectory() as temp:
            url = self.descriptor_variant(temp, lambda v: v["lock"].update(identity_hash="0" * 64))
            r = self.prepare(url, Path(temp) / "client", "no longer matches")
            self.assertEqual(r.returncode, 0, r.stdout + r.stderr)
            self.assertIn("PASS refused", r.stdout)

    def test_an_unsigned_https_catalogue_is_refused(self):
        with tempfile.TemporaryDirectory() as temp:
            def remote(v):
                v["catalogues"] = [{"url": "https://packs.example.com/"}]
            url = self.descriptor_variant(temp, remote)
            r = self.prepare(url, Path(temp) / "client", "may be unsigned")
            self.assertEqual(r.returncode, 0, r.stdout + r.stderr)
            self.assertIn("PASS refused", r.stdout)

    def test_plain_http_to_a_remote_catalogue_is_refused(self):
        with tempfile.TemporaryDirectory() as temp:
            def remote(v):
                v["catalogues"] = [{"url": "http://packs.example.com/"}]
            url = self.descriptor_variant(temp, remote)
            r = self.prepare(url, Path(temp) / "client", "must be HTTPS")
            self.assertEqual(r.returncode, 0, r.stdout + r.stderr)
            self.assertIn("PASS refused", r.stdout)


if __name__ == "__main__":
    unittest.main()
