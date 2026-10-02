"""Publish, HTTP serve, real C# headless install/hash-check/uninstall. Exit 0/1."""
import argparse
import base64
import hashlib
import json
from pathlib import Path
import shutil
import subprocess
import sys
import tempfile
import threading
import zipfile

from run import publish, server
from razor_scripts import make_pack


def main():
    parser = argparse.ArgumentParser(description=__doc__)
    parser.add_argument("--dotnet", default=shutil.which("dotnet") or "dotnet")
    args = parser.parse_args()
    try:
        with tempfile.TemporaryDirectory(prefix="guo-store-smoke-") as temporary:
            root = Path(temporary)
            image = base64.b64decode("iVBORw0KGgoAAAANSUhEUgAAAAEAAAABCAQAAAC1HAwCAAAAC0lEQVR42mP8/x8AAwMCAO+aX1cAAAAASUVORK5CYII=")
            manifest = dict(schema="guo/store-pack@1", id="smoke-background", version="1.0.0", kind="background", title="Smoke background", author="GUO", licence="CC0-1.0", min_profile_version=6, preview="still.png", files={"still.png": hashlib.sha256(image).hexdigest()})
            pack = root / "smoke.zip"
            with zipfile.ZipFile(pack, "w") as z:
                z.writestr("manifest.json", json.dumps(manifest))
                z.writestr("still.png", image)
            publish(pack, root / "cdn")
            # A screensaver: one .ogv loop, needs profile v11 (data_formats 12).
            loop = b"not a real theora stream; the installer checks bytes, not codecs"
            saver = dict(manifest, id="smoke-screensaver", kind="screensaver", title="Smoke screensaver", min_profile_version=11,
                         files={"still.png": hashlib.sha256(image).hexdigest(), "loop.ogv": hashlib.sha256(loop).hexdigest()})
            saver_pack = root / "saver.zip"
            with zipfile.ZipFile(saver_pack, "w") as z:
                z.writestr("manifest.json", json.dumps(saver))
                z.writestr("still.png", image)
                z.writestr("loop.ogv", loop)
            publish(saver_pack, root / "cdn")
            publish(make_pack(root / "scripts-v1.zip"), root / "cdn")
            publish(make_pack(root / "scripts-v2.zip", "1.1.0"), root / "cdn")
            with server(root / "cdn", port=0) as httpd:
                thread = threading.Thread(target=httpd.serve_forever, daemon=True)
                thread.start()
                try:
                    project = Path(__file__).parent / "headless/StoreSmoke.csproj"
                    result = subprocess.run([args.dotnet, "run", "--project", str(project), "--", f"http://127.0.0.1:{httpd.server_port}", str(root / "installed")], timeout=120)
                    return 0 if result.returncode == 0 else 1
                finally:
                    httpd.shutdown()
                    thread.join(timeout=5)
    except (OSError, ValueError, subprocess.TimeoutExpired) as e:
        print("FAIL:", e, file=sys.stderr)
        return 1


if __name__ == "__main__":
    raise SystemExit(main())
