"""Cross-language v2 contract checks using the real portable C# validator."""
import copy
import hashlib
import json
from pathlib import Path
import subprocess
import sys
import tempfile
import unittest

sys.path.insert(0, str(Path(__file__).resolve().parents[1]))
from asset_store.pack import parse_manifest


def example():
    return dict(schema="guo/store-pack@2", id="example-content", version="1.0.0",
                kind="content", target="combined", title="Example", author="GUO",
                licence="CC0-1.0", min_profile_version=6, preview="preview.png",
                files={"preview.png": hashlib.sha256(b"preview").hexdigest(),
                       "content.json": hashlib.sha256(b"{}").hexdigest()},
                dependencies={}, components=[dict(id="tile", type="static", target="shared", entry="content.json")])


class Contract(unittest.TestCase):
    def test_cross_language(self):
        cases = {"valid": (example(), True)}
        for key, value in [("target", "invalid"), ("dependencies", None), ("components", []),
                           ("kind", "background")]:
            m = example(); m[key] = value
            cases[key] = (m, False)
        for key, value in [("id", "Bad"), ("type", "dll"), ("target", "other"),
                           ("entry", "missing.json"), ("references", ["missing:tile"]),
                           ("references", ["example-content:absent"])]:
            m = example(); m["components"][0][key] = value
            cases[f"component-{key}-{len(cases)}"] = (m, False)
        m = example(); m["components"] *= 2
        cases["duplicate"] = (m, False)
        m = example(); m["target"] = "client"; m["components"][0]["target"] = "server"
        cases["wrong-side"] = (m, False)
        m = example(); m["components"][0].update(type="script", language="razor-ce", runtime="guo-razor", runtime_version="0.1.0", capabilities=["client.message"])
        cases["script-json-entry"] = (copy.deepcopy(m), False)
        m["components"][0]["entry"] = "script.razor"
        m["files"]["script.razor"] = hashlib.sha256(b"sysmsg hello").hexdigest()
        cases["script"] = (m, True)
        bad = copy.deepcopy(m); bad["files"]["hidden.razor"] = bad["files"]["script.razor"]
        cases["undeclared-script"] = (bad, False)
        bad = copy.deepcopy(m); bad["components"][0]["capabilities"] *= 2
        cases["duplicate-capability"] = (bad, False)
        bad = example(); bad["components"][0]["execute_on_install"] = True
        cases["unknown-field"] = (bad, False)
        root = Path(__file__).resolve().parents[2]
        with tempfile.TemporaryDirectory(dir=root / "build") as temp:
            folder = Path(temp)
            for name, (manifest, accepted) in cases.items():
                raw = json.dumps(manifest).encode()
                (folder / (name + ".json")).write_bytes(raw)
                try:
                    parse_manifest(raw); actual = True
                except (ValueError, TypeError):
                    actual = False
                self.assertEqual(actual, accepted, name + " Python")
            result = folder.parent / (folder.name + "-result.json")
            try:
                subprocess.run(["dotnet", str(root / "tools/asset_store/headless/bin/Debug/net8.0/StoreSmoke.dll"), "corpus", str(folder), str(result)], check=True)
                reported = json.loads(result.read_text())
                for name, (_, accepted) in cases.items():
                    self.assertEqual(reported[name + ".json"]["result"], "accept" if accepted else "reject", name + " C#")
            finally:
                result.unlink(missing_ok=True)


if __name__ == "__main__":
    unittest.main()
