"""A local signed test catalogue for the editor smoke's UO Store stage.

    python tools/editor_smoke/store_fixture.py OUT_DIR

Builds the starter content packs (tools/asset_store/content_examples.py) into OUT_DIR/cdn, signs the index
with a throwaway key made in OUT_DIR, writes a malformed pack (a renamed executable) to OUT_DIR/bad.zip,
prints one line `FIXTURE {json}` with the catalogue's address, key fingerprint and the folder of
sample ZIPs, then serves the catalogue on a loopback port until it is killed. Nothing is written
outside OUT_DIR, and no key of the project's is used.
"""
from __future__ import annotations

import functools
import hashlib
import json
import sys
import zipfile
from http.server import SimpleHTTPRequestHandler, ThreadingHTTPServer
from pathlib import Path

sys.path.insert(0, str(Path(__file__).resolve().parents[1]))

from asset_store import catalogue, content_examples, ed25519  # noqa: E402
from asset_store.run import make_index  # noqa: E402


class Quiet(SimpleHTTPRequestHandler):
    def log_message(self, *args):
        pass


def main() -> int:
    out = Path(sys.argv[1]).resolve()
    out.mkdir(parents=True, exist_ok=True)
    key = out / "catalogue.key"
    public = catalogue.keygen(key)
    examples = out / "examples"
    cdn = out / "cdn"
    content_examples.build(examples, cdn)
    make_index(cdn, (catalogue.read_secret(key), {"id": "editor-smoke-packs", "title": "Editor smoke packs"}, ""))

    png = content_examples.png(8, 8, (9, 9, 9, 255))
    manifest = dict(schema="guo/store-pack@1", id="smoke-bad-pack", version="1.0.0", kind="background", title="Smoke bad pack",
                    author="Smoke", licence="CC0-1.0", min_profile_version=6, preview="still.png",
                    files={"still.png": hashlib.sha256(png).hexdigest(), "tool.png": hashlib.sha256(b"MZ not an image").hexdigest()})
    with zipfile.ZipFile(out / "bad.zip", "w") as z:
        z.writestr("manifest.json", json.dumps(manifest))
        z.writestr("still.png", png)
        z.writestr("tool.png", b"MZ not an image")

    server = ThreadingHTTPServer(("127.0.0.1", 0), functools.partial(Quiet, directory=str(cdn)))
    print("FIXTURE " + json.dumps({"url": f"http://127.0.0.1:{server.server_port}/", "key": ed25519.encode(public),
                                   "fingerprint": ed25519.fingerprint(public), "zips": str(examples), "bad": str(out / "bad.zip")}), flush=True)
    try:
        server.serve_forever()
    except KeyboardInterrupt:
        pass
    return 0


if __name__ == "__main__":
    raise SystemExit(main())
