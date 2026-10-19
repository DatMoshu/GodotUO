"""Mod components (MOD-03, ADR-0038 amendment): the Python publisher and the C# installer agree.

Python signs; the C# client verifies the same signature, refuses the same packs, and runs the
approval lifecycle its loader relies on. The DLL here is placeholder bytes: the Store never
loads a mod, only the client's loader does (tools/network_tests and ModsProbe cover that).
"""
import copy
import hashlib
import json
from pathlib import Path
import subprocess
import sys
import tempfile
import unittest
import zipfile

sys.path.insert(0, str(Path(__file__).resolve().parents[1]))
from asset_store import mods
from asset_store.pack import parse_manifest

ROOT = Path(__file__).resolve().parents[2]
HEADLESS = ROOT / "tools/asset_store/headless/bin/Debug/net8.0/StoreSmoke.dll"
SECRET = bytes(range(32))  # a test key; never a real publisher's

MOD_JSON = json.dumps({
    "schema": "guo/mod@1", "id": "announce", "version": "1.0.0", "title": "Shard announcements",
    "author": "GUO samples", "abi": 1, "assembly": "Announce.dll", "entry": "GuoMods.Announce.AnnounceMod",
    "shards": ["127.0.0.1"], "packets": [{"id": "0xF9", "length": -1, "override": False}],
}).encode()
DLL = b"MZ placeholder, never loaded by the Store"
PREVIEW = b"preview"


def files(mod_json=MOD_JSON, dll=DLL):
    return {"preview.png": PREVIEW, "mods/announce/mod.json": mod_json, "mods/announce/Announce.dll": dll}


def manifest(payload=None, sign=True):
    payload = payload or files()
    m = dict(schema="guo/store-pack@2", id="announce-mod", version="1.0.0", kind="content", target="client",
             title="Shard announcements", author="GUO samples", licence="CC0-1.0", min_profile_version=6,
             preview="preview.png", files={k: hashlib.sha256(v).hexdigest() for k, v in payload.items()},
             dependencies={}, components=[dict(id="announce", type="mod", target="client", entry="mods/announce/mod.json",
                                               assembly="mods/announce/Announce.dll", abi=1,
                                               packets=[{"id": "0xF9", "length": -1, "override": False}], gumps=[])])
    if sign:
        mods.sign(m, "announce", SECRET)
    return m


def accepted(m):
    try:
        parse_manifest(json.dumps(m).encode())
        return True
    except (ValueError, TypeError):
        return False


class ModComponents(unittest.TestCase):
    @classmethod
    def setUpClass(cls):
        project = ROOT / "tools/asset_store/headless/StoreSmoke.csproj"
        built = subprocess.run(["dotnet", "build", str(project), "-v", "q", "-nologo"], capture_output=True, text=True)
        if built.returncode:
            raise RuntimeError("the headless store checker does not build: " + built.stdout[-2000:])

    def cases(self):
        cases = {"signed": (manifest(), True)}

        def bad(name, change):
            m = manifest()
            change(m)
            cases[name] = (m, False)

        cases["unsigned"] = (manifest(sign=False), False)
        def tamper(m):
            sig = m["components"][0]["signature"]
            i = len("ed25519:") + 10
            m["components"][0]["signature"] = sig[:i] + ("A" if sig[i] != "A" else "B") + sig[i + 1:]
        bad("tampered-signature", tamper)
        bad("signed-other-hash", lambda m: m["files"].update({"mods/announce/Announce.dll": hashlib.sha256(b"other").hexdigest()}))
        bad("server-target", lambda m: m["components"][0].update(target="shared"))
        bad("gumps", lambda m: m["components"][0].update(gumps=[0x1234]))
        bad("abi-2", lambda m: m["components"][0].update(abi=2))
        bad("unknown-field", lambda m: m["components"][0].update(native=True))
        bad("reserved-subcommand", lambda m: m["components"][0].update(packets=[{"id": "0xBF", "subcommand": "0x4755"}]))
        bad("assembly-elsewhere", lambda m: (m["files"].update({"other/Announce.dll": m["files"].pop("mods/announce/Announce.dll")}),
                                             m["components"][0].update(assembly="other/Announce.dll")))
        bad("stray-dll", lambda m: m["files"].update({"mods/announce/Helper.dll": hashlib.sha256(b"x").hexdigest()}))
        bad("mod-field-on-static", lambda m: m["components"].append(dict(id="tile", type="static", target="client",
                                                                          entry="mods/announce/mod.json", assembly="x.dll")))
        v1 = dict(schema="guo/store-pack@1", id="old-mod", version="1.0.0", kind="theme", title="Old", author="GUO",
                  licence="CC0-1.0", min_profile_version=1, preview="preview.png",
                  files={"preview.png": hashlib.sha256(PREVIEW).hexdigest(), "theme.json": hashlib.sha256(b"{}").hexdigest(),
                         "Mod.dll": hashlib.sha256(DLL).hexdigest()})
        cases["v1-with-dll"] = (v1, False)
        return cases

    def test_python_and_csharp_agree(self):
        cases = self.cases()
        with tempfile.TemporaryDirectory(dir=ROOT / "build") as temp:
            folder = Path(temp) / "corpus"
            folder.mkdir()
            for name, (m, expected) in cases.items():
                self.assertEqual(accepted(m), expected, name + " Python")
                (folder / (name + ".json")).write_text(json.dumps(m))
            result = Path(temp) / "result.json"
            subprocess.run(["dotnet", str(HEADLESS), "corpus", str(folder), str(result)], check=True)
            reported = json.loads(result.read_text())
            for name, (_, expected) in cases.items():
                self.assertEqual(reported[name + ".json"]["result"], "accept" if expected else "reject", name + " C#")

    def test_payload_must_match_mod_json(self):
        def require(condition, message):
            if not condition:
                raise ValueError(message)
        m = manifest()
        payload = files()
        mods.check_payload(m, payload.__getitem__, require)
        other = json.loads(MOD_JSON)
        other["packets"] = [{"id": "0xF9", "length": 9}]
        changed = files(mod_json=json.dumps(other).encode())
        with self.assertRaisesRegex(ValueError, "different packets"):
            mods.check_payload(manifest(changed), changed.__getitem__, require)

    def test_install_and_approval_lifecycle(self):
        with tempfile.TemporaryDirectory(dir=ROOT / "build") as temp:
            zip_path = Path(temp) / "announce-mod.zip"
            payload = files()
            with zipfile.ZipFile(zip_path, "w") as z:
                z.writestr("manifest.json", json.dumps(manifest(payload)))
                for name, data in payload.items():
                    z.writestr(name, data)
            store = Path(temp) / "store"
            store.mkdir()
            out = subprocess.run(["dotnet", str(HEADLESS), "mods", str(zip_path), str(store)], capture_output=True, text=True)
            self.assertEqual(out.returncode, 0, out.stdout + out.stderr)
            self.assertIn("PASS mods", out.stdout)

    def test_csharp_refuses_payload_mismatch_at_install(self):
        other = json.loads(MOD_JSON)
        other["assembly"] = "Other.dll"
        payload = files(mod_json=json.dumps(other).encode())
        with tempfile.TemporaryDirectory(dir=ROOT / "build") as temp:
            zip_path = Path(temp) / "bad.zip"
            with zipfile.ZipFile(zip_path, "w") as z:
                z.writestr("manifest.json", json.dumps(manifest(payload)))
                for name, data in payload.items():
                    z.writestr(name, data)
            store = Path(temp) / "store"
            store.mkdir()
            out = subprocess.run(["dotnet", str(HEADLESS), "mods", str(zip_path), str(store)], capture_output=True, text=True)
            self.assertNotEqual(out.returncode, 0)
            self.assertIn("different assemblies", out.stdout + out.stderr)


if __name__ == "__main__":
    unittest.main()
