"""The operator's login offer: bounded JSON, wire framing and CLI output."""
import json
from pathlib import Path
import subprocess
import sys
import tempfile
import unittest

sys.path.insert(0, str(Path(__file__).resolve().parents[1]))
from shard_content import offer


class OfferTests(unittest.TestCase):
    def test_offer_preserves_descriptor_and_wire_length(self):
        descriptor = {"schema": "guo/shard-content@1", "lock": {"pack": "test-pack", "version": "1.0.0"}}
        value = offer.build("Example", descriptor)
        raw = offer.encode(value)
        wire = offer.packet(raw)
        self.assertEqual(wire[:1], b"\xbf")
        self.assertEqual(int.from_bytes(wire[1:3], "big"), len(wire))
        self.assertEqual(wire[3:6], b"\x47\x55\x01")
        self.assertEqual(json.loads(wire[6:])["content"], descriptor)
        self.assertEqual(value["packs"][0]["id"], "test-pack")

    def test_offer_refuses_oversize_and_invalid_fields(self):
        for name in ("", "bad\nname", "x" * 201):
            with self.subTest(name=name), self.assertRaises(ValueError):
                offer.build(name, None, features={"water": {}})
        for features in ({"Bad Name": {}}, {"water": []}, {"water": {"pad": "x" * 4096}},
                         {f"feature-{i}": {} for i in range(33)}):
            with self.subTest(features=list(features)), self.assertRaises(ValueError):
                offer.build("Example", None, features=features)
        with self.assertRaises(ValueError):
            offer.build("Example", None)
        with self.assertRaises(ValueError):
            offer.packet(b"x" * (offer.MAX_JSON + 1))
        with self.assertRaises(ValueError):
            offer.encode({"pad": "x" * offer.MAX_JSON})
        for features in ({"con": {}}, {"water": {"value": float("nan")}}):
            with self.subTest(features=list(features)), self.assertRaises(ValueError):
                offer.build("Example", None, features=features)
        for version in ("1.0", "1.0.0.0", "2147483648.0.0"):
            with self.subTest(version=version), self.assertRaises(ValueError):
                offer.build("Example", {"schema": "guo/shard-content@1", "lock": {"pack": "test-pack", "version": version}})

    def test_features_only_cli_writes_offer_and_preserves_it_on_error(self):
        with tempfile.TemporaryDirectory() as temp:
            root = Path(temp)
            feature = root / "water.json"
            feature.write_text('{"allowed":["classic"],"default":"classic"}', encoding="utf-8")
            command = [sys.executable, str(Path(__file__).with_name("run.py")), "offer", "--name", "Example",
                       "--shard-dir", temp, "--features-only", "--feature", f"water={feature}"]
            result = subprocess.run(command, capture_output=True, text=True)
            self.assertEqual(result.returncode, 0, result.stdout + result.stderr)
            path = root / "Data/GUO/shard-offer.json"
            before = path.read_bytes()
            self.assertNotIn("content", json.loads(before))
            self.assertEqual(json.loads(before)["features"]["water"]["default"], "classic")
            result = subprocess.run(command + ["--feature", f"water={feature}"], capture_output=True, text=True)
            self.assertEqual(result.returncode, 2, result.stdout + result.stderr)
            self.assertEqual(path.read_bytes(), before)


if __name__ == "__main__":
    unittest.main()
