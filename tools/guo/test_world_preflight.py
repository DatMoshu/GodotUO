import json
import tempfile
import unittest
from pathlib import Path
from guo.world_preflight import check_fingerprint, load_static_overrides

class PreflightTests(unittest.TestCase):
    def test_missing_and_mismatched_base_refused(self):
        for expected in (None, '', 'other'):
            with self.assertRaises(ValueError):
                check_fingerprint(expected, 'correct')
        check_fingerprint('correct', 'correct')

    def test_partial_overlays_keep_height_and_convert_flags(self):
        with tempfile.TemporaryDirectory() as tmp:
            dirs = [Path(tmp)/'a', Path(tmp)/'b']
            for d, row in zip(dirs, [{'flags':'0x200','height':5}, {'flags':'0x10000200'}]):
                (d/'assets').mkdir(parents=True)
                (d/'assets/tiledata.json').write_text(json.dumps({'0xE800':row}))
            self.assertEqual(load_static_overrides(dirs)[0xe800], {'flags':0x10000200,'height':5})
            (dirs[1]/'assets/tiledata.json').write_text('{"0xFFFF": {}}')
            with self.assertRaises(ValueError):load_static_overrides(dirs)

if __name__ == '__main__':unittest.main()
