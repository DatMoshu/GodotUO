"""HTTP publication -> catalogue -> dependency download -> verified C# install."""
from pathlib import Path
import subprocess
import sys
import tempfile
import threading

sys.path.insert(0, str(Path(__file__).resolve().parents[1]))
from asset_store.content_examples import build
from asset_store.run import server


def main():
    root = Path(__file__).resolve().parents[2]
    with tempfile.TemporaryDirectory(prefix="content-http-", dir=root / "build") as temporary:
        work = Path(temporary)
        build(work / "examples", work / "cdn")
        with server(work / "cdn", port=0) as http:
            thread = threading.Thread(target=http.serve_forever, daemon=True)
            thread.start()
            try:
                command = ["dotnet", str(root / "tools/asset_store/headless/bin/Debug/net8.0/StoreSmoke.dll"), "install-content", f"http://127.0.0.1:{http.server_port}", str(work / "installed"), "sample-content-server"]
                subprocess.run(command, check=True, timeout=90)
                assert (work / "installed/sample-content-art/1.0.0/stone.png").is_file()
                assert (work / "installed/sample-content-server/1.0.0/item.json").is_file()
                subprocess.run(command, check=True, timeout=90)  # immutable idempotent repeat
            finally:
                http.shutdown(); thread.join(timeout=5)
    print("PASS: content publication, HTTP download, exact dependency installation and verified repeat")


if __name__ == "__main__":
    main()
